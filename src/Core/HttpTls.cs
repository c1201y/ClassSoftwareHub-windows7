using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 全工程统一的 TLS 配置（2026-10-27）。
///
/// <para>
/// ⛔ Win7 的 Schannel **默认不协商 TLS 1.2**（要装补丁 + 改注册表才默认开）。.NET 6 的
/// <c>HttpClient</c> 走系统默认协议，于是未打补丁的教室机上 GitHub / 自家接口全部握手失败，
/// 表现就是「检查更新永远网络错误」「投稿永远失败」，而日志里只有一句 SSL 错误。
/// </para>
///
/// <para>
/// 显式指定 <see cref="SslProtocols.Tls12"/> 即可解决：系统支持 TLS 1.2 时一定用得上，
/// 不支持时（极老的 XP 级系统，不在支持范围）会因为握手失败回到原来的错误，
/// 不会比现在更差。⚠️ 不要用 <c>ServicePointManager.SecurityProtocol</c> ——
/// .NET Core 上该开关已废弃且不保证生效。
/// </para>
///
/// <para>
/// TLS 1.3 只在 Win11 / Server 2022 的 Schannel 上有；在 Win7/8/10 的协议列表里写上它，
/// 握手会直接失败。所以按 <see cref="OsInfo"/> 分档，别一股脑全开。
/// </para>
/// </summary>
public static class HttpTls
{
    /// <summary>本机可用的协议集合：TLS 1.2，Win11 上再加 TLS 1.3。</summary>
    public static SslProtocols Protocols =>
        OsInfo.IsWindows11 ? SslProtocols.Tls12 | SslProtocols.Tls13 : SslProtocols.Tls12;

    /// <summary>给 <see cref="HttpClientHandler"/> / <see cref="SocketsHttpHandler"/> 用的 SSL 选项。</summary>
    public static SslClientAuthenticationOptions SslOptions() => new() { EnabledSslProtocols = Protocols };

    /// <summary>默认处理器：跟随重定向、按需解压、显式 TLS 1.2。</summary>
    public static HttpClientHandler Handler(bool allowRedirect = true, bool decompress = false,
                                            int maxConnectionsPerServer = 0)
        => new()
        {
            AllowAutoRedirect = allowRedirect,
            AutomaticDecompression = decompress ? DecompressionMethods.All : DecompressionMethods.None,
            MaxConnectionsPerServer = maxConnectionsPerServer > 0 ? maxConnectionsPerServer : int.MaxValue,
            // HttpClientHandler 没有 SslOptions（那是 SocketsHttpHandler 的），直接给协议集合
            SslProtocols = Protocols,
        };

    /// <summary>日志/排障用的一行说明。</summary>
    public static string Describe()
        => (Protocols & SslProtocols.Tls13) != 0
            ? "TLS 1.2 + 1.3（显式启用）"
            : "TLS 1.2（显式启用，兼容 Win7）";
}
