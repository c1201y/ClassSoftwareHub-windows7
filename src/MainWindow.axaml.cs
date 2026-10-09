using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;
using Microsoft.Win32;
using AvaloniaDialog = FluentAvalonia.UI.Controls.ContentDialog;
// ⚠️ TrayIcon 撞名：Avalonia.Controls.TrayIcon 与 Services.TrayIcon 同时在 using 区（CS0104）。
//    本工程托盘用的是自研 Services.TrayIcon（Shell_TrayAddMessage 那套），用别名消歧义。
using Win32TrayIcon = ClassSoftwareHub.Desktop.Services.TrayIcon;

namespace ClassSoftwareHub.Desktop;

/// <summary>
/// 主窗口。对应 WinUI 原版 <c>MainWindow.xaml(.cs)</c>（1867 行）。
///
/// ⚠️ 移植要点（逐条，详见交付总结）：
///   · 无边框 / 自定义标题栏：原版 <c>ExtendsContentIntoTitleBar = true</c> + <c>SetTitleBar</c>；
///     Avalonia 用 <c>ExtendClientAreaToDecorationsHint</c> + <c>ExtendClientAreaChromeHints</c> +
///     <c>ExtendClientAreaTitleBarHeightHint</c>（见 <see cref="ConfigureTitleBar"/> 的选择理由）。
///   · 窗口材质：一律 <c>Platform.Backdrop.Apply</c>（自动分级降级到 Win7 的 Aero 毛玻璃 / 纯色）。
///   · 主题：一律 <c>Platform.ThemeCompat.Apply/Notify</c>。
///   · 网页浮层（WebSheet）：Avalonia 没有 WebView2 控件 → 见 <c>Views/WebSheetHost</c>。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 网页浮层的进场/退场位移（对应原版 XAML 里的 &lt;TranslateTransform x:Name="WebSheetShift"&gt;）。
    /// Avalonia 的 x:Name 字段生成器不给属性元素里的 Transform 生成字段，
    /// 这里改成从 RenderTransform 手动取，语义与原版一致。
    /// </summary>
    private TranslateTransform WebSheetShift =>
        (TranslateTransform)((Avalonia.Controls.Border)WebSheetCard).RenderTransform!;

    private readonly SettingsStore _settings = App.Settings;
    private readonly BridgeService _bridge;
    private readonly WebViewRuntimeService _runtime = new();
    private readonly DispatcherTimer _loadTimer;

    private bool _webReady;
    private bool _navigatedOk;
    private bool _themeFromWeb;
    private bool _firstActivated;
    private readonly bool _startMinimized;
    private readonly bool _startPalette;

    private Win32TrayIcon? _tray;
    private Win32TrayIcon? _trayTools;      // 第二个托盘图标：常用工具（左键直接开工具窗口）
    private bool _exitRequested;
    private bool _finishing;                 // FinishExit 防重入（ExitApp / OnWindowClosed 两条路都会走到）

    /// <summary>
    /// 主窗口是不是**我们自己**收进托盘的（点 × / 托盘菜单 / 启动参数 --minimized、--palette）。
    ///
    /// 存在的意义是给「可见性看门狗」一个"别多管闲事"的开关：
    /// 用户故意收起来的窗口，看门狗绝不能自作主张再弹出来。
    /// </summary>
    private bool _hiddenToTray;

    /// <summary>可见性看门狗的命中次数 —— 只用来限制日志刷屏。</summary>
    private int _visibleWatchdogHits;

    /// <summary>托盘气泡被点的时候要干什么（不同的通知点进去该去不同的地方）。</summary>
    private Action? _balloonAction;

    /// <summary>当前正开着的下载进度弹窗 + 它盯着的那条任务（同时只有一个弹窗）。</summary>
    private ContentDialog? _downloadDialog;
    private string? _downloadDialogTaskId;

    /// <summary>网页上报的「可拖动矩形」（物理像素），仅在开启原生 caption 区域时使用。</summary>
    private readonly List<Rect> _webCaptionRects = new();

    // ── 网页浮层宿主（CoreWebView2 子窗口） ──
    private Views.WebSheetHost? _sheetHost;
    private bool _sheetReady;
    private string _sheetUrl = "";

    public MainWindow()
    {
        Platform.PortLog.Step("MainWindow: 构造函数开始");
        InitializeComponent();
        Platform.PortLog.Step("MainWindow: InitializeComponent 完成");

        Title = ShellConfig.WindowTitle;

        _loadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ShellConfig.LoadTimeoutSeconds) };
        _loadTimer.Tick += (_, _) => { _loadTimer.Stop(); ShowNetworkError("连接超时"); };

        _bridge = new BridgeService(this, _settings);

        ConfigureWindow();
        ConfigureTitleBar();
        ApplyTheme(_settings.Current.Theme);
        ApplyBackdrop(_settings.Current.Backdrop);
        LoadLoadingIcon();
        Platform.PortLog.Step("MainWindow: 窗口/标题栏/主题/材质 配置完成");

        Closed += OnWindowClosed;

        // 托盘图标：关窗口收托盘、开机最小化收托盘、托盘菜单（工具浮窗 / 退出）都靠它
        InitTray();
        // ⚠️ 侧边栏就是在这一步（InitTray → ToolSidebarWindow.ApplySetting）挂上去的 ——
        //    所以「侧边栏有、主界面没有」说明死在这行之后。日志停在哪一条，就是死在哪一步。
        Platform.PortLog.Step("MainWindow: 托盘 + 侧边栏就绪（侧边栏此时已可见）");

        // 下载跑完（成功 / 失败）→ 弹系统通知。谁在盯着弹窗的那条不弹，交给弹窗自己收尾。
        DownloadManager.Current.Finished += OnDownloadFinished;

        // ===== 原生界面 =====
        // 软件内容来自内容包（开发时读站点工程 dist/content，正式版走远端 manifest）
        App.Content.Load();
        Platform.PortLog.Step("MainWindow: 内容包已加载");
        NativeShell.Init();
        Platform.PortLog.Step("MainWindow: 原生外壳 ShellPage.Init 完成");
        NativeShell.SetIcon(LoadAppIconImage());
        Platform.PortLog.Step("MainWindow: 标题栏图标已设置");

        // 后台同步内容包（远端发布过 content/manifest.json 才会真的下载；没有就继续用本机数据）
        _ = SyncContentAsync();

        // 开机自启 + 「开机最小化」：注册表里会带 --minimized，启动时收进任务栏
        _startMinimized = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        // 直接开工具浮窗（可以拿它建个桌面快捷方式：ClassSoftwareHub.exe --palette）
        _startPalette = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--palette", StringComparison.OrdinalIgnoreCase));
        Activated += OnFirstActivated;

        StartWindowVisibilityWatchdog();

        _ = BootAsync();
        Platform.PortLog.Step("MainWindow: 构造函数全部完成（窗口对象可用）");
    }

    private void OnFirstActivated(object? sender, EventArgs args)
    {
        if (_firstActivated) return;
        _firstActivated = true;
        Activated -= OnFirstActivated;

        // ⚠️ 这一条是排障的**分水岭**：
        //    port.log 里如果停在「构造函数全部完成」而没有下面这条，说明窗口对象建好了、
        //    却从来没被显示过 —— 那问题就在 Avalonia 的显示环节，而不是我们的构造代码。
        Platform.PortLog.Step("MainWindow: 首次 Activated —— 窗口真的露脸了");
        App.NoteMainShown();

#if CSH_CONSOLE
        // 这一句是「主窗口到底露过脸没有」的唯一权威依据（退出那一刻窗口已被关掉，IsVisible 不作数）。
        Platform.DiagConsole.MarkMainWindowShown();
#endif

        // 窗口真正显示之后再兜一次圆角：框架在首帧还会碰一次标题栏，
        // 早于显示设的值有可能被它按回去（图标那边也踩过同样的重置）。
        ApplyWindowRounding();

        // 显示之后确认一下窗口真的落在屏幕里 —— 存档尺寸/位置换了机器之后可能跑到屏幕外
        EnsureWindowOnScreen();

        if (_startPalette)
        {
            // --palette：主窗口不露脸，托盘 + 工具浮窗直接摆出来
            HideToTray();
            Views.ToolPaletteWindow.ShowTool();
        }
        else if (_startMinimized)
        {
            // 「开机最小化启动」= 直接收进托盘（不占任务栏），想用的时候从托盘图标点出来
            HideToTray();
        }

        // 启动时查一次更新：**不强制**，有新版就问用户（主窗口没露脸时发系统通知）
        _ = CheckUpdateOnStartupAsync();

        // 启动自检：首帧之后先记一条，5 秒后再记一条（托盘/侧边栏是异步挂上的，第二条才准）
        WriteStartupTrace("首帧显示后");
        DispatcherTimer.RunOnce(() => WriteStartupTrace("启动 5 秒后"), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 把窗口拉回屏幕可见区域。
    ///
    /// 为什么需要：存档尺寸 / 系统给的初始位置碰上"换了台机器、改了分辨率、多屏拔掉一个"之后，
    /// 窗口可能整个落在屏幕外面 —— 用户看到的现象就是"程序在跑，主界面不显示，只有边上的
    /// 侧边栏还露着"。这里按 Win32 实际矩形判断重叠，重叠太少就居中拉到主屏工作区。
    /// </summary>
    private void EnsureWindowOnScreen()
    {
        try
        {
            var hwnd = TryGetHwnd();
            if (hwnd == IntPtr.Zero) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;

            var screens = Screens.All;
            if (screens.Count == 0) return;

            var rect = new PixelRect(r.Left, r.Top, r.Width, r.Height);
            var needW = Math.Min(220, Math.Max(80, r.Width / 3));      // 至少露出这么宽
            var needH = Math.Min(120, Math.Max(60, r.Height / 3));     // 至少露出这么高（标题栏得够得着）

            var onScreen = false;
            // 顺便记下"窗口主要落在哪块屏上" —— 后面把位置夹回工作区时要按这块屏算，
            // 不能一律按主屏，否则副屏摆在左边/上边时会把窗口硬拽到主屏去。
            var wa = (Screens.Primary ?? screens[0]).WorkingArea;
            long bestArea = 0;
            foreach (var s in screens)
            {
                var inter = s.WorkingArea.Intersect(rect);
                if (inter.Width >= needW && inter.Height >= needH) onScreen = true;

                var area = (long)Math.Max(0, inter.Width) * Math.Max(0, inter.Height);
                if (area > 0 && area >= bestArea) { bestArea = area; wa = s.WorkingArea; }
            }

            if (!onScreen)
            {
                var w = Math.Min(r.Width, wa.Width);
                var h = Math.Min(r.Height, wa.Height);
                var x = wa.X + (wa.Width - w) / 2;
                var y = wa.Y + (wa.Height - h) / 2;

                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

                Services.ScreenCapture.Log($"[startup] 主窗口原本不在屏幕内（{r.Left},{r.Top},{r.Right},{r.Bottom}）→ 已居中到 {x},{y} {w}×{h}");
                return;
            }

            // ⚠️ 2026-10-07：窗口"露得够多"≠"整扇都够得着"。
            //    Windows 给「没自己指定位置」的窗口用的是**级联默认位置** —— 每开一次就往下、往右挪一点；
            //    窗口高度又和屏幕差不多时，右下角（往往正是"开始抽号"这类主操作按钮）会压到任务栏底下，
            //    用户看到的现象和"按钮点了没反应"一模一样。
            //    实测：窗口 (98,98)-(2064,1233)，按钮矩形 y=1198~1286，而任务栏从 y=1184 起 —— 点击全被任务栏吃掉。
            //    这里**只挪位置、不动尺寸**：右/下越界就整体左移/上移回工作区；
            //    窗口本身比工作区还大时不硬缩（尺寸有专门的夹取逻辑），顶到左上角为止。
            var nx = Math.Max(Math.Min(r.Left, wa.Right - r.Width), wa.X);
            var ny = Math.Max(Math.Min(r.Top, wa.Bottom - r.Height), wa.Y);
            if (nx == r.Left && ny == r.Top) return;

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, nx, ny, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            Services.ScreenCapture.Log($"[startup] 主窗口右下越界（{r.Left},{r.Top},{r.Right},{r.Bottom}）→ 已挪回 {nx},{ny}");
        }
        catch { }
    }

    private async Task CheckUpdateOnStartupAsync()
    {
        try
        {
            // 上次「静默安装」的退出码（由 UpdateService.RunInstaller 让 cmd 写下来的）。
            // 以前退出码从不被读取：安装失败时应用已经退了，用户下次启动仍是旧版本、
            // 且完全不知情 —— 这里补一句，别让更新静默失败。
            // ⚠️ 放在 AutoCheckUpdate 之前：用户关掉"自动检查更新"不代表他不关心"上次装失败了"。
            // ⚠️ 只在拿到**非 0** 退出码时才提示；读不到记录一律当没有（宁可漏报也不误报）。
            var installOutcome = Services.Updating.UpdateService.ConsumeInstallOutcome();
            if (installOutcome.HasValue && installOutcome.Value != 0)
            {
                ShowBalloon("上次更新可能没有成功",
                    $"安装程序返回了退出码 {installOutcome.Value}，本机仍是 dv{ShellConfig.ShellVersion}。\n" +
                    "可到「设置 → 检查更新」再试一次。", null);
            }

            if (!_settings.Current.AutoCheckUpdate) return;

            var service = Services.Updating.UpdateService.CreateDefault();
            if (!service.Source.IsConfigured) return;

            var channel = Services.Updating.UpdateChannels.Parse(_settings.Current.UpdateChannel);
            var result = await service.CheckAsync(channel, ShellConfig.ShellVersion);
            if (result is not { HasUpdate: true, Release: { } release }) return;

            // 这种启动方式主窗口是藏着的（--minimized / --palette / 直接收进托盘）
            // → 弹对话框没人看，改发一条系统通知，点通知再把界面叫出来
            if (!IsWindowVisible())
            {
                NotifyUpdateAvailable(release);
                return;
            }

            // 让用户自己决定；选「稍后」就安静放过，下次启动还会再问一次
            // ⚠️ 签名已变：首参 XamlRoot 去掉 → AskAsync(release, owner)；返回值由 bool 改为三态枚举
            var choice = await Services.Updating.UpdateFlow.AskAsync(release, this);
            if (choice == Services.Updating.UpdateFlow.UpdateChoice.Now)
                await Services.Updating.UpdateFlow.RunAsync(service, release, owner: this);
            else if (choice == Services.Updating.UpdateFlow.UpdateChoice.Background)
                Services.Updating.UpdateFlow.StartBackgroundDownload(service, release);
        }
        catch
        {
            // 启动时查更新失败就安静放过，别影响正常使用
        }
    }

    /// <summary>主窗口没露脸时，用系统通知提醒"有新版本"（点通知 = 打开主界面并重新走一次检查）。</summary>
    private void NotifyUpdateAvailable(Services.Updating.UpdateRelease release)
    {
        ShowBalloon($"发现新版本 {release.Tag}", "当前版本不是最新版，单击查看更新详情。",
            () => { ShowFromTray(); _ = CheckUpdateManualAsync(); });
    }

    /// <summary>
    /// 更新包下载完成：报一声，点通知打开首页看横幅（横幅上有「立即安装」）。
    ///
    /// ⚠️ 移植说明（WinUI → Avalonia）：原版这里优先走
    ///    <c>Microsoft.Windows.AppNotifications</c>（唯一能带「现在安装 / 稍后安装」按钮的通知形态），
    ///    注册失败才退回托盘气泡。Avalonia 侧没有对应设施，而本仓库系统通知本来就是**托盘气泡**
    ///    （见 <see cref="ShowBalloon"/> 注释：免安装形态下 AppNotification 要额外注册 AUMID/COM），
    ///    所以这里直接走气泡 —— 按钮语义由首页横幅承担（待装标记已在存档里，横幅会一直提醒到装上）。
    ///    ⚠️ 正因如此，**首页横幅是待装更新唯一的可见入口**（见 WelcomePage.RefreshUpdateReadyBar 注释），
    ///    下面必须先把它点亮：用户可能本来就停在首页，那种情况不会有任何导航、横幅不会自己出现。
    /// </summary>
    public void NotifyUpdateReady(string tag)
    {
        Shell.RefreshHomeUpdateBanner();

        ShowBalloon($"ClassSoftwareHub {tag} 下载完成", "安装包已通过校验，打开首页即可安装。",
            () => { ShowFromTray(); Shell.NavigateTo("home"); });
    }

    /// <summary>后台更新下载失败：报一声，别让人干等（直接走气泡，见 <see cref="NotifyUpdateReady"/>）。</summary>
    public void NotifyBackgroundDownloadFailed(string tag, string reason)
    {
        ShowBalloon($"ClassSoftwareHub {tag} 下载失败", $"{reason}\n可稍后在应用内重试。", null);
    }

    /// <summary>下载跑完的提示：完成报一声「好了」，失败也报一声（别让人以为还在下）。</summary>
    private void OnDownloadFinished(DownloadTask task)
    {
        // 进度弹窗正开着它 → 弹窗自己会弹「下载完成 / 下载失败」，别再叠一条系统通知
        if (task.Id == _downloadDialogTaskId) return;

        if (task.State == DownloadState.Completed)
        {
            ShowBalloon("下载完成", $"{task.FileName}\n已保存到：{DownloadService.DefaultDir}",
                () => { ShowFromTray(); Shell.NavigateTo("downloads"); });
        }
        else if (task.State == DownloadState.Failed)
        {
            ShowBalloon("下载失败", $"{task.Title} 下载未完成：{task.Error}",
                () => { ShowFromTray(); Shell.NavigateTo("downloads"); });
        }
    }

    /// <summary>
    /// 弹一条系统通知（托盘气泡），并记住"点它该干什么"。
    /// 不用 Windows 的 AppNotification 是因为免安装（unpackaged）下它要额外注册一套 AUMID/COM
    /// 激活器，而托盘气泡本来就在用（发现新版本那条），零依赖、装了就能用。
    /// </summary>
    private void ShowBalloon(string title, string text, Action? onClick)
    {
        _balloonAction = onClick;
        try { _tray?.ShowBalloon(title, text); }
        catch { }
    }

    /// <summary>
    /// 启动后台同步内容包：远端有 content/manifest.json 就把变化的文件拖到本地缓存
    /// （%LOCALAPPDATA%\ClassSoftwareHub\content），下次启动 ContentStore 就优先用它，
    /// 不再依赖开发目录。远端还没发布时什么都不做。
    /// </summary>
    private async Task SyncContentAsync()
    {
        try
        {
            var before = App.Content.SourceKind;
            var result = await Services.ContentUpdater.SyncAsync();

            if (!result.Updated && before == "cache") return;

            // 有更新（或本来用的是开发目录）→ 重新读一遍，让内容源切到本地缓存
            var oldSource = App.Content.Source;
            var oldCount = App.Content.Apps.Count;
            App.Content.Load();

            var changed = App.Content.Source != oldSource || App.Content.Apps.Count != oldCount;
            if (changed)
                Debug.WriteLine($"[content] {result.Message}；{oldSource} → {App.Content.Source}");
        }
        catch
        {
            // 内容同步失败不打扰用户：本机数据照样能用
        }
    }

    private Views.SubmitWindow? _submitWindow;

    public Views.SubmitWindow? SubmitWindowRef => _submitWindow;

    /// <summary>原生外壳（首页卡片等要从这里跳导航）。</summary>
    public Pages.ShellPage Shell => NativeShell;

    /// <summary>
    /// ⚠️ 遗留路径：「提交软件」的网页版小窗口（Views/SubmitWindow）。提交页早已原生化（Pages/SubmitPage），
    /// 现在没有入口调用这里（OpenSubmitWindow 无人调用），留着当兜底；缺 WebView2 时给替代方案。
    /// </summary>
    public async void OpenSubmitWindow()
    {
        if (_submitWindow is not null)
        {
            _submitWindow.Activate();
            return;
        }

        // 兜底：这是唯一需要 WebView2 的地方。系统里没有运行时 → 给两条退路，别让它白屏报错。
        if (!_runtime.Probe())
        {
            await ShowRuntimeMissingDialogAsync(ShellConfig.SubmitPageUrl);
            return;
        }

        _submitWindow = new Views.SubmitWindow();
        _submitWindow.Closed += (_, _) => _submitWindow = null;
        _submitWindow.Show();
    }

    // ============================================================
    // 窗口 & 标题栏
    // ============================================================

    private bool _iconApplied;

    /// <summary>图标文件：优先用内嵌的 AppIcon.ico（多尺寸），取不到就退回 exe 自己。</summary>
    private static string IconPath()
    {
        var ico = EmbeddedAssets.ExtractToCache("AppIcon.ico", "AppIcon.ico");
        if (!string.IsNullOrEmpty(ico) && File.Exists(ico)) return ico;
        return Environment.ProcessPath ?? "";
    }

    /// <summary>「常用工具」托盘图标用的图标：扳手那个（跟主界面图标区分开）。</summary>
    private static string ToolIconPath()
    {
        var ico = EmbeddedAssets.ExtractToCache("ToolIcon.ico", "ToolIcon.ico");
        if (!string.IsNullOrEmpty(ico) && File.Exists(ico)) return ico;
        return IconPath();
    }

    /// <summary>把内嵌的 512 图标读成 Avalonia 位图（标题栏小图标 / 加载图用）。</summary>
    private static Bitmap? LoadBitmapFromCache(string embeddedName, string outName)
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache(embeddedName, outName);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return new Bitmap(path);
        }
        catch { return null; }
    }

    private static Bitmap? LoadAppIconImage() => LoadBitmapFromCache("AppIcon-512.png", "AppIcon-512.png");

    /// <summary>
    /// 打窗口图标。除了 Avalonia 的 <c>Icon</c> 之外再补一发老式的 WM_SETICON ——
    /// 有的环境下只设 Avalonia 图标不生效，任务栏 / Alt-Tab 就一直是系统默认图标。
    /// </summary>
    private void ApplyWindowIcon(IntPtr hwnd)
    {
        var icon = IconPath();
        if (icon.Length == 0) return;

        try
        {
            if (File.Exists(icon)) Icon = new WindowIcon(icon);
        }
        catch { }

        try
        {
            var big = NativeMethods.LoadImage(IntPtr.Zero, icon, NativeMethods.IMAGE_ICON, 32, 32, NativeMethods.LR_LOADFROMFILE);
            var small = NativeMethods.LoadImage(IntPtr.Zero, icon, NativeMethods.IMAGE_ICON, 16, 16, NativeMethods.LR_LOADFROMFILE);
            if (big != IntPtr.Zero) NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, NativeMethods.ICON_BIG, big);
            if (small != IntPtr.Zero) NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, NativeMethods.ICON_SMALL, small);
        }
        catch { }
    }

    private void ConfigureWindow()
    {
        // 给进程一个显式的任务栏身份：任务栏按这个 id 取图标，
        // 避免 Windows 拿旧缓存（上次那个"只有 256 一张图、缩不来"的 ico）继续显示通用图标。
        try { NativeMethods.SetCurrentProcessExplicitAppUserModelID("c1201y.ClassSoftwareHub.Desktop"); }
        catch { /* 设不上不影响使用 */ }

        var hwnd = TryGetHwnd();
        if (hwnd != IntPtr.Zero)
        {
            try { ApplyWindowIcon(hwnd); } catch { }

            // 窗口显示之后再补一次：有的环境下窗口一显示图标会被系统重置回默认
            Activated += (_, _) =>
            {
                if (_iconApplied) return;
                _iconApplied = true;
                try { ApplyWindowIcon(hwnd); } catch { }
            };
        }

        try
        {
            CanResize = true;
            Topmost = _settings.Current.AlwaysOnTop;

            // ⚠️ 存档尺寸必须**按当前屏幕夹一次**：存档里那对 1280×655 是上一台机器/上一个分辨率
            //    留下的，换台机器（或换了缩放）之后可能比现在的屏幕还大，窗口就会长到屏幕外面去，
            //    用户看到的现象正是"程序在跑，主界面不显示"（2026-10-01 实测反馈）。
            //    上下限都按屏幕算：屏幕本身就矮（老投影机 1024×768）时，下限要让步，否则窗口永远装不下。
            var primary = Screens.Primary;
            var work = primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var scale = primary?.Scaling ?? 1.0;
            if (scale <= 0) scale = 1.0;

            var workW = (int)(work.Width / scale);
            var workH = (int)(work.Height / scale);

            var maxW = Math.Max(640, workW - 40);          // 留 40dip 余量，别贴着边
            var maxH = Math.Max(480, workH - 60);
            var minW = Math.Min(900, maxW);
            var minH = Math.Min(600, maxH);

            MinWidth = minW;
            MinHeight = minH;

            Width = Math.Clamp(_settings.Current.WindowWidth, minW, maxW);
            Height = Math.Clamp(_settings.Current.WindowHeight, minH, maxH);

            // 夹完之后把真正生效的尺寸写回去，免得下次启动又拿旧的超大值来夹
            if (_settings.Current.WindowWidth != (int)Width || _settings.Current.WindowHeight != (int)Height)
            {
                _settings.Current.WindowWidth = (int)Width;
                _settings.Current.WindowHeight = (int)Height;
                try { _settings.Save(); } catch { }
            }
        }
        catch { }

        Closing += (_, _) =>
        {
            Core.AppLog.Info("exit", "主窗 Closing#1 SaveWindowState");
            SaveWindowState();
        };

        // 点右上角 × 默认收进托盘（可在设置 / 内置工具页关掉）；托盘挂不上就直接退，别把用户困在后台
        Closing += (_, args) =>
        {
            Core.AppLog.Info("exit", $"主窗 Closing: exitRequested={_exitRequested} "
                + $"closeToTray={_settings.Current.CloseToTray} trayReady={_tray?.IsReady}");

            if (_exitRequested || !_settings.Current.CloseToTray || _tray?.IsReady != true)
            {
                // 这一支是"真的要走"（托盘退出 / 用户关掉了「收进托盘」/ 托盘没挂上）。
                // ⚠️ 必须打上全局退出标记：工具浮窗与 Q 群反馈窗的 Closing 会把关闭拦成"收起来"，
                //    而 desktop.Shutdown() 撞上被取消的关闭就会中止整条退出流程 ——
                //    结果就是"窗口都关了、托盘也摘了、进程却一直不走"（2026-10-04 实测）。
                App.IsExiting = true;
                Core.AppLog.Info("exit", "主窗 Closing -> 放行（真的要走）");
                return;
            }

            args.Cancel = true;
            Core.AppLog.Info("exit", "主窗 Closing -> 取消（收进托盘）");
            HideToTray();
            ShowTrayHideHintOnce();
        };

        Resized += (_, _) => OnWindowGeometryChanged();
        PositionChanged += (_, _) => OnWindowGeometryChanged();

        // 最小化 = 用户看不见窗口了 → 把内存还给系统（教学机 8G）。
        // ⚠️ 不要挪到切页时做 —— 那条路会把 GC 卡在用户正要滚动的瞬间（见 Services/MemoryTrimmer.cs）。
        //    可见时的回收由 MemoryTrimmer 自己的巡检线程负责（水位 + 停手）。
        // ⚠️ 移植说明：原版挂 <c>AppWindow.Changed</c>（<c>args.DidPresenterChange &amp;&amp; IsMinimized()</c>）；
        //    Avalonia 没有那个回调，等价做法是监听窗口的 <c>WindowState</c> 属性变化。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
                Services.MemoryTrimmer.TrimLater(2500);
        };

        // 让内存回收知道"用户此刻看不看得见窗口"：
        //   藏进托盘 / 最小化 → 立刻收（不等水位、不等停手，这是最该收的时候）；
        //   看得见            → 走"水位 + 停手"那条路（见 Services/MemoryTrimmer.cs）。
        Services.MemoryTrimmer.IsIdle = () => !IsWindowVisible() || IsMinimized();

        // 可见时也得能收 —— 后台巡检线程就是那条路唯一的触发点（2026-10-02，
        // 起因：用户一直可见地乱点也能把占用顶到半个 G，而原来只在最小化时收）。
        // ⚠️ 必须**注完 IsIdle 再起**，否则头几轮巡检会按"看得见"判、空转。
        Services.MemoryTrimmer.StartWatch();
    }

    /// <summary>窗口大小 / 位置 / 最大化状态变了：重算网页落点、圆角、标题栏几何。</summary>
    /// <remarks>
    /// ⚠️ 2026-10-02 性能：拖拽改大小时系统每动一像素就改一次尺寸，<c>Resized</c> 与
    ///    <c>PositionChanged</c> 会**各**触发一次，这个方法原来每次都要发一遍网页脚本
    ///    （<c>ExecuteScriptAsync</c> 是跨进程往返，最贵的一项），于是拖起来一顿一顿的。
    ///    现在分两级：
    ///      · 「跟着窗口走」的极廉价项（圆角、最大化图标）立刻做；
    ///      · 重活（网页脚本 / 浮层落点）在**拖拽结束后**或**本帧末尾**合并成一次。
    /// </remarks>
    private void OnWindowGeometryChanged()
    {
        ApplyWindowRounding();               // 最大化 / 还原 → 圆角跟着切换（最大化必须直角）
        NativeShell.UpdateCaptionGlyphs();    // 自绘模式下「最大化 / 向下还原」两个图标跟着切

        // 拖拽进行中：重活全部攒着，等松手（WM_EXITSIZEMOVE）一次做完
        if (_frame?.InSizeMove == true)
        {
            _heavyGeometryPending = true;
            return;
        }

        QueueHeavyGeometryWork();
    }

    /// <summary>拖拽期间被推迟的重活还欠着没做。</summary>
    private bool _heavyGeometryPending;

    /// <summary>本帧的重活已经排过队（同一帧里 Resized + PositionChanged 会各来一次，合并掉）。</summary>
    private bool _heavyGeometryQueued;

    /// <summary>
    /// 把"重活"合并到本帧末尾做一次：
    /// <see cref="SendCaptionInsets"/>（推网页状态）、<see cref="RequestTitlebarRegions"/>（跑网页 JS）、
    /// <see cref="RepositionSheetHost"/>（挪网页宿主子窗口）。
    /// </summary>
    private void QueueHeavyGeometryWork()
    {
        if (_heavyGeometryQueued) return;
        _heavyGeometryQueued = true;

        Dispatcher.UIThread.Post(() =>
        {
            _heavyGeometryQueued = false;
            RunHeavyGeometryWork();
        }, DispatcherPriority.Background);
    }

    /// <summary>一次把攒下的重活做完（拖拽结束时也直接调它，不再等下帧）。</summary>
    private void RunHeavyGeometryWork()
    {
        _heavyGeometryPending = false;
        try
        {
            SendCaptionInsets();
            RequestTitlebarRegions();
            RepositionSheetHost();
        }
        catch
        {
            // 网页还没起来 / 宿主子窗口没建好，都不该影响窗口拖动
        }
    }

    /// <summary>
    /// 混合标题栏：应用内那条 48px 就是标题栏；三个窗口按钮由系统画（新系统）或我们自己画（Win7）。
    ///
    /// ⚠️⚠️ 标题栏方案的选择与理由（对应任务「适配点 1」）：
    ///   【Win10/11 首选】用 Avalonia 的客户区扩展还原原版观感：
    ///       ExtendClientAreaToDecorationsHint = true
    ///       ExtendClientAreaChromeHints      = PreferSystemChrome（系统三大金刚按钮由系统画）
    ///       ExtendClientAreaTitleBarHeightHint = ShellConfig.TitleBarHeight（48）
    ///   【Win7】自己接管窗口非客户区（见 <see cref="Core.SelfDrawnFrame"/>）：
    ///       理由：客户区扩展在 Win7 上要拿到「系统绘制的三大金刚按钮」得靠 DWM 的
    ///       DwmExtendFrameIntoClientArea，而教室机常见 DWM 合成关闭；一旦拿不到，用户就会得到
    ///       一个**没有最小化/最大化/关闭按钮**的窗口 —— 那比多一条原生标题栏严重得多。
    ///       做法：**一个字节的窗口样式都不改**（WS_CAPTION / WS_THICKFRAME 全留着），
    ///       只在消息层 WM_NCCALCSIZE 声明「非客户区 0 像素」、WM_NCHITTEST 自己报边缘与标题栏命中。
    ///       于是原生边框消失、48px 的应用内标题栏真正顶到窗口上沿，而
    ///       **拖拽缩放、Aero 吸附、双击最大化、任务栏右键菜单仍然由系统执行**（2026-10-02 落地）。
    ///   ⚠️ 无论哪条路，应用内 48px 的 <c>TitleBarArea</c> 结构都保持与原版一致；拖动统一走
    ///      <see cref="BeginWindowDrag"/>（ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION，原版同款，
    ///      Win7 完整可用），双击走 <see cref="ToggleWindowMaximize"/>。
    /// </summary>
    private static readonly bool UseExtendedTitleBar = !OsInfo.IsWindows7;

    /// <summary>自绘窗口框架（只在 Win7 路线上非 null；见 <see cref="AttachSelfDrawnFrame"/>）。</summary>
    private SelfDrawnFrame? _frame;

    private void ConfigureTitleBar()
    {
        if (!UseExtendedTitleBar)
        {
            AttachSelfDrawnFrame();
            ApplyWindowRounding();   // ⚠️ 必须在窗口装饰定下来之后（那一步会把圆角偏好按回 Default）
            return;
        }

        try
        {
            ExtendClientAreaToDecorationsHint = true;
            // 注意：左侧属性名与右侧类型同名，必须全限定类型，否则被实例属性遮蔽（CS0176）
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = ShellConfig.TitleBarHeight;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 标题栏定制不可用: " + ex.Message);
        }

        ApplyWindowRounding();   // ⚠️ 必须在扩展客户区之后 —— 那一步会把圆角偏好按回 Default(直角)
    }

    /// <summary>
    /// Win7 路线：自己接管窗口的非客户区 —— 原生标题栏/边框不再绘制，
    /// 应用内那条 48px 标题栏成为真正的窗口顶边，但**系统缩放与吸附原样保留**。
    ///
    /// ⚠️ 为什么可以放心大胆地挂：这条路**完全没有碰窗口样式**。万一挂载失败（后端异常等），
    ///    窗口就是一个普普通通带原生标题栏的窗口 —— 退回原生样式是「完整可用」的，
    ///    不会出现"既没有原生按钮、又没有自绘按钮"的空窗（这正是原注释担心的那种情况）。
    /// </summary>
    private void AttachSelfDrawnFrame()
    {
        try
        {
            ExtendClientAreaToDecorationsHint = false;

            _frame = SelfDrawnFrame.Attach(this);
            _frame.ResizeBorder = 6;                              // 四周 6dip 的可拖拽缩放带
            _frame.TitleBarHeight = ShellConfig.TitleBarHeight;   // 48dip 以内返回 HTCAPTION
            _frame.InteractiveRegions = TitleBarInteractiveRegions;

            // 缩放时"新暴露的那条带"要填成页面底色 —— Avalonia 的窗口类背景刷是 NULL（实测），
            // 不填就会留上一帧的残影，在无 DWM 合成的机器上看着就像"原生边框又冒出来了"。
            // 颜色每次擦的时候现取，换主题自动生效。
            _frame.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(RootGrid);

            // 拖拽缩放结束（鼠标松开）→ 把拖拽期间攒下的重活一次做完，界面立刻收敛到正确状态。
            // 这就是"拖起来顺滑、松手后图标/网页落点立刻对齐"的来源。
            _frame.SizeMoveFinished += (_, _) =>
            {
                if (_heavyGeometryPending) RunHeavyGeometryWork();
            };

            // 系统不再提供三大金刚了，改由应用内标题栏自己画
            NativeShell.UseSelfDrawnCaption();

            Platform.PortLog.Step("MainWindow: 已接管非客户区（自绘标题栏 48dip，系统缩放/吸附保留）");
        }
        catch (Exception ex)
        {
            _frame = null;
            Debug.WriteLine("[shell] 自绘窗口框架挂载失败，退回原生标题栏: " + ex.Message);
            Platform.PortLog.Step("MainWindow: 自绘窗口框架挂载失败，退回原生标题栏 - " + ex.Message);
        }
    }

    /// <summary>
    /// 标题栏里「点了不该拖动窗口」的区域（dip，窗口坐标）。
    /// <see cref="SelfDrawnFrame"/> 用它把按钮从 HTCAPTION 区域里挖出来 ——
    /// 不挖的话，点主题切换键/最小化键会被系统当成拖窗口，点击直接丢失。
    /// </summary>
    private IReadOnlyList<Rect> TitleBarInteractiveRegions()
    {
        var list = new List<Rect>(4);

        foreach (var control in NativeShell.CaptionInteractiveControls)
        {
            try
            {
                if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) continue;

                var origin = control.TranslatePoint(new Point(0, 0), this);
                if (origin is null) continue;

                list.Add(new Rect(origin.Value.X, origin.Value.Y,
                                  control.Bounds.Width, control.Bounds.Height));
            }
            catch
            {
                // 控件树正在重建时可能取不到坐标：跳过这一个就行，顶多那一下点不动
            }
        }

        return list;
    }

    /// <summary>
    /// 主窗口四角圆角（Win 11 原生观感）。
    ///
    /// 为什么得手动兜：一旦扩展进标题栏，窗口圆角偏好就留在 Default —— 实测 Win 11 上就是**直角**，
    /// 和系统别处的窗口并排看很出戏。显式写 ROUND 即可复原（见 WindowChrome.SetRounded 的实测记录）。
    ///
    /// ⚠️ 最大化时反而要写 DONOTROUND：贴边的窗口不该削角，万一某个版本认了 ROUND，
    ///    屏幕四角就会露出底下的桌面 —— 全屏时钟当初踩的就是这个坑。
    /// </summary>
    private void ApplyWindowRounding()
    {
        try
        {
            var hwnd = TryGetHwnd();
            if (hwnd == IntPtr.Zero) return;
            WindowChrome.SetRounded(hwnd, WindowState != WindowState.Maximized);
        }
        catch { }
    }

    private double GetScale() => RenderScaling > 0 ? RenderScaling : 1.0;

    private IntPtr TryGetHwnd() => Backdrop.TryGetHwnd(this);

    /// <summary>
    /// 启动自检：把「这台机器上程序到底长什么样」一次性写进 <c>startup.log</c>。
    ///
    /// 为什么要有这个：装机环境千差万别（尤其 Win7），远端排障来回问十句不如一份日志。
    /// 一次记录就能回答"主窗口到底显示没有 / 托盘挂上没有 / 侧边栏在哪 / 显示器怎么摆的"，
    /// 这几项恰好是"点了没反应、界面不显示、托盘没图标"这类症状的分水岭。
    /// </summary>
    private void WriteStartupTrace(string stage)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"===== 启动自检（{stage}） {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} =====");
            sb.AppendLine("系统: " + Platform.OsInfo.Describe()
                          + $" | DWM合成={(Platform.OsInfo.IsDwmCompositionEnabled ? "开" : "关")}"
                          + $" | 窗口缩放={(RenderScaling * 100):0}%");

            var screens = Screens.All;
            sb.AppendLine($"显示器: {screens.Count} 台");
            foreach (var s in screens)
                sb.AppendLine($"   · 边界=({s.Bounds.X},{s.Bounds.Y})-({s.Bounds.Right},{s.Bounds.Bottom})"
                              + $" 工作区=({s.WorkingArea.X},{s.WorkingArea.Y})-({s.WorkingArea.Right},{s.WorkingArea.Bottom})"
                              + $" 缩放={s.Scaling * 100:0}% 主屏={s.IsPrimary}");

            var hwnd = TryGetHwnd();
            var onScreen = "?";
            var rect = "(拿不到矩形)";
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
            {
                rect = $"({r.Left},{r.Top})-({r.Right},{r.Bottom})";
                var box = new PixelRect(r.Left, r.Top, r.Width, r.Height);
                foreach (var s in screens)
                {
                    var inter = s.WorkingArea.Intersect(box);
                    if (inter.Width > 40 && inter.Height > 40) { onScreen = "是"; break; }
                    onScreen = "否（界面会看不见）";
                }
            }
            sb.AppendLine($"主窗口: 可见={IsVisible} 状态={WindowState} 矩形={rect} 在屏幕内={onScreen} hwnd={hwnd}");

            sb.AppendLine($"托盘图标: 主界面={_tray?.IsReady} 常用工具={_trayTools?.IsReady}"
                          + "（false = 没挂上，此时点 × 直接退出、不会把用户困在后台）");

            var sbr = Views.ToolSidebarWindow.CurrentRect;
            sb.AppendLine($"工具侧边栏: 矩形={(sbr is null ? "(没显示)" : $"({sbr.Value.X},{sbr.Value.Y})-({sbr.Value.Right},{sbr.Value.Bottom})")}"
                          + $" 设置=开:{_settings.Current.SidebarEnabled} 边:{_settings.Current.SidebarEdge} 模式:{_settings.Current.SidebarMode}");

            sb.AppendLine($"设置: 材质={_settings.Current.Backdrop} 主题={_settings.Current.Theme}"
                          + $" 关窗收托盘={_settings.Current.CloseToTray} 开机最小化启动={_startMinimized}");
            sb.AppendLine();

            var path = Path.Combine(Services.SettingsStore.Dir, "startup.log");
            Directory.CreateDirectory(Services.SettingsStore.Dir);
            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }

    /// <summary>是否是当前可见的窗口（托盘"显示/收起"切换用）。</summary>
    private bool IsWindowVisible()
    {
        try { return NativeMethods.IsWindowVisible(TryGetHwnd()); }
        catch { return IsVisible; }
    }

    /// <summary>
    /// 窗口此刻是不是最小化状态（托盘隐藏走的另一条路，见 <see cref="HideToTray"/>）。
    /// ⚠️ 移植说明：原版判 <c>AppWindow.Presenter is OverlappedPresenter { State: Minimized }</c>；
    ///    Avalonia 没有 presenter 那套，等价判据是 <see cref="Window.WindowState"/>。
    /// </summary>
    private bool IsMinimized() => WindowState == WindowState.Minimized;

    /// <summary>
    /// 把右侧标题栏占位（系统按钮宽度）报给网页。
    /// ⚠️ 移植说明：原版从 <c>AppWindow.TitleBar.RightInset/LeftInset/Height</c> 取真实值；
    ///    Avalonia 没有这套 API，这里按 <c>SystemOverlay</c> 的常见值给个近似（并与缩放对齐）。
    ///    因为网页只出现在 WebSheet 浮层里，这个值仅用于网页顶栏留白，近似即可。
    /// </summary>
    private void SendCaptionInsets()
    {
        try
        {
            var scale = GetScale();
            // 系统画三大金刚时右侧占位 ≈138；自绘模式下是「主题切换键 + 三个 46dip 方块」≈188
            var right = UseExtendedTitleBar ? 138d : 188d;
            _bridge.Send("shell.captionInset", new
            {
                value = right,
                height = ShellConfig.TitleBarHeight,
                left = 0d,
                scale
            });
        }
        catch { }
    }

    private void RequestTitlebarRegions()
        => _ = ExecuteScriptAsync("window.cshShell && window.cshShell.post('titlebarUpdate');");

    // ============================================================
    // 拖动 / 最大化
    // ============================================================

    /// <summary>
    /// 网页判定「这里是空白拖动区」后调用：
    /// ReleaseCapture() 释放鼠标捕获，再 SendMessage(WM_NCLBUTTONDOWN, HTCAPTION) 把事件重定向到非客户区，
    /// 交给系统的窗口拖动机制（含贴边、双击最大化）。
    ///
    /// ⚠️ 与 PORTING.md 说的 <c>Window.BeginMoveDrag(PointerPressedEventArgs)</c> 的区别：
    ///    那条 API 需要一个指针事件参数，而本方法是从**网页桥接消息**里调用的（没有事件参数）；
    ///    原版这里本来就是 Win32 做法，且在 Win7 上完整可用，所以照搬原版实现（语义一字不差）。
    /// </summary>
    public void BeginWindowDrag()
    {
        try
        {
            var hwnd = TryGetHwnd();
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 拖动失败: " + ex.Message);
        }
    }

    /// <summary>双击空白区：在 Maximize / Restore 间切换。</summary>
    public void ToggleWindowMaximize() => HandleWindowCommand("toggleMaximize");

    /// <summary>
    /// 网页算好的「可拖动矩形」。
    /// 默认不注册成原生 caption 区域（按规格书走网页→原生消息），
    /// 但保留开关 settings.NativeCaptionRegions 以便对比手感。
    /// </summary>
    public void ApplyTitleBarRegions(JsonObject payload)
    {
        _webCaptionRects.Clear();

        if (_settings.Current.NativeCaptionRegions)
        {
            var scale = GetScale();
            if (payload["caption"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject o) continue;
                    var x = Num(o, "x");
                    var y = Num(o, "y");
                    var w = Num(o, "w");
                    var h = Num(o, "h");
                    if (w < 8 || h < 8) continue;
                    _webCaptionRects.Add(new Rect(x * scale, y * scale, w * scale, h * scale));
                }
            }
        }

        ApplyCaptionRegions();
    }

    private static double Num(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<double>() ?? 0; }
        catch { return 0; }
    }

    /// <summary>网页上报主题：原生跟着走（系统按钮颜色对齐）。</summary>
    public void OnWebThemeReported(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return;
        var normalized = theme.Trim().ToLowerInvariant();
        if (normalized is not ("light" or "dark")) return;

        _themeFromWeb = true;
        var variant = normalized == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        if (RequestedThemeVariant != variant)
        {
            // 网页驱动的主题：直接设本窗口（Window 是 TopLevel，有 RequestedThemeVariant）
            RequestedThemeVariant = variant;
            if (_settings.Current.Backdrop == "solid")
                RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
            else
                ApplyBackdrop(_settings.Current.Backdrop);
        }
    }

    private void SaveWindowState()
    {
        try
        {
            if (WindowState == WindowState.Maximized) return;
            _settings.Current.WindowWidth = (int)Width;
            _settings.Current.WindowHeight = (int)Height;
            _settings.Save();
        }
        catch { }
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        Core.AppLog.Info("exit", $"主窗口 Closed (exitRequested={_exitRequested})");
        _loadTimer.Stop();
        DownloadManager.Current.Finished -= OnDownloadFinished;
        try { _settings.Save(); } catch { }
        try { _sheetHost?.Dispose(); } catch { }
        _sheetHost = null;
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        try { _trayTools?.Dispose(); } catch { }
        _trayTools = null;

        // 走到这儿说明主窗口是**真的关掉了**（"收进托盘"那条路在 Closing 里被 cancel，压根到不了 Closed）
        // → 应用就该退出了。
        // ⛔ Avalonia 也是"主窗关了不等于进程退"：侧边栏 / 工具浮窗还活着，消息循环就被撑住，
        //    进程会一直挂在任务管理器里。2026-10-04 实测：把「关闭时收进托盘」关掉后点 ×
        //    （= 任务栏右键「关闭窗口」）必现残留。所以这里自己把剩下的窗口收掉并请应用退出。
        //    已经由 ExitApp 发起时（_exitRequested 已是 true）不重复走 —— 那条路自己会收尾。
        if (!_exitRequested)
        {
            _exitRequested = true;
            App.IsExiting = true;
            FinishExit();
        }
    }

    private void LoadLoadingIcon()
    {
        try { LoadingIcon.Source = LoadBitmapFromCache("AppIcon-512.png", "AppIcon-512.png"); }
        catch { }
    }

    // ============================================================
    // caption 区域（原生侧，可选）
    // ============================================================

    /// <summary>
    /// ⚠️ 移植说明：原版用 WinUI 的 <c>InputNonClientPointerSource.SetRegionRects(Caption, …)</c> 把网页上报的
    ///    拖动区注册成原生 caption；**Avalonia 没有等价 API**。这里保留字段与解析逻辑（配置开关仍能记录用户意图），
    ///    但注册动作退化为 no-op —— 拖动完全走「网页判断 + 桥接消息」的规格书路径（原版默认路径）。
    /// </summary>
    private void ApplyCaptionRegions()
    {
        // no-op（见方法注释）。保留 _webCaptionRects 供排障时查看。
    }

    // ============================================================
    // 外观：背景（亚克力 / Mica / 纯色）与主题
    // ============================================================

    /// <summary>切换背景材质（网页 / 设置页调）。</summary>
    public void SetBackdrop(string kind)
    {
        ApplyBackdrop(kind);
        _settings.Save();
    }

    /// <summary>
    /// 窗口背景：一律交给 <see cref="Backdrop.Apply"/>（它按系统分级降级 Mica → Acrylic → Win7 Aero 毛玻璃 → 纯色）。
    /// ⚠️ 不要在这里自己写 SystemBackdrop 等价逻辑，也别绕过 Backdrop 直接调 DWM。
    /// </summary>
    private void ApplyBackdrop(string kind)
    {
        var applied = Backdrop.Apply(this, RootGrid, kind);
        _settings.Current.Backdrop = applied;
        _bridge.Send("shell.backdropChanged", new { value = applied });
    }

    private Color FallbackSolidColor()
        => RootGrid.ActualThemeVariant == ThemeVariant.Dark
            ? Color.FromArgb(255, 32, 32, 32)
            : Color.FromArgb(255, 243, 243, 243);

    /// <summary>切换主题（网页 / 设置页调）。</summary>
    public void SetTheme(string theme)
    {
        ApplyTheme(theme, notifyWeb: true);
        _settings.Save();
    }

    private void ApplyTheme(string theme, bool notifyWeb = false)
    {
        theme = string.IsNullOrWhiteSpace(theme) ? "system" : theme.Trim().ToLowerInvariant();
        _settings.Current.Theme = theme;

        // ⚠️ 实测核验：RequestedThemeVariant 不在 StyledElement 上，只在 Application / TopLevel / ThemeVariantScope。
        //    本窗口自己是 TopLevel（Window），直接设它即可；再 Notify 一次让登记过的外部组件（侧栏/浮窗/截图窗）一起换。
        //    ⚠️ 本窗口的根必须登记成 **main 作用域**（2026-10-03 修）：以前所有根共用一个"当前设置"，
        //    开了「外部组件单独外观」后 Notify 会把外部组件的外观也算给主窗口 —— 浅色主界面上
        //    全是深色那套的白字，整个设置页白底白字看不清（用户截图实锤）。
        RequestedThemeVariant = ThemeCompat.Map(theme);
        ThemeCompat.Apply(RootGrid, mainScope: true);   // 登记本窗口根（幂等），后续 Notify 才会把它算进去
        ThemeCompat.Notify();

        if (_settings.Current.Backdrop == "solid")
            RootGrid.Background = new SolidColorBrush(FallbackSolidColor());
        else
            ApplyBackdrop(_settings.Current.Backdrop);   // 主题变了 → tint 颜色跟着重算

        Services.ThemeBrush.Probe(RootGrid, "MainWindow.ApplyTheme(" + theme + ")");
        _bridge.Send("shell.themeChanged", new { value = theme, actual = RootGrid.ActualThemeVariant.ToString() });

        if (notifyWeb)
            _bridge.Send("shell.applyWebTheme", new { value = theme });
    }

    public void SetWebTransparent(bool transparent)
    {
        _settings.Current.WebTransparent = transparent;
        _bridge.Send("shell.webTransparent", new { value = transparent });
        _settings.Save();
    }

    public void SetTitleBarColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        try { _settings.Current.TitleBarTint = hex.Trim(); } catch { }
    }

    public void SetWebZoom(double zoom)
    {
        _settings.Current.WebZoom = Math.Clamp(zoom, 0.5, 3.0);
        RepositionSheetHost();
        _ = ExecuteScriptAsync(
            $"document.documentElement.style.zoom = '{_settings.Current.WebZoom.ToString(CultureInfo.InvariantCulture)}';");
        _settings.Save();
    }

    // ============================================================
    // 启动 / WebView2 初始化
    // ============================================================

    private Task BootAsync()
    {
        // 原生界面完全不依赖 WebView2 —— 所以缺了也照常进主界面。
        // 只有应用内网页浮层（WebSheet）需要它，那边会给「去安装 / 用浏览器打开」的兜底。
        _runtime.Probe();
        RuntimePanel.IsVisible = false;

        // 记一笔"这台电脑运行过的版本"（更新页的「历史版本」读的就是这份记录）
        Services.VersionHistory.Record();

        // 原生界面优先：启动时不加载任何网页。
        return Task.CompletedTask;
    }

    private void ShowNetworkError(string? tip = null)
    {
        _loadTimer.Stop();
        LoadingRing.IsActive = false;
        LoadingPanel.IsVisible = false;
        ErrorTitle.Text = ShellConfig.NetworkErrorMessage;
        ErrorTip.Text = tip ?? string.Empty;
        ErrorPanel.IsVisible = true;
    }

    // ============================================================
    // WebView2 缺失：弹窗三选项
    // ============================================================

    private void HandleMissingRuntime()
    {
        var choice = _settings.Current.WebView2MissingChoice;
        if (choice == "browser")
        {
            OpenExternal(ShellConfig.SiteUrl);
            ExitApp();
            return;
        }

        if (choice == "install")
            OpenExternal(ShellConfig.WebView2DownloadUrl);

        ErrorPanel.IsVisible = false;
        LoadingRing.IsActive = false;
        LoadingPanel.IsVisible = false;
        RuntimePanel.IsVisible = true;
        if (NoPromptCheck is not null && choice.Length > 0)
            NoPromptCheck.IsChecked = true;
    }

    private void InstallRuntime_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (NoPromptCheck?.IsChecked == true) _settings.Current.WebView2MissingChoice = "install";
        _settings.Save();
        OpenExternal(ShellConfig.WebView2DownloadUrl);
    }

    private void OpenInBrowser_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (NoPromptCheck?.IsChecked == true) _settings.Current.WebView2MissingChoice = "browser";
        _settings.Save();
        OpenExternal(ShellConfig.SiteUrl);
        ExitApp();
    }

    private void ExitApp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ExitApp();

    private void RetryButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_webReady)
        {
            _ = BootAsync();
            return;
        }
    }

    // ============================================================
    // 桥接动作
    // ============================================================

    public void HandleWindowCommand(string action)
    {
        switch (action)
        {
            case "minimize":
                WindowState = WindowState.Minimized;
                break;
            case "maximize":
                WindowState = WindowState.Maximized;
                break;
            case "restore":
                WindowState = WindowState.Normal;
                break;
            case "toggleMaximize":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "close":
                Close();
                break;
        }
        _bridge.Send("shell.state", BuildStatePayload());
    }

    public void SetAlwaysOnTop(bool value)
    {
        _settings.Current.AlwaysOnTop = value;
        Topmost = value;
        _settings.Save();
        _bridge.Send("shell.state", BuildStatePayload());
    }

    public void SetTelemetryEnabled(bool value)
    {
        _settings.Current.TelemetryEnabled = value;
        _settings.Current.TelemetryAsked = true;
        _settings.Save();
    }

    public void SetAutoStart(bool value)
    {
        _settings.Current.AutoStart = value;
        ApplyAutoStart();
        _settings.Save();
        _bridge.Send("shell.state", BuildStatePayload());
    }

    /// <summary>开机最小化启动（依赖开机自启：不打开自启时不会被用到）。</summary>
    public void SetMinimizeOnStart(bool value)
    {
        _settings.Current.MinimizeOnStart = value;
        ApplyAutoStart();
        _settings.Save();
        _bridge.Send("shell.state", BuildStatePayload());
    }

    /// <summary>点关闭时收进托盘（true）/ 直接退出（false）。</summary>
    public void SetCloseToTray(bool value)
    {
        _settings.Current.CloseToTray = value;
        _settings.Save();
    }

    /// <summary>工具浮窗是否始终置顶。</summary>
    public void SetPaletteOnTop(bool value)
    {
        _settings.Current.PaletteOnTop = value;
        _settings.Save();
        Views.ToolPaletteWindow.ApplyOnTopSetting();
    }

    // ============================================================
    // 托盘图标：主窗口藏起来、工具浮窗、退出都从这儿走
    // ============================================================

    private void InitTray()
    {
        try
        {
            // 托盘图标一：主界面（左键 = 主窗口显示/收起）
            _tray = new Win32TrayIcon(IconPath());
            _tray.LeftClick += ToggleMainWindow;
            _tray.CommandInvoked += OnTrayCommand;
            _tray.BalloonClicked += OnBalloonClicked;
            _tray.MenuItems.Add(new TrayMenuItem { Text = "打开主界面", Command = "show", IsDefault = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "常用工具", Command = "palette" });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "工具侧边栏", Command = "sidebar" });
            _tray.MenuItems.Add(new TrayMenuItem { Separator = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "检查更新", Command = "update" });
            _tray.MenuItems.Add(new TrayMenuItem { Separator = true });
            _tray.MenuItems.Add(new TrayMenuItem { Text = "退出", Command = "exit" });

            if (!_tray.Setup(ShellConfig.WindowTitle))
                Debug.WriteLine("[tray] 托盘图标没挂上（挂不上时关闭窗口仍然直接退出）");

            // 托盘图标二：常用工具（左键 = 直接开工具窗口，不用先进主界面）—— 学校电脑是触屏，少点几下
            _trayTools = new Win32TrayIcon(ToolIconPath());
            _trayTools.LeftClick += () => Views.ToolPaletteWindow.TogglePalette();
            _trayTools.CommandInvoked += OnTrayCommand;
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "打开常用工具", Command = "palette", IsDefault = true });
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "打开主界面", Command = "show" });
            _trayTools.MenuItems.Add(new TrayMenuItem { Separator = true });
            _trayTools.MenuItems.Add(new TrayMenuItem { Text = "退出", Command = "exit" });

            if (!_trayTools.Setup("常用工具"))
                Debug.WriteLine("[tray] 第二个托盘图标没挂上");

            // 屏幕右边那条工具侧边栏（全屏放 PPT 时也够得着工具）
            Views.ToolSidebarWindow.ApplySetting();

            // 记着"上一次在用的窗口"（点了侧边栏之后前台就变成我们自己了，"关前台应用"得知道原本是谁）
            Services.TeachingActions.StartFocusWatcher();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[tray] 初始化失败: " + ex.Message);
        }
    }

    /// <summary>通知被点开：按"这条通知是关于什么的"分流（更新 / 下载完成 / 下载失败）。</summary>
    private void OnBalloonClicked()
    {
        var action = _balloonAction;
        _balloonAction = null;

        if (action is not null)
        {
            try { action(); } catch { }
            return;
        }

        // 没登记动作的（老路子）：把界面叫出来 + 顺手查一次更新
        ShowFromTray();
        _ = CheckUpdateManualAsync();
    }

    private void OnTrayCommand(string command)
    {
        switch (command)
        {
            case "show":
                ShowFromTray();
                break;
            case "palette":
                Views.ToolPaletteWindow.ShowTool();
                break;
            case "sidebar":
                Views.ToolSidebarWindow.ShowSidebar();
                break;
            case "update":
                ShowFromTray();
                _ = CheckUpdateManualAsync();
                break;
            case "exit":
                ExitApp();
                break;
        }
    }

    /// <summary>左键点托盘图标：主窗口显示/收起来回切。</summary>
    public void ToggleMainWindow()
    {
        try
        {
            if (IsWindowVisible()) HideToTray();
            else ShowFromTray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[tray] 切换窗口失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 窗口可见性看门狗：主窗口"本该看得见、系统侧却是隐藏状态"时，把它拉回来。
    ///
    /// 为什么需要（2026-10-01 实机日志给的证据）：
    ///   出问题的那个实例，枚举它的顶层窗口是这样的 ——
    ///       · 可见 40x220   标题=「工具侧边栏」
    ///       · 隐藏 2480x1135 标题=「ClassSoftwareHub」   ← 主窗口是"隐藏"的
    ///   也就是说，用户看到的「主界面不显示、只有侧边栏可用」，
    ///   本质就是**主窗口停在隐藏状态**，而侧边栏是另一个顶层窗口、照样露着。
    ///
    /// ⚠️ 判据必须问 Win32（<see cref="Platform.WindowProbe.IsShown"/>），
    ///    不能看 Avalonia 的 <c>IsVisible</c> —— 外部 ShowWindow(SW_HIDE) 时它是不会更新的。
    ///
    /// 纪律：用户/启动参数**故意**收起来的窗口（<see cref="_hiddenToTray"/>、--minimized、--palette）
    /// 绝对不去碰。本看门狗只负责"没道理地不见了"这一种情况。
    /// </summary>
    private void StartWindowVisibilityWatchdog()
    {
        if (_startMinimized || _startPalette) return;   // 这两种模式一开始就是故意不收脸的

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (_exitRequested || _hiddenToTray) return;     // 是我们自己收的 → 别多管闲事

                var hwnd = TryGetHwnd();
                if (hwnd == IntPtr.Zero) return;                 // 窗口还没建出来 → 交给 App 的启动看门狗

                if (!Platform.WindowProbe.IsShown(hwnd))
                {
                    // 只记前几次：真修不好时会每 5 秒命中一次，别把 port.log 刷爆
                    if (_visibleWatchdogHits++ < 3)
                        Platform.PortLog.Step("可见性看门狗: 主窗口在系统侧处于隐藏状态 → 强制拉出来");
                    ShowFromTray();
                }
            }
            catch { /* 看门狗自己绝不能把程序带崩 */ }
        };
        timer.Start();
    }

    /// <summary>主窗口藏进托盘（任务栏上不留最小化按钮）。</summary>
    public void HideToTray()
    {
        _hiddenToTray = true;
        try { Hide(); } catch { }
        try { _sheetHost?.Hide(); } catch { }
        // 藏起来没人看的时候把内存还给系统（教学机 8G，不能白白占着）
        Services.MemoryTrimmer.TrimLater(1200);
    }

    /// <summary>
    /// 第一次「点 × 收进托盘」时提示一次（只提示一次，之后不再打扰）。
    ///
    /// 用户反馈（2026-09-30）：关掉窗口后以为程序退了，去点桌面图标毫无反应，
    /// 托盘图标又不一定在看得见的地方 —— 只能去任务管理器。这条提示把"程序还在后台"
    /// 与"两条回来的路"一次讲清，点气泡本身也直接叫回窗口。
    /// </summary>
    private void ShowTrayHideHintOnce()
    {
        if (_settings.Current.TrayHideHintShown) return;

        _settings.Current.TrayHideHintShown = true;
        try { _settings.Save(); } catch { }

        ShowBalloon("程序仍在后台运行",
            "点「×」是把窗口收进托盘，程序并没有退出。点托盘图标或桌面图标都可以重新打开主界面。",
            () => ShowFromTray());
    }

    /// <summary>从托盘把主窗口叫回来。</summary>
    public void ShowFromTray()
    {
        try
        {
            _hiddenToTray = false;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();

            var hwnd = TryGetHwnd();
            if (hwnd != IntPtr.Zero)
            {
                // ⚠️⚠️ 只靠 Avalonia 的 Show() **救不回被外部/异常路径隐藏的窗口**：
                //     Avalonia 自己以为窗口已经显示，于是 Show() 变成空操作，系统侧那道
                //     「隐藏」状态就永远是隐藏的。
                //
                //     2026-10-01 实机就是这个状态：用户双击 → 旧实例确实收到了唤醒请求 →
                //     也确实调了 ShowFromTray → 界面照样不出现。本机也复现出同样结果：
                //     看门狗每 5 秒都检测到"隐藏"，调 ShowFromTray 却拉不回来。
                //
                //     所以这里必须再压一道 Win32，把**系统侧**的状态硬掰回来。
                //     SW_RESTORE 对"隐藏"和"最小化"两种情况都管用（激活并显示、必要时还原尺寸）。
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[tray] 显示窗口失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 工具浮窗里的「详细设置」：把主界面叫出来，并直接跳到对应的工具页
    /// （浮窗太小，放不下那些自定义项 —— 但用户想细调时得有条路进去）。
    /// </summary>
    public void OpenToolSettings(Type pageType)
    {
        try
        {
            ShowFromTray();
            Shell.NavigateToTool(pageType);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[palette] 打开工具设置失败: " + ex.Message);
        }
    }

    /// <summary>托盘菜单「检查更新」：查到新版就走更新流程，没有就明确说一声。</summary>
    private async Task CheckUpdateManualAsync()
    {
        try
        {
            var service = Services.Updating.UpdateService.CreateDefault();
            if (!service.Source.IsConfigured) return;

            var channel = Services.Updating.UpdateChannels.Parse(_settings.Current.UpdateChannel);
            var result = await service.CheckAsync(channel, ShellConfig.ShellVersion);

            if (result is { HasUpdate: true, Release: { } release })
            {
                // 一样先问，绝不替用户做主（返回值是三态：Later / Now / Background）
                var choice = await Services.Updating.UpdateFlow.AskAsync(release, this);
                if (choice == Services.Updating.UpdateFlow.UpdateChoice.Now)
                    await Services.Updating.UpdateFlow.RunAsync(service, release, owner: this);
                else if (choice == Services.Updating.UpdateFlow.UpdateChoice.Background)
                    Services.Updating.UpdateFlow.StartBackgroundDownload(service, release);
                return;
            }

            await new AvaloniaDialog
            {
                Title = "当前已是最新版本",
                Content = $"当前：dv{ShellConfig.ShellVersion}\n通道：{Services.Updating.UpdateChannels.ToDisplay(channel)}",
                CloseButtonText = "确定",
            }.ShowAsync(this);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[update] 检查更新失败: " + ex.Message);
        }
    }

    /// <summary>把自启项写进注册表 Run；开着「开机最小化」时附带 --minimized 参数。</summary>
    private void ApplyAutoStart()
    {
        try
        {
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "ClassSoftwareHubDesktop";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null) return;

            if (_settings.Current.AutoStart)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                var cmd = _settings.Current.MinimizeOnStart ? $"\"{exe}\" --minimized" : $"\"{exe}\"";
                key.SetValue(valueName, cmd);
            }
            else if (key.GetValue(valueName) is not null)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 开机自启设置失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 把链接交给系统浏览器处理（只有「必须用外部程序」的场景才该走这里）。
    ///
    /// ⚠️ 必须限协议。这里最终是 `Process.Start(UseShellExecute = true)`，等于把 URL 直接交给
    /// shell 解析 —— 放行任意协议的话，一个 `file:///C:/Windows/...` 或者攻击者自定义注册的协议
    /// 就能拉起本机程序。而 URL 的来源（软件条目的 website、网页浮层里的链接）并不都是我们自己写的，
    /// 所以这里只留真正需要的三个，其余一律拒绝并记日志。
    /// </summary>
    public void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        if (!AllowedExternalSchemes.Contains(uri.Scheme))
        {
            Debug.WriteLine($"[shell] 拒绝打开非白名单协议的外部链接: {uri.Scheme}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 打开外部链接失败: " + ex.Message);
        }
    }

    /// <summary>允许交给系统的协议：网页、微软商店。其余（file / 自定义协议等）不放行。</summary>
    private static readonly HashSet<string> AllowedExternalSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "ms-windows-store",
    };

    /// <summary>
    /// 打开 Microsoft Store 链接：先转成商店协议拉起身上的「微软商店」应用；
    /// 转不出来（不是商店链接）就当普通网页开在浮层里；商店应用不在再退回浏览器。
    /// </summary>
    public void OpenStore(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            OpenExternal(url);
            return;
        }

        var storeUri = Core.StoreLink.ToStoreUri(url);
        if (storeUri is null)
        {
            ShowWebSheet(url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(storeUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 打开商店失败，改开网页: " + ex.Message);
            OpenExternal(url);
        }
    }

    /// <summary>
    /// 用系统的默认关联打开一个本地文件（刚下载完的安装包之类）。
    /// ⚠️ 故意不加 shell 参数：安装包会按自己的清单弹 UAC，我们不该替它提权。
    /// </summary>
    public void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 打开文件失败: " + ex.Message);
        }
    }

    /// <summary>在资源管理器里定位到某个文件。</summary>
    public void RevealFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[shell] 定位文件失败: " + ex.Message);
        }
    }

    // ============================================================
    // 应用内网页浮层（官网 / 网页版）：由下往上淡入的「窗中窗」
    // ============================================================

    /// <summary>
    /// 在应用内浮层里打开一个网页（http/https）。缺 WebView2 时退化成「去安装 / 用浏览器打开」。
    ///
    /// ⚠️ 移植说明：网页宿主由 <see cref="Views.WebSheetHost"/>（子窗口 + CoreWebView2）承担，
    ///    它建不出来（没有运行时 / 建窗失败）时会**立刻退回系统浏览器**（原版没有这条，因为原版一定有控件）。
    /// </summary>
    public async void ShowWebSheet(string? url, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var href = url.Trim();

        if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            OpenExternal(href);   // ms-windows-store: 这类协议只能交给系统
            return;
        }

        if (!_runtime.IsAvailable && !_runtime.Probe())
        {
            await ShowRuntimeMissingDialogAsync(href);
            return;
        }

        _sheetUrl = href;
        if (!string.IsNullOrWhiteSpace(title))
        {
            WebSheetTitle.Text = title!;
        }
        else
        {
            try { WebSheetTitle.Text = new Uri(href).Host; } catch { WebSheetTitle.Text = "网页"; }
        }

        WebSheetRing.IsActive = true;
        WebSheet.IsVisible = true;
        PlaySheetAnimation(show: true);

        try
        {
            if (!_sheetReady)
            {
                var hwnd = TryGetHwnd();
                var host = await Views.WebSheetHost.CreateAsync(hwnd, _runtime);
                if (host is null)
                {
                    // 宿主建不出来 → 别白屏，直接用系统浏览器打开
                    CloseWebSheet();
                    OpenExternal(href);
                    return;
                }

                _sheetHost = host;
                _sheetHost.NavigationStarting += () => Dispatcher.UIThread.Post(() => WebSheetRing.IsActive = true);
                _sheetHost.NavigationCompleted += ok => Dispatcher.UIThread.Post(() =>
                {
                    WebSheetRing.IsActive = false;
                    if (!ok) WebSheetTitle.Text = "网页无法打开";
                });
                _sheetHost.TitleChanged += t => Dispatcher.UIThread.Post(() =>
                {
                    if (!string.IsNullOrWhiteSpace(t)) WebSheetTitle.Text = t;
                });
                _sheetHost.NewWindowRequested += u => Dispatcher.UIThread.Post(() =>
                {
                    if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _sheetHost?.Navigate(u);
                    else OpenExternal(u);
                });
                _sheetHost.WebMessageReceived += json => _bridge.HandleWebMessage(json);

                _sheetReady = true;
            }

            _sheetHost?.Navigate(href);
            RepositionSheetHost();
        }
        catch (Exception ex)
        {
            WebSheetRing.IsActive = false;
            WebSheetTitle.Text = "网页无法打开：" + ex.Message;
        }
    }

    /// <summary>把网页宿主摆到 <c>SheetWeb</c> 占位块的位置（父窗口客户区坐标，物理像素）。</summary>
    private void RepositionSheetHost()
    {
        if (_sheetHost is null) return;
        try
        {
            if (!WebSheet.IsVisible) { _sheetHost.Hide(); return; }

            var p = SheetWeb.TranslatePoint(new Point(0, 0), this);
            if (p is null) return;

            var scale = GetScale();
            var size = SheetWeb.Bounds.Size;
            _sheetHost.ShowAt(
                (int)Math.Round(p.Value.X * scale),
                (int)Math.Round(p.Value.Y * scale),
                (int)Math.Round(size.Width * scale),
                (int)Math.Round(size.Height * scale),
                scale);
        }
        catch { }
    }

    /// <summary>显示/收起浮层：位移 + 透明度一起动（由下往上淡入）。</summary>
    private void PlaySheetAnimation(bool show)
    {
        try
        {
            if (show)
            {
                WebSheetCard.Opacity = 0;
                WebSheetShift.Y = 150;
                RunAnim(WebSheetCard, Visual.OpacityProperty, 0, 1, 220, true,
                    () => { WebSheetCard.Opacity = 1; RepositionSheetHost(); });
                RunAnim(WebSheetShift, TranslateTransform.YProperty, 150, 0, 320, true, () => WebSheetShift.Y = 0);
            }
            else
            {
                RunAnim(WebSheetCard, Visual.OpacityProperty, WebSheetCard.Opacity, 0, 160, false,
                    () =>
                    {
                        WebSheetCard.Opacity = 0;
                        WebSheet.IsVisible = false;
                        _sheetHost?.Hide();
                    });
                RunAnim(WebSheetShift, TranslateTransform.YProperty, WebSheetShift.Y, 150, 190, false,
                    () => WebSheetShift.Y = 150);
            }
        }
        catch
        {
            // 动画起不来就直接落值
            WebSheetCard.Opacity = show ? 1 : 0;
            WebSheetShift.Y = show ? 0 : 150;
            WebSheet.IsVisible = show;
            if (show) RepositionSheetHost(); else _sheetHost?.Hide();
        }
    }

    /// <summary>
    /// 起一段属性补间（对应原版的 Storyboard/DoubleAnimation）。
    /// ⚠️ Avalonia 的动画跑完会**回到基准值**，所以这里一律"基准值 = 目标值"：
    ///    调用方在 done 里把基准值写成终值（相当于原版的 FillBehavior.Stop + 先写基准值）。
    /// </summary>
    private static void RunAnim(Animatable target, AvaloniaProperty prop, double from, double to, int ms,
                                bool easeOut, Action? done)
    {
        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(ms),
            Easing = easeOut ? new CubicEaseOut() : new CubicEaseIn(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(prop, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(prop, to) } },
            }
        };

        _ = anim.RunAsync(target).ContinueWith(_ =>
        {
            try { Dispatcher.UIThread.Post(() => done?.Invoke()); } catch { }
        });
    }

    private void CloseWebSheet() => PlaySheetAnimation(show: false);

    private void WebSheetClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => CloseWebSheet();

    private void WebSheetScrim_Tapped(object? sender, TappedEventArgs e) => CloseWebSheet();

    private void WebSheetExternal_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var url = _sheetUrl;
        try { url = _sheetHost?.Core?.Source ?? url; } catch { }
        OpenExternal(url);
    }

    // ============================================================
    // 原生下载（不跳浏览器）
    // ============================================================

    /// <summary>
    /// 原生下载一个文件。
    ///
    /// ⚠️ 下载本身跑在 <see cref="DownloadManager"/> 里，**跟这个弹窗解绑**：
    ///   · 点「后台继续」把弹窗收掉，下载在后台接着跑；
    ///   · 想反悔就按「取消下载」，或者去左侧「任务进行」页取消；
    ///   · 下完/失败会弹系统通知，通知点开直接跳到「任务进行」。
    /// 弹窗还开着的时候跑完了，更省事：直接在这儿弹「下载完成」。
    /// </summary>
    public async void DownloadFile(string? url, string? suggestedName = null,
        DownloadRoute route = DownloadRoute.Setting)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var task = DownloadManager.Current.Start(url, suggestedName, suggestedName, route);
        _downloadDialogTaskId = task.Id;   // 这条由本方法负责收尾，别让系统通知重复报一次

        // 同一时刻只允许一个 ContentDialog：上一条下载的弹窗先收掉（它自己的下载转后台继续）
        if (_downloadDialog is { } previous)
        {
            try { previous.Hide(); } catch { }
            await Task.Yield();
        }

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
        var status = new TextBlock { Text = "正在连接", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock
        {
            Text = $"保存到：{DownloadService.DefaultDir}\n单击「后台继续」后，下载任务转入后台运行。",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(bar);
        panel.Children.Add(status);
        panel.Children.Add(hint);

        var dialog = new AvaloniaDialog
        {
            Title = "下载 " + task.Title,
            Content = panel,
            PrimaryButtonText = "后台继续",     // 只是把弹窗收掉，下载继续
            CloseButtonText = "取消下载",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.CloseButtonClick += (_, _) => DownloadManager.Current.Cancel(task.Id);

        void Sync()
        {
            bar.IsIndeterminate = task.BarIndeterminate;
            bar.Value = task.BarValue;
            status.Text = task.StatusText;
        }

        void OnTaskChanged(object? _, PropertyChangedEventArgs __)
        {
            Sync();
            // 跑完了（成功/失败/取消）→ 把进度框收掉，下面接着弹结果
            if (task.IsFinished)
            {
                try { dialog.Hide(); } catch { }
            }
        }

        task.PropertyChanged += OnTaskChanged;
        Sync();

        if (task.IsRunning)
        {
            _downloadDialog = dialog;
            try { await dialog.ShowAsync(this); }
            catch { }
            finally
            {
                task.PropertyChanged -= OnTaskChanged;
                if (ReferenceEquals(_downloadDialog, dialog)) _downloadDialog = null;
            }
        }
        else
        {
            // 极端情况：还没来得及弹就被下完了（只在"上一张弹窗还停着"那种空档里可能发生）
            task.PropertyChanged -= OnTaskChanged;
        }

        // 从这儿往后是**收尾**：完成 / 失败由下面自己弹，不再叠一条系统通知
        _downloadDialogTaskId = null;

        // 「后台继续」→ 任务还在跑：放手让它下，收尾交给系统通知
        if (task.IsRunning) return;
        if (task.State == DownloadState.Canceled) return;    // 自己取消的，不用报错

        if (task.State == DownloadState.Failed)
        {
            await new AvaloniaDialog
            {
                Title = "下载失败",
                Content = new TextBlock
                {
                    Text = $"{task.Title}\n\n{task.Error}\n\n可在左侧「任务进行」页单击「重试」。",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "确定",
            }.ShowAsync(this);
            return;
        }

        var done = new AvaloniaDialog
        {
            Title = "下载完成",
            Content = new TextBlock
            {
                Text = $"{task.FileName}\n{DownloadProgress.Size(task.Bytes)}\n\n{task.Path}",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "立即打开",
            SecondaryButtonText = "打开所在文件夹",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Primary,
        };

        switch (await done.ShowAsync(this))
        {
            case ContentDialogResult.Primary:
                OpenFile(task.Path);
                break;
            case ContentDialogResult.Secondary:
                RevealFile(task.Path);
                break;
        }
    }

    /// <summary>缺 WebView2 时的统一提示（官网浮层、提交软件都用它）。</summary>
    private async Task ShowRuntimeMissingDialogAsync(string? fallbackUrl = null)
    {
        var target = string.IsNullOrWhiteSpace(fallbackUrl) ? ShellConfig.SiteUrl : fallbackUrl!;

        var dialog = new AvaloniaDialog
        {
            Title = "缺少 WebView2 运行时",
            Content = new TextBlock
            {
                Text = "本页面需要系统安装 WebView2 运行时（Microsoft Edge 内核组件，免费）。\n\n" +
                       "可立即安装，也可使用浏览器打开，内容一致。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "安装 WebView2",
            SecondaryButtonText = "浏览器打开",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        switch (await dialog.ShowAsync(this))
        {
            case ContentDialogResult.Primary:
                OpenExternal(ShellConfig.WebView2DownloadUrl);
                break;
            case ContentDialogResult.Secondary:
                OpenExternal(target);
                break;
        }
    }

    /// <summary>
    /// 真的要退出应用（不是"收进托盘"）。
    ///
    /// ⚠️ public（2026-10-04）：<c>Services/Updating/UpdateFlow.ExitAppNow()</c> 走静默安装**必须**从
    ///    更新流程里调到它 —— 直接 <c>desktop.Shutdown()</c> 会被本窗的关闭逻辑当成"用户点了 ×"、
    ///    只把窗口藏起来，进程不退 → 安装程序查到 Mutex 占用 → 静默模式自动取消（这就是
    ///    「1.2 无法更新到 1.3」的根因）。
    /// </summary>
    public void ExitApp()
    {
        Core.AppLog.Info("exit", "ExitApp() 被调用");
        _exitRequested = true;
        // 让工具浮窗 / Q 群反馈窗的 Closing 放行（它们默认是"关掉 = 收起来"）。
        // ⚠️ 实测（2026-10-04）：程序化的 Window.Close() 与 desktop.Shutdown() **都不保证**触发
        //    Avalonia 的 Closing（取决于关闭路径），所以真正保证退干净的是 FinishExit() 里的显式关窗；
        //    这个标记是防御性的 —— 万一某条路径让程序化关闭也走 Closing，它保证那两个窗不会被自己的 args.Cancel 拦下。
        App.IsExiting = true;
        try { _settings.Save(); } catch { }
        try { _sheetHost?.Dispose(); } catch { }
        _sheetHost = null;
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        try { _trayTools?.Dispose(); } catch { }
        _trayTools = null;
        FinishExit();
    }

    /// <summary>
    /// 退出流程的后半段：摘钩子 → 关掉所有自家窗口 → <c>desktop.Shutdown()</c> → 1.5 秒硬退兜底。
    /// 两个入口共用：<see cref="ExitApp"/>（托盘「退出」/ 更新安装器要接管），
    /// 以及 <see cref="OnWindowClosed"/>（用户点了 × 且没开「关闭时收进托盘」）。
    /// </summary>
    private void FinishExit()
    {
        // 防重入：ExitApp 与 OnWindowClosed 两条路都可能走到这儿。
        if (_finishing) return;
        _finishing = true;

        // ① 先摘钩子 / 还原注册表（虚拟键盘）。必须在任何硬退兜底之前 —— 硬退会跳过清理，
        //    把"系统键盘不自动弹"这种脏状态留在用户机器上。
        try { Services.VirtualKeyboard.VirtualKeyboardService.Stop(); } catch { }

        // ② 显式关掉我们自己建的每一个窗口，再请应用退出。
        //    ⛔ 不能只依赖 desktop.Shutdown()，2026-10-04 实测它会漏窗口：
        //       没关掉的窗口把消息循环撑住，结果就是「主界面没了、托盘图标也没了，
        //       进程却一直挂在任务管理器里」—— 正是用户反馈的"点了退出，任务管理器里还有残留"。
        Core.AppLog.Info("exit", "开始显式关闭所有窗口");
        try { Views.ToolPaletteWindow.CloseForExit(); } catch { }
        try { Views.QqFeedbackGuideWindow.CloseForExit(); } catch { }
        try { Views.ToolSidebarWindow.CloseForExit(); } catch { }
        try { Views.MessageWindow.CloseForExit(); } catch { }   // 桌面留言悬浮窗（可拖动、置顶，同样会撑住消息循环）

        // 兜底扫一遍：其它自建窗口（贴纸 / 音量 / 截图 / 虚拟键盘…）一并收掉，
        // 谁家窗口忘了自己关，这里统一兜住。App.IsExiting 已经置位，拦关闭的窗口会放行。
        try
        {
            if (Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var w in desktop.Windows.ToList())
                {
                    try { w.Close(); } catch { }
                }
            }
        }
        catch { }

        Core.AppLog.Info("exit", "窗口已全部请求关闭");

        try
        {
            if (Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d2)
                d2.Shutdown();
            else
                Environment.Exit(0);
        }
        catch { Environment.Exit(0); }
        Core.AppLog.Info("exit", "Shutdown() 已返回（若进程仍在 = 消息循环没退）");

        // ③ 最后一道保险：desktop.Shutdown() 偶尔仍会让消息循环卡住不退。
        //    1.5 秒还没走就硬退 —— 走到这里该做的清理都已做完（设置已存、托盘已摘、键盘钩子已还原），硬退是安全的。
        new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(1500);
            Core.AppLog.Info("exit", "1.5 秒后进程仍在 -> 硬退兜底");
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "csh-exit-watchdog" }.Start();
    }

    private async Task ExecuteScriptAsync(string script)
    {
        try
        {
            if (_sheetHost is { IsReady: true } host)
                await host.ExecuteScriptAsync(script);
        }
        catch { }
    }

    // ============================================================
    // 给网页的信息
    // ============================================================

    public object BuildInfoPayload() => new
    {
        appName = ShellConfig.AppName,
        appVersion = ShellConfig.ShellVersion,
        // ⚠️ 站点外壳脚本按 "winui3" 分支（它不懂 Avalonia）；移植版刻意保留这个值，避免网页侧行为变化。
        shell = "winui3",
        shellVersion = ShellConfig.ShellVersion,
        siteVersionTarget = ShellConfig.SiteVersionTarget,
        os = OsInfo.Describe(),
        osBuild = OsInfo.Version.Build,
        isWin11 = OsInfo.IsWindows11,
        webView2 = _runtime.Version ?? string.Empty,
        theme = _settings.Current.Theme,
        actualTheme = RootGrid.ActualThemeVariant.ToString(),
        backdrop = _settings.Current.Backdrop,
        alwaysOnTop = _settings.Current.AlwaysOnTop,
        autoStart = _settings.Current.AutoStart,
        telemetryEnabled = _settings.Current.TelemetryEnabled,
        webTransparent = _settings.Current.WebTransparent,
        zoom = _settings.Current.WebZoom,
        titleBarHeight = ShellConfig.TitleBarHeight,
        themeFromWeb = _themeFromWeb,
        siteUrl = ShellConfig.SiteUrl
    };

    public object BuildStatePayload() => new
    {
        theme = _settings.Current.Theme,
        actualTheme = RootGrid.ActualThemeVariant.ToString(),
        backdrop = _settings.Current.Backdrop,
        alwaysOnTop = _settings.Current.AlwaysOnTop,
        autoStart = _settings.Current.AutoStart,
        telemetryEnabled = _settings.Current.TelemetryEnabled,
        webTransparent = _settings.Current.WebTransparent,
        zoom = _settings.Current.WebZoom,
        isMaximized = WindowState == WindowState.Maximized,
        navigated = _navigatedOk,
    };
}
