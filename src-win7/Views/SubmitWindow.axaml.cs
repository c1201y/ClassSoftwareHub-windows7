using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// ⚠️ 遗留：网页版「提交软件」小窗口。提交页早已原生化（Pages/SubmitPage），这里现在没有入口调用，只作兜底。
/// 用系统标题栏（不搞自绘），打开就是站点 #/submit，填完直接提交。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · 原版放 <c>WebView2</c> 控件、<c>EnsureCoreWebView2Async</c> 初始化；Avalonia 没有 WebView2 控件，
///     改走 <see cref="WebSheetHost"/>（原生子窗口 + CoreWebView2「自带宿主」模式，与主窗口的 WebSheet 同一条路）。
///     WebView2 的三条 Settings（关状态栏 / 开右键菜单 / 开缩放）已由 WebSheetHost.Wire 统一设好，这里不再重复。
///   · 初始化时机从构造函数挪到 <see cref="Opened"/>：WinUI 的窗口没显示也能初始化 WebView2，
///     而 Avalonia 要等窗口打开后才拿得到 HWND（子窗宿主必须有父 HWND）。
///   · <c>DescribeWebError(CoreWebView2WebErrorStatus)</c> 拿不到细分错误码了 —— WebSheetHost 的
///     NavigationCompleted 只回 bool，所以失败提示退回通用文案（见 OnNavigationCompleted）。
/// </summary>
public sealed partial class SubmitWindow : Window
{
    private readonly WebViewRuntimeService _runtime = new();
    private WebSheetHost? _host;
    private bool _everLoaded;
    private bool _closing;

    public SubmitWindow()
    {
        InitializeComponent();
        Title = "提交软件 · " + ShellConfig.AppName;
        try
        {
            ClientSize = new Size(1020, 780);          // 原版 AppWindow.Resize(1020, 780)（客户区同尺寸）
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(icon)) icon = Environment.ProcessPath ?? "";
            if (icon.Length > 0 && File.Exists(icon)) Icon = new WindowIcon(icon);
        }
        catch { /* 尺寸设不上不影响使用 */ }

        Opened += (_, _) => _ = InitAsync();
        Resized += (_, _) => LayoutWebView();
        PositionChanged += (_, _) => LayoutWebView();
        Closing += (_, _) =>
        {
            _closing = true;
            try { _host?.Dispose(); } catch { }
            _host = null;
        };
    }

    private async Task InitAsync()
    {
        try
        {
            // 兜底探测：缺 WebView2 运行时（Win7 上最高 109.x）就直接走「浏览器打开」那条路，别白屏
            if (!_runtime.Probe())
            {
                ShowError("未检测到 WebView2 运行时，请点下方按钮用浏览器打开提交页");
                return;
            }

            var hwnd = Backdrop.TryGetHwnd(this);
            _host = await WebSheetHost.CreateAsync(hwnd, _runtime);
            if (_host is null)
            {
                ShowError("WebView2 初始化失败，请点下方按钮用浏览器打开提交页");
                return;
            }

            _host.NewWindowRequested += OnNewWindowRequested;
            _host.NavigationCompleted += OnNavigationCompleted;

            LayoutWebView();
            _host.Navigate(ShellConfig.SubmitPageUrl);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>把 WebView 子窗铺满本窗口客户区（尺寸用物理像素，随窗口缩放）。</summary>
    private void LayoutWebView()
    {
        if (_closing || _host is null) return;
        try
        {
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd == IntPtr.Zero) return;
            if (!NativeMethodsWindow.GetClientRect(hwnd, out var rc)) return;
            _host.ShowAt(0, 0, rc.Width, rc.Height, RenderScaling);
        }
        catch { /* 摆不上就等下一次窗口尺寸变化再试 */ }
    }

    private void OnNewWindowRequested(string uri)
    {
        // 外链一律交给系统浏览器，别在这个小窗口里乱开
        try { _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(uri)); } catch { }
    }

    private void OnNavigationCompleted(bool ok)
    {
        if (ok)
        {
            _everLoaded = true;
            ErrorPanel.IsVisible = false;
            LoadingPanel.IsVisible = false;
            WebHost.IsVisible = true;
            WebHost.Opacity = 1;
        }
        else if (!_everLoaded)
        {
            // 首次加载失败才整屏报错
            // ⚠️ 原版这里能细分 CoreWebView2WebErrorStatus；WebSheetHost 只回 bool，退回通用文案
            ShowError("页面加载失败（网络错误或站点不可达）");
        }
        else
        {
            // 已经打开过页面：站内的失败（比如提交接口返回错误）由网页自己提示
            LoadingPanel.IsVisible = false;
            WebHost.IsVisible = true;
            WebHost.Opacity = 1;
        }
    }

    private void ShowError(string? tip)
    {
        LoadingPanel.IsVisible = false;
        WebHost.IsVisible = false;
        WebHost.Opacity = 0;
        ErrorPanel.IsVisible = true;
        ErrorTip.Text = tip ?? "";
    }

    private void Retry_Click(object? sender, RoutedEventArgs e)
    {
        ErrorPanel.IsVisible = false;
        LoadingPanel.IsVisible = true;
        Ring.IsActive = true;
        try { _host?.Navigate(ShellConfig.SubmitPageUrl); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>兜底：这个窗口打不开（缺 WebView2 / 网络问题）时，去系统浏览器打开提交页。</summary>
    private void Browser_Click(object? sender, RoutedEventArgs e)
    {
        try { _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(ShellConfig.SubmitPageUrl)); } catch { }
        Close();
    }
}
