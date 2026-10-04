using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 应用内网页浮层（WebSheet）用的 **CoreWebView2 宿主**。
///
/// ⛔ 为什么需要这个文件：Avalonia **没有 WebView2 控件**（原版是 WinUI 的
///    <c>Microsoft.UI.Xaml.Controls.WebView2</c>）。本工程只引了 <c>Microsoft.Web.WebView2</c> 的 **Core**
///    （见 PORTING.md：Win7 上 WebView2 运行时止于 109，SDK 锁 1.0.1587.40）。
///    Core 版提供 <c>CoreWebView2Environment.CreateCoreWebView2ControllerAsync(IntPtr)</c> ——
///    即"自带宿主"模式：我们自己建一个子窗口（HWND），把 WebView 挂进去。
///
/// 做法：
///   ① 在 Avalonia 主窗口下面建一个 WS_CHILD 子窗口；
///   ② <c>CreateCoreWebView2ControllerAsync(child)</c> 拿到 Controller；
///   ③ Controller 的 Bounds = 子窗口客户区（物理像素），位置由外边 SetWindowPos 摆；
///   ④ 显示/隐藏靠 Controller.IsVisible，离场时 Close + 销毁子窗口。
///
/// ⚠️ 这套是**未经 Win7 实机验证**的（本地无 Win7，也不一定有 WebView2 运行时）。所以整个类一行都不许抛：
///    任何一步失败都返回不可用，MainWindow 那边会走「用系统浏览器打开」的降级路径。
/// </summary>
internal sealed class WebSheetHost : IDisposable
{
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly NativeMethodsShell.WndProcDelegate _proc;   // 必须留引用，否则委托被 GC 后回调直接崩
    private readonly string _className = "CshWebSheetHost_" + Guid.NewGuid().ToString("N")[..8];
    private IntPtr _child;

    private CoreWebView2Controller? _controller;
    private bool _disposed;

    public CoreWebView2? Core => _controller?.CoreWebView2;
    public bool IsReady => _controller?.CoreWebView2 is not null;

    /// <summary>导航开始。</summary>
    public event Action? NavigationStarting;

    /// <summary>导航完成（ok）。</summary>
    public event Action<bool>? NavigationCompleted;

    /// <summary>文档标题变化。</summary>
    public event Action<string>? TitleChanged;

    /// <summary>要开新窗口（target=_blank 之类）。</summary>
    public event Action<string>? NewWindowRequested;

    /// <summary>网页发来的消息（JSON）。</summary>
    public event Action<string>? WebMessageReceived;

    private WebSheetHost(IntPtr parent, NativeMethodsShell.WndProcDelegate proc)
    {
        _proc = proc;

        var hInstance = NativeMethodsShell.GetModuleHandle(null);
        var wc = new NativeMethodsShell.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethodsShell.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        NativeMethodsShell.RegisterClassEx(ref wc);

        _child = NativeMethodsShell.CreateWindowEx(
            0, _className, "csh-websheet", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 10, 10, parent, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    /// <summary>
    /// 建宿主并把 WebView 环境接上。失败（没有运行时 / 建窗失败 / 初始化异常）返回 <c>null</c>。
    /// ⚠️ 必须在 UI 线程调用。
    /// </summary>
    public static async Task<WebSheetHost?> CreateAsync(IntPtr parentHwnd, WebViewRuntimeService runtime)
    {
        if (parentHwnd == IntPtr.Zero) return null;

        WebSheetHost? host = null;
        try
        {
            NativeMethodsShell.WndProcDelegate proc = static (h, m, w, l) => NativeMethodsShell.DefWindowProc(h, m, w, l);
            host = new WebSheetHost(parentHwnd, proc);

            if (host._child == IntPtr.Zero) { host.Dispose(); return null; }

            var env = await runtime.GetEnvironmentAsync();
            var controller = await env.CreateCoreWebView2ControllerAsync(host._child);
            host._controller = controller;

            controller.DefaultBackgroundColor = Color.Transparent;
            controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            controller.IsVisible = false;
            host.Wire(controller.CoreWebView2);
            return host;
        }
        catch
        {
            host?.Dispose();
            return null;
        }
    }

    private void Wire(CoreWebView2 core)
    {
        try
        {
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;          // 开发期方便调试
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            var shim = Services.EmbeddedAssets.ReadText("shell-shim.js");
            if (!string.IsNullOrEmpty(shim))
                _ = core.AddScriptToExecuteOnDocumentCreatedAsync(shim);

            core.NavigationStarting += (_, _) => NavigationStarting?.Invoke();
            core.NavigationCompleted += (_, e) => NavigationCompleted?.Invoke(e.IsSuccess);
            core.DocumentTitleChanged += (_, _) =>
            {
                try { TitleChanged?.Invoke(core.DocumentTitle); } catch { }
            };

            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                NewWindowRequested?.Invoke(e.Uri);
            };

            core.WebMessageReceived += (_, e) =>
            {
                // 网页发的是 JSON 对象 → 用 WebMessageAsJson；万一发字符串再退回 TryGetWebMessageAsString
                string? json = null;
                try { json = e.WebMessageAsJson; }
                catch
                {
                    try { json = e.TryGetWebMessageAsString(); } catch { }
                }
                if (string.IsNullOrEmpty(json)) return;
                WebMessageReceived?.Invoke(json!);
            };
        }
        catch { /* 事件接不上不影响基本显示 */ }
    }

    /// <summary>把宿主摆到指定位置（父窗口客户区坐标系，物理像素）并显示。</summary>
    public void ShowAt(int x, int y, int w, int h, double scale)
    {
        if (_disposed) return;
        try
        {
            if (w <= 0 || h <= 0) { Hide(); return; }

            NativeMethods.SetWindowPos(_child, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

            if (_controller is { } c)
            {
                c.RasterizationScale = scale > 0 ? scale : 1.0;
                c.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                c.Bounds = new Rectangle(0, 0, w, h);
                c.IsVisible = true;
            }
        }
        catch { /* 摆不上去就当作没显示 */ }
    }

    public void Hide()
    {
        try { if (_controller is { } c) c.IsVisible = false; } catch { }
    }

    public void Navigate(string url)
    {
        try { Core?.Navigate(url); } catch { }
    }

    public async Task ExecuteScriptAsync(string script)
    {
        try
        {
            if (Core is { } core) await core.ExecuteScriptAsync(script);
        }
        catch { }
    }

    public void SetZoom(double zoom)
    {
        try
        {
            if (_controller is { } c) c.ZoomFactor = Math.Clamp(zoom, 0.5, 3.0);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _controller?.Close(); } catch { }
        _controller = null;

        try { if (_child != IntPtr.Zero) NativeMethodsShell.DestroyWindow(_child); } catch { }
        _child = IntPtr.Zero;
        try { NativeMethodsShell.UnregisterClass(_className, NativeMethodsShell.GetModuleHandle(null)); } catch { }
    }
}
