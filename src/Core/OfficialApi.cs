using System;
using System.Net.Http;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 自建后端专用的 <see cref="HttpClient"/> 工厂。
///
/// <para>
/// 所有打到自己服务端的请求都必须经过 <c>Services.License.ClientAuthHandler</c> —— 它负责带上
/// 构建标识与激活凭证，并在服务端回「需要激活」时就地弹窗、成功后自动重放这次请求。
/// 分散 new 出来的 <see cref="HttpClient"/> 会绕过这道闸门，所以统一从这里取。
/// </para>
/// <para>
/// 第三方域名（GitHub、字典、AI 服务商、OSS）不要用这里：那些请求与授权无关。
/// </para>
/// </summary>
public static class OfficialApi
{
    /// <summary>除 <see cref="ServiceEndpoints.All"/> 之外还要带凭证的域名（加速节点代签入口）。</summary>
    private static readonly string[] ExtraHosts =
    {
        "sign.classsoftwarehub.us.ci",
    };

    /// <summary>这个地址是不是自家服务端（决定要不要带凭证、要不要处理激活）。</summary>
    public static bool IsOfficialHost(Uri? uri)
    {
        if (uri is null) return false;
        var host = uri.Host;

        foreach (var endpoint in ServiceEndpoints.All)
        {
            if (host.Equals(new Uri(endpoint).Host, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var extra in ExtraHosts)
        {
            if (host.Equals(extra, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>建一个带授权处理的客户端。<paramref name="timeoutSeconds"/> 为 0 表示交给调用方用取消令牌控制。</summary>
    public static HttpClient Create(bool allowRedirect = true, bool decompress = false,
                                    int timeoutSeconds = 20, int maxConnectionsPerServer = 0)
    {
        var inner = HttpTls.Handler(allowRedirect, decompress, maxConnectionsPerServer);
        var client = new HttpClient(new Services.License.ClientAuthHandler { InnerHandler = inner })
        {
            Timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : System.Threading.Timeout.InfiniteTimeSpan,
        };
        return client;
    }
}
