using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;

/// <summary>
/// 自建后端的授权闸门：给请求带上构建标识与激活凭证，并在服务端回「需要激活」时弹窗、
/// 成功后自动重放这一次请求。
///
/// <para>
/// 服务端约定（见 <c>docs/正版校验与服务端接入.md</c>）：未授权时返回 401/403，
/// 正文为 <c>{ "error": "人话原因", "code": "csh_license_required" }</c>；
/// 放行时若签发了新凭证，会带 <c>X-CSH-Lic</c>（可选 <c>X-CSH-Lic-Exp</c>，Unix 秒）响应头。
/// </para>
/// <para>
/// ⚠️ 用户放弃激活时**原样返回服务端那条响应**，不伪造成功 —— 各调用方本来就会把
///    <c>error</c> 字段当作业务失败显示，正好把服务端的原因说给用户听。
/// </para>
/// </summary>
internal sealed class ClientAuthHandler : DelegatingHandler
{
    private const string BuildHeader = "X-CSH-Build";
    private const string SignatureHeader = "X-CSH-Sig";
    private const string DeviceHeader = "X-CSH-Device";
    private const string DeviceIdHeader = DeviceIdentity.Header;
    private const string VersionHeader = "X-CSH-Ver";
    private const string LicenseHeader = "X-CSH-Lic";
    private const string TokenExpiryHeader = "X-CSH-Lic-Exp";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (!OfficialApi.IsOfficialHost(request.RequestUri))
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        // 先读缓存一份正文，激活成功后要原样重放这次请求
        var body = await BufferAsync(request, ct).ConfigureAwait(false);
        Authorize(request);

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        Capture(response);

        // ⚠️ HttpClient.Timeout 从请求发起即计时，而激活对话框可能要几十秒才输完；
        //    届时外层 ct 已被超时取消。挑战判定与重放都不再沿用这个 ct：
        //    读响应用 CancellationToken.None（响应已到达），重放也用独立令牌，
        //    避免「刚激活成功就报超时 / 任务已取消」。弹窗耗时因此不占本次请求的超时预算。
        var challenge = await ReadChallengeAsync(response, CancellationToken.None).ConfigureAwait(false);
        if (challenge is null) return response;

        AppLog.Warn("license", $"服务端要求激活｜状态码={challenge.Value.Code}｜路径={request.RequestUri?.AbsolutePath}");

        if (!await LicenseGate.EnsureAsync(challenge.Value.Message).ConfigureAwait(false))
            return response;

        var retry = Clone(request, body);
        if (retry is null) return response;

        try
        {
            Authorize(retry);
            var second = await base.SendAsync(retry, CancellationToken.None).ConfigureAwait(false);
            Capture(second);
            await ReadChallengeAsync(second, CancellationToken.None).ConfigureAwait(false);
            return second;
        }
        catch (Exception ex)
        {
            // 重放失败一律返回原「需要激活」响应，由业务层显示服务端原因，不把异常抛给调用方
            AppLog.Fail("license", ex, $"激活后重放请求｜路径={request.RequestUri?.AbsolutePath}");
            return response;
        }
        finally
        {
            retry.Dispose();
        }
    }

    private static void Authorize(HttpRequestMessage request)
    {
        var headers = request.Headers;
        headers.Remove(BuildHeader);
        headers.Remove(SignatureHeader);
        headers.Remove(DeviceHeader);
        headers.Remove(DeviceIdHeader);
        headers.Remove(VersionHeader);
        headers.Remove(LicenseHeader);

        headers.TryAddWithoutValidation(BuildHeader, BuildIdentity.BuildId);
        if (BuildIdentity.IsOfficial)
            headers.TryAddWithoutValidation(SignatureHeader, BuildIdentity.Signature);

        headers.TryAddWithoutValidation(DeviceHeader, DeviceFingerprint.Value);
        headers.TryAddWithoutValidation(VersionHeader, ShellConfig.ShellVersion);

        // 硬件锚定的设备标识（Win10+ 用系统标识，Win7 用安装码 + 卷序列号）。
        // 服务端拿它判信誉与封禁 —— 比单纯按 IP 限流准得多（同一出口 IP 后面常是一整间机房）。
        var deviceId = DeviceIdentity.Value;
        if (deviceId.Length > 0) headers.TryAddWithoutValidation(DeviceIdHeader, deviceId);

        var token = LicenseStore.Token;
        if (token.Length > 0) headers.TryAddWithoutValidation(LicenseHeader, token);
    }

    private static void Capture(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(LicenseHeader, out var values)) return;

        var token = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token) || token == LicenseStore.Token) return;

        long expires = 0;
        if (response.Headers.TryGetValues(TokenExpiryHeader, out var expiryValues))
            long.TryParse(expiryValues.FirstOrDefault(), out expires);

        LicenseStore.Save(token.Trim(), expires);
    }

    /// <summary>
    /// 这条响应是不是「需要激活」。是则返回服务端给的原因，同时把正文还原回去供调用方读取。
    /// </summary>
    private static async Task<(string Code, string Message)?> ReadChallengeAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            return null;

        byte[] bytes;
        try
        {
            bytes = response.Content is null
                ? Array.Empty<byte>()
                : await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (response.Content is not null)
        {
            var mediaType = response.Content.Headers.ContentType;
            var restored = new ByteArrayContent(bytes);
            if (mediaType is not null) restored.Headers.ContentType = mediaType;
            response.Content.Dispose();
            response.Content = restored;
        }

        if (bytes.Length == 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("code", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.String) return null;

            var code = codeElement.GetString() ?? "";
            if (!code.StartsWith("csh_license", StringComparison.OrdinalIgnoreCase)) return null;

            var message = root.TryGetProperty("error", out var errorElement) &&
                          errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()!
                : "在线服务需要激活后才能使用。";

            return (code, message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> BufferAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null) return Array.Empty<byte>();
        if (request.Content is not (ByteArrayContent or StringContent)) return null;

        try
        {
            return await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static HttpRequestMessage? Clone(HttpRequestMessage request, byte[]? body)
    {
        if (body is null) return null;

        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (body.Length > 0)
        {
            var content = new ByteArrayContent(body);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            clone.Content = content;
        }

        return clone;
    }
}
