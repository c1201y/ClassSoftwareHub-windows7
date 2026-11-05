using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;

/// <summary>
/// 激活码兑换：<c>POST {入口}/api/license/activate</c>。
///
/// <para>
/// 请求体 <c>{ "code": "…", "device": "…", "build": "…", "version": "…" }</c>；
/// 成功回 <c>{ "success": true, "token": "…", "expiresAt": 1767225600 }</c>，
/// 失败回 <c>{ "error": "人话原因" }</c>（与投稿那几条路由同一套形状）。
/// </para>
/// <para>
/// 兑换本身不能要求授权（否则死循环），所以这里用独立的 <see cref="HttpClient"/>，
/// 不经过 <see cref="ClientAuthHandler"/>。
/// </para>
/// </summary>
internal static class LicenseClient
{
    internal const string Route = "/api/license/activate";

    /// <summary>激活码本体长度（不含展示用的 <c>CSH-</c> 前缀与连字符）。</summary>
    internal const int CodeLength = 16;

    private const int TimeoutMs = 8000;

    private static readonly HttpClient Http = new(Core.HttpTls.Handler())
    {
        Timeout = TimeSpan.FromSeconds(TimeoutMs / 1000),
    };

    /// <summary>把用户输入的激活码兑换成令牌并落盘。返回 (是否成功, 提示语)。</summary>
    internal static async Task<(bool Ok, string Message)> ActivateAsync(
        string code, CancellationToken ct = default)
    {
        code = Normalize(code);
        if (code.Length != CodeLength)
            return (false, $"激活码应为 {CodeLength} 个字符（不含前缀与连字符），请检查是否漏字符。");

        var payload = JsonSerializer.Serialize(new
        {
            code,
            device = DeviceFingerprint.Value,
            build = BuildIdentity.BuildId,
            version = ShellConfig.ShellVersion,
        });

        var sawServer = false;
        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeoutMs);

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await Http
                    .PostAsync(baseUrl.TrimEnd('/') + Route, content, budget.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    continue;

                sawServer = true;
                var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True &&
                    root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String)
                {
                    var value = token.GetString() ?? "";
                    if (value.Length == 0) return (false, "服务端没有返回有效凭证，请稍后再试。");

                    long expires = 0;
                    if (root.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number)
                        expires = exp.GetInt64();

                    LicenseStore.Save(value, expires, code);
                    ShellConfig.RememberSubmitEndpoint(baseUrl);
                    return (true, "激活成功。");
                }

                var message = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                    ? err.GetString()
                    : null;
                return (false, string.IsNullOrWhiteSpace(message) ? "激活码未被接受。" : message!);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warn("license", $"激活入口不可用｜入口={baseUrl}｜原因={ex.GetType().Name}｜处置=换下一个入口");
            }
        }

        return sawServer
            ? (false, "激活码未被接受，请确认是否已过期或用于其他设备。")
            : (false, "连不上激活服务（网络或地区限制）。请稍后重试，或把设备码发给开发者。");
    }

    /// <summary>
    /// 去掉空白与分隔符、统一大写 —— 用户从聊天窗口复制时常带空格和短横线；
    /// 签发工具给出的展示码带 <c>CSH-</c> 前缀，本体只有 16 位，这里一并剥掉。
    /// </summary>
    internal static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var builder = new StringBuilder(code.Length);
        foreach (var c in code)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToUpperInvariant(c));
        }

        var text = builder.ToString();
        return text.Length == CodeLength + 3 && text.StartsWith("CSH", StringComparison.Ordinal)
            ? text[3..]
            : text;
    }
}
