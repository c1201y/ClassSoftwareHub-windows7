using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>检测系统 WebView2 运行时（Evergreen），并提供共享环境。</summary>
/// <remarks>
/// ⚠️ Win7 移植说明（硬约束，见 PORTING.md 6.4）：
///    WebView2 运行时在 Windows 7 上的**最后支持版本是 109.0.1518.x** —— 之后的版本不再支持 Win7，
///    微软已停止在 Win7 上分发 Evergreen 运行时。所以：
///      · csproj 已把 SDK 锁在 <c>1.0.1587.40</c>（配套 109 运行时，不会用上更新的 API）；
///      · <see cref="Probe"/> 只负责「有没有装运行时」，**不判版本高低**（装了 109 就能用，不用拦）；
///      · 探不到运行时时，调用方（主窗口的 WebSheet）必须走**「用浏览器打开」的降级路径**
///        （<see cref="ShellConfig.WebView2DownloadUrl"/> 引导安装 / Launcher 打开系统浏览器）——
///        这套降级原版已有，照搬即可，本服务只提供 <see cref="IsAvailable"/> / <see cref="LastError"/> 供其判断。
/// </remarks>
public sealed class WebViewRuntimeService
{
    public bool IsAvailable { get; private set; }
    public string? Version { get; private set; }
    public string? LastError { get; private set; }

    private CoreWebView2Environment? _environment;

    public static string UserDataDir { get; } = Path.Combine(SettingsStore.Dir, "WebView2");

    /// <summary>同步探测（启动时调用一次）。</summary>
    public bool Probe()
    {
        try
        {
            Version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            IsAvailable = !string.IsNullOrWhiteSpace(Version);
            LastError = null;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Version = null;
            LastError = ex.Message;
        }
        return IsAvailable;
    }

    public async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            // 只加载远程站点；不使用任何本地回退页面
            Language = "zh-CN",
            // ⚠️ 移植说明：原版还设了 AreBrowserExtensionsEnabled = false，
            //    但那个属性是 SDK 1.0.1722+ 才有的；本工程为配 Win7 的 109 运行时把 SDK 锁在
            //    1.0.1587.40，**没有这个属性** —— 而该版本本来就不加载浏览器扩展，语义等价，故省略。
        };
#if DEBUG
        // 开发期：暴露一个 CDP 调试口，方便外壳侧排查注入/样式问题
        options.AdditionalBrowserArguments = "--remote-debugging-port=9333";
#endif
        // ⚠️ 移植说明：旧 SDK 的入口是 CreateAsync（新 SDK 才叫 CreateWithOptionsAsync），
        //    参数一致（browserExecutableFolder / userDataFolder / options）。
        _environment ??= await CoreWebView2Environment.CreateAsync(
            null,
            UserDataDir,
            options);
        return _environment;
    }
}
