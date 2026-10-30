using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using Microsoft.Web.WebView2.Core;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 工具宿主页（2026-10-23 上游）：把本地工具包（HTML）当页面跑。
///
/// <para>
/// 设计要点（上游定的口径）：
/// <list type="bullet">
///   <item><b>Web 优先、原生兜底</b>：什么时候用哪边由 <see cref="ToolCarrier"/> 一次判定，本页只管跑。</item>
///   <item><b>不做超时</b>：装了 WebView2 就加载网页，没装就直接走原生，不搞「等几秒再切」那一套。</item>
///   <item><b>只保留异常回退</b>：初始化抛异常或导航明确失败才静默切原生页 ——
///         那是「加载不了」，不是「慢」。不弹提示、不加角标：用户不该知道背后有两套实现。</item>
///   <item><b>只此一个 WebView</b>：切工具时整页重建，不堆实例。</item>
/// </list>
/// </para>
/// <para>
/// Win7 版平台差异：Avalonia 没有 WebView2 控件，网页由 <see cref="WebSheetHost"/>
/// （子窗口 + CoreWebView2）承载，占位区 <c>WebHost</c> 提供它的摆放区域；
/// 窗口移动 / 缩放时 MainWindow 的几何管线会触发 <c>ToolHostGeometryChanged</c>，本页据此重摆。
/// </para>
/// </summary>
public sealed partial class ToolHostPage : PageBase
{
    private ToolHostRequest? _request;

    /// <summary>已经决定去向了（成功或回退），避免重复导航。</summary>
    private bool _settled;

    /// <summary>本页还活着（导航离开后置 false：异步回来的初始化不许再动 UI）。</summary>
    private bool _alive;

    private WebSheetHost? _host;

    public ToolHostPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);

        _request = parameter as ToolHostRequest;
        if (_request is null || string.IsNullOrEmpty(_request.EntryFile))
        {
            Fallback("没有工具包参数");
            return;
        }

        _alive = true;
        WebHost.SizeChanged += (_, _) => Reposition();
        if (App.MainWindow is { } win) win.ToolHostGeometryChanged += OnGeometryChanged;

        _ = StartAsync();
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _alive = false;
        if (App.MainWindow is { } win) win.ToolHostGeometryChanged -= OnGeometryChanged;
        TeardownHost();
    }

    private async Task StartAsync()
    {
        try
        {
            var owner = App.MainWindow;
            if (owner is null || !WebViewRuntimeService.IsRuntimeInstalled())
            {
                Fallback("WebView2 不可用");
                return;
            }

            var hwnd = Backdrop.TryGetHwnd(owner);
            var host = await WebSheetHost.CreateAsync(hwnd, App.WebRuntime);

            if (!_alive) { host?.Dispose(); return; }   // 等待期间页面已被离开
            if (host is null) { Fallback("初始化失败"); return; }
            _host = host;

            var core = host.Core;
            if (core is null) { Fallback("内核未就绪"); return; }

            // 本地目录 → https://csh.tools/...（只允许读，不给写权限）
            core.SetVirtualHostNameToFolderMapping(
                ToolCarrier.HostName,
                _request!.Folder,
                CoreWebView2HostResourceAccessKind.Allow);

            // 页面脚本一执行就把主题写进 document，避免「先白后黑」闪一下
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ThemeScript());
            if (!_alive) return;

            core.WebMessageReceived += (_, args) =>
            {
                try
                {
                    if (args.TryGetWebMessageAsString() == "ready")
                        _ = core.ExecuteScriptAsync(ThemeScript());
                }
                catch { /* 桥接失败不该影响工具本身 */ }
            };

            // 浏览器进程真的挂了（不是「慢」）才回退。这条不是超时：安全软件拦 WebView2 时
            // 就是这个症状 —— 子进程被反复杀掉，NavigationCompleted 永远不会来。
            core.ProcessFailed += (_, args) =>
            {
                if (args.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) return;

                AppLog.Warn("tool-host", "浏览器进程退出，回退原生页");
                OnUi(() => Fallback("浏览器进程退出"));
            };

            host.NavigationCompleted += ok =>
            {
                if (ok)
                {
                    AppLog.Step("tool-host", "工具页已就绪");
                    return;
                }

                AppLog.Warn("tool-host", "工具页加载失败，回退原生页");
                OnUi(() => Fallback("加载失败"));
            };

            var fileName = System.IO.Path.GetFileName(_request.EntryFile);
            var url = $"https://{ToolCarrier.HostName}/{Uri.EscapeDataString(fileName)}";
            AppLog.Step("tool-host", $"导航到 {url}（根目录 {_request.Folder}）");

            host.Navigate(url);
            Reposition();
        }
        catch (Exception ex)
        {
            AppLog.Exception("tool-host", ex, "初始化工具宿主");
            OnUi(() => Fallback("初始化异常"));
        }
    }

    /// <summary>把网页宿主摆到 <c>WebHost</c> 占位区的位置（主窗口客户区坐标，物理像素）。</summary>
    private void Reposition()
    {
        if (_host is null) return;
        try
        {
            var owner = App.MainWindow;
            if (owner is null) { _host.Hide(); return; }

            var p = WebHost.TranslatePoint(new Point(0, 0), owner);
            if (p is null) return;

            var scale = owner.RenderScaling > 0 ? owner.RenderScaling : 1.0;
            var size = WebHost.Bounds.Size;
            _host.ShowAt(
                (int)Math.Round(p.Value.X * scale),
                (int)Math.Round(p.Value.Y * scale),
                (int)Math.Round(size.Width * scale),
                (int)Math.Round(size.Height * scale),
                scale);
        }
        catch { /* 摆不上去就当作没显示，等下一次几何事件再试 */ }
    }

    private void OnGeometryChanged() => OnUi(Reposition);

    /// <summary>切回 UI 线程（WebView2 的事件可能在别的线程上回来）。</summary>
    private void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void TeardownHost()
    {
        try { _host?.Hide(); } catch { }
        try { _host?.Dispose(); } catch { }
        _host = null;
    }

    /// <summary>
    /// 静默退回原生页。别在这里加任何提示 —— 降级对用户是「无感」的。
    /// </summary>
    private void Fallback(string reason)
    {
        if (_settled) return;
        _settled = true;
        AppLog.Step("tool-host", $"回退原生页：{reason}");

        TeardownHost();

        var page = _request?.NativePage ?? typeof(ToolsPage);
        var parameter = _request?.NativeParameter;

        this.FindAncestorOfType<Frame>()?.Navigate(page, parameter);
    }

    /// <summary>把深浅色注入网页（网页那边只认 <c>document.documentElement.dataset.theme</c>）。</summary>
    private string ThemeScript()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        return $"document.documentElement.dataset.theme='{(dark ? "dark" : "light")}';";
    }
}
