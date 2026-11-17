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
/// 背景：Avalonia 没有 WebView2 控件（原版为 WinUI 的
///    <c>Microsoft.UI.Xaml.Controls.WebView2</c>）。本工程只引用 <c>Microsoft.Web.WebView2</c> 的 Core 版
///    （见 PORTING.md：Win7 上 WebView2 运行时止于 109，SDK 锁 1.0.1587.40）。
///    Core 版提供 <c>CoreWebView2Environment.CreateCoreWebView2ControllerAsync(IntPtr)</c>，
///    即"自带宿主"模式：自建子窗口（HWND）承载 WebView。
///
/// 做法：
///   ① 在 Avalonia 主窗口下面建一个 WS_CHILD 子窗口；
///   ② <c>CreateCoreWebView2ControllerAsync(child)</c> 拿到 Controller；
///   ③ Controller 的 Bounds = 子窗口客户区（物理像素），位置由外边 SetWindowPos 摆；
///   ④ 显示/隐藏靠 Controller.IsVisible，离场时 Close + 销毁子窗口。
///
/// 此实现未经 Win7 实机验证（可能缺少 WebView2 运行时），因此整个类不允许抛出异常：
///    任何一步失败都返回不可用，由 MainWindow 走「用系统浏览器打开」的降级路径。
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

    /// <summary>网页 → 原生消息（JSON）。第二个参数是发出消息的文档地址（用于来源校验）。</summary>
    public event Action<string, string?>? WebMessageReceived;

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
                // 必须传出消息来源：浮层可导航到任意第三方网页，而 shim 在所有文档上执行，
                // 调用方需校验来源为本家站点才允许桥接。
                string? source = null;
                try { source = e.Source; } catch { }
                WebMessageReceived?.Invoke(json!, source);
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

    // ── 内存回收（自上游 dv1.2.2 移植，2026-11-14）──────────────────────────
    //
    // 起因：网页工具每开一次就拉起一个 WebView2 渲染进程，用户反馈进程列表里
    // 挂着一堆「WebView2: 数学实验室」，每条约 27～44 MB。本进程里做 GC.Collect /
    // EmptyWorkingSet 对那是**另一个进程**，一点用都没有。
    //
    // 能动的只有 CoreWebView2 上的两件事：
    //   ① 挂起（TrySuspendAsync）：把页面冻结、堆与缓存压到最低，渲染进程还在但基本不占内存，
    //      Resume 之后页面状态原样还在。⚠️ **只在控件不可见时可用**，所以必须先 IsVisible = false。
    //   ② 关闭（Controller.Close）：控件连同渲染进程一起释放。关掉的宿主**不能再用**。

    /// <summary>挂起后多久没再打开就关掉（10 分钟）。</summary>
    public const int IdleCloseMs = 10 * 60 * 1000;

    /// <summary>是否已挂起（避免重复挂起 / 恢复不该恢复的）。</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>
    /// 挂起：先隐藏，把内存目标压到 Low，再冻结。
    /// 返回 true = 真的冻结了；false = 只降到了低内存档位（冻结没被接受，不是故障）。
    ///
    /// <para>
    /// ⚠️ <c>TrySuspendAsync</c> 在「控件刚离开可见状态」那一下会抛「组或资源的状态不是执行
    /// 请求操作的正确状态」—— 因为 IsVisible 的改动要过一轮消息循环才传到底层控制器。
    /// 所以这里藏完之后先等一小会儿，再冻结；仍然失败也不算错，Low 档位本身已经把
    /// 渲染进程的内存压下去了。
    /// </para>
    /// </summary>
    public async Task<bool> SuspendAsync()
    {
        if (_disposed || IsSuspended) return false;

        try
        {
            var core = Core;
            if (core is null) return false;

            // ⚠️ 顺序不能反：TrySuspend 要求控制器此刻不可见
            try { if (_controller is { } c) c.IsVisible = false; } catch { }

            // ⚠️ 本地 WebView2 SDK（1.0.1587.40，Win7 上的上限）没有
            //    MemoryUsageTargetLevel 这个属性（较新运行时才引入），只能靠 TrySuspendAsync 冻结。

            // 等一轮消息循环，把「不可见」真正传给底层控制器，否则 TrySuspend 会直接抛
            try { await Task.Delay(150).ConfigureAwait(true); } catch { }

            try
            {
                await core.TrySuspendAsync();
                IsSuspended = true;
                ClassSoftwareHub.Desktop.Core.AppLog.Step("webview", "网页浮层已挂起");
                return true;
            }
            catch (Exception ex)
            {
                // 不是故障：冻结只在「控件确实不可见」时才被接受。
                // 内存目标已经是 Low，能省的大头已经省了；闲置到期还会整个关掉。
                ClassSoftwareHub.Desktop.Core.AppLog.Debug("webview", "网页浮层已降到低内存档位，冻结未生效：" + ex.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            ClassSoftwareHub.Desktop.Core.AppLog.Warning("webview", "网页浮层挂起失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 恢复：解冻 + 内存目标恢复 Normal + 重新显示。
    /// 没挂起过也安全（<c>Resume</c> 对活动页面是空操作）。
    /// </summary>
    public void Resume()
    {
        if (_disposed) return;

        try
        {
            var core = Core;
            if (core is null) return;

            core.Resume();


            try { if (_controller is { } c) c.IsVisible = true; } catch { }

            IsSuspended = false;
            ClassSoftwareHub.Desktop.Core.AppLog.Step("webview", "网页浮层已恢复");
        }
        catch (Exception ex)
        {
            ClassSoftwareHub.Desktop.Core.AppLog.Warning("webview", "网页浮层恢复失败：" + ex.Message);
        }
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