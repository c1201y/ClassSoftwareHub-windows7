using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views.Oobe;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 初次配置（OOBE，自上游 dv1.2.0-insider1.8 移植）。
///
/// <para>
/// 第三版（上游）：照 ClassIsland 的欢迎向导**重写逻辑**，不搬它的控件。窗口自己只剩两件事 ——
/// 一个装页面的宿主，和"什么时候前进/后退/收尾"的调度；每一步是一个独立页面
/// （见 Views/Oobe），步骤表、当前位置、草稿都在 <see cref="OobeSession"/> 里。
/// 页面实例被窗口缓存（原版是 Frame 的 NavigationCacheMode），往回翻时用户刚填的值还在。
/// </para>
///
/// <para>
/// 什么时候弹：只有**新装**（<c>settings.json</c> 不存在）那一次启动，以及上次配到一半退出。
/// 判定与置位都在 <see cref="App.OnFrameworkInitializationCompleted"/>，本窗只负责显示与收值 ——
/// 判定逻辑不要挪进来，那边必须在 <c>Settings.Load()</c> 之前取"文件在不在"，晚了就被 Load 里
/// 那次 Save 抹掉。
/// </para>
///
/// <para>
/// ⛔ 升级上来的老用户**永远不弹**：他们的存档里没有 <c>OobeState</c> 字段，读出来是
/// <see cref="OobeStates.Unknown"/>，走"不弹"分支。别改成"字段为假就弹"。
/// </para>
///
/// <para>
/// 收值时机：点「完成」和点窗口的 × 都要落定，按草稿写进设置。
/// 不提供「跳过」（2026-10-31 Nick 定）：初次配置是必选的，跳过会静默丢掉用户
/// 前面选好的主题等项 —— "OOBE 选了浅色、进主界面还是深色"就是这么来的。
/// </para>
///
/// <para>
/// ⚠️ 移植时序（与原版不同，必须理清）：原版收尾是「先 Close 向导 → 再亮主窗口」。
///    Avalonia 的桌面生命周期按「最后一个窗口关闭」退出，向导关掉的那一刻主窗口还没 Show，
///    整条退出流程会被当场触发。所以这里改成**先亮主窗口、再关向导** ——
///    两步在同一个调用栈里，用户看到的仍然是"向导消失、主界面出现"。
/// </para>
/// </summary>
public sealed partial class WelcomeWindow : Window
{
    private const double WidthDip = 800;
    private const double HeightDip = 600;

    private static WelcomeWindow? _instance;

    /// <summary>配置结束后要做的事（= 把主窗口亮出来）。由 <see cref="ShowForFirstRun"/> 设进来。</summary>
    private static Action? _onDone;

    /// <summary>调试预览（<c>--oobe-preview</c>）：不读写 OobeState、不应用设置。</summary>
    private static bool _preview;

    private OobeSession? _session;

    /// <summary>页面实例缓存（等价原版 Frame 的 NavigationCacheMode="Required"）。</summary>
    private readonly Dictionary<Type, Control> _pages = new();

    private Control? _currentPage;

    /// <summary>已经让第一页进场（窗口显示一次就够，Activated 会反复触发）。</summary>
    private bool _started;

    /// <summary>已经在收尾（防止「完成」里 Close 又被 Closed 处理一次）。</summary>
    private bool _finished;

    private bool _closed;

    private WelcomeWindow()
    {
        InitializeComponent();
        Configure();

        _session = OobeSession.Attach(this, OobeDraft.FromSettings());

        // 第一页在这里就建好（含开场控件），窗口显示出来时第一帧就有内容。
        // 动画不在这里播：那个等窗口真的露脸（StartOnce）。
        _session.Prepare();

        // 导航必须等窗口真的显示出来：构造期间播动画，页面会直接跳到终值，
        // 用户看到的是"一闪就到位"，等于没做。Opened / Root.Loaded / Activated 谁先到都行，用守卫去重。
        Opened += (_, _) => StartOnce();
        Root.Loaded += (_, _) => StartOnce();
        Activated += (_, _) => StartOnce();

        Closed += OnClosed;
    }

    // ── 底部导航条 ───────────────────────────────────────────────────────

    /// <summary>按当前页刷新底部按钮：第 1 步只留「开始」，其余页在左、右各一颗。</summary>
    internal void UpdateNav()
    {
        var index = _session?.Index ?? 0;

        BackNavButton.IsVisible = index > 0;

        // 完成页自己管轮播：到最后一屏主按钮就变成「完成」
        NextNavButton.Content = _currentPage is OobeFinishPage finish
            ? finish.PrimaryText
            : index == 0 ? "开始" : "下一步";

        // 页面可以拦住下一步（协议未勾选）；其余页一律放行
        NextNavButton.IsEnabled = _currentPage is not IOobeStepGuard guard || guard.CanAdvance;

        // 页面是常驻缓存的，进/出这一屏用 IsVisible 切换（Avalonia 不会因此触发 Loaded/Unloaded），
        // 所以「当前页」这件事得由窗口自己告诉页面 —— 见 OobeSession 的 Started 之外不需要额外通知。
    }

    private void NavBack_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentPage is OobeFinishPage finish) finish.StepBack();
        else _session?.Back();
    }

    private void NavNext_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentPage is IOobeStepGuard guard && !guard.CanAdvance) return;
        if (_currentPage is OobeFinishPage finish) finish.StepForward();
        else _session?.Next();
    }

    // ── 预览（只改本窗显示，收尾时才写进设置） ──────────────────────────

    /// <summary>
    /// 外观预览：原版改这一层的 <c>RequestedTheme</c>，整棵子树跟着重取资源。
    /// Avalonia 里 <c>RequestedThemeVariant</c> 只在 TopLevel 上，所以直接设在窗口上。
    /// </summary>
    internal void PreviewTheme(string theme)
    {
        try { RequestedThemeVariant = ThemeCompat.Map(theme); }
        catch (Exception ex) { AppLog.Warn("oobe", "主题预览失败：" + ex.Message); }
    }

    /// <summary>背景材质预览：只作用于本窗。</summary>
    internal void PreviewBackdrop(string kind)
    {
        try { BackdropTheme.Apply(this, Root, kind); }
        catch (Exception ex) { AppLog.Warn("oobe", "背景预览失败：" + ex.Message); }
    }

    /// <summary>本窗句柄（目录选择器之类的 owner）。</summary>
    internal IntPtr WindowHandle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 该弹就弹；<paramref name="onDone"/> 在收尾（完成 / 关窗）后调用一次 ——
    /// 新装首启时主窗口就是靠它才露面的。
    /// </summary>
    public static void ShowForFirstRun(Action onDone)
    {
        _preview = false;
        if (App.Settings.Current.OobeState == OobeStates.Done || _instance is not null)
        {
            onDone();
            return;
        }

        ShowCore(onDone);
    }

    /// <summary>
    /// 强制预览一次向导，**不读写 OobeState、不应用设置**。
    /// 用法：<c>ClassSoftwareHub.exe --oobe-preview</c>（见 App）。
    /// ⚠️ 原版只在 DEBUG 构建里有；移植版 Release 也留着 —— 教室机现场核对向导外观时，
    ///    总不能再装一个调试构建（该开关不写任何设置，跑完即退，无副作用）。
    /// </summary>
    public static void ShowPreview(Action onDone)
    {
        _preview = true;
        ShowCore(onDone);
    }

    /// <summary>用户又点了一次桌面图标：向导还开着就把它叫到前面，别让主窗口从底下冒出来。</summary>
    public static void BringToFront()
    {
        try
        {
            if (_instance is not { } win) return;
            if (!win.IsVisible) win.Show();
            win.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "唤醒向导窗口失败：" + ex.Message);
        }
    }

    private static void ShowCore(Action onDone)
    {
        _onDone = onDone;

        try
        {
            _instance = new WelcomeWindow();
            _instance.Show();
            _instance.Activate();
            AppLog.Info("oobe", _preview ? "初次配置窗口已弹出（预览）" : "初次配置窗口已弹出");
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "初次配置窗口创建失败");

            // ⛔ 建不出来不能就这么挂着：不改状态的话，之后**每次**启动都会再试一遍、
            //    每次都失败、每次都在日志里留一坨异常。直接按"已处理"记账，让用户进主界面。
            if (!_preview)
            {
                App.Settings.Current.OobeState = OobeStates.Done;
                App.Settings.Save();
            }

            _instance = null;
            onDone();
        }
    }

    // ── 初始化 ───────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            // 原版：框架留着（拖动/阴影/圆角靠系统），标题栏内容自己画，不可拉伸、不可最小化/最大化。
            // Avalonia 版保留系统标题栏，只继承「不可拉伸」这一条；标题就是原版标题栏上那行字。
            CanResize = false;

            BackdropTheme.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeCompat.Apply(Root);

            ApplySize();
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "初次配置窗口初始化");
        }
    }

    /// <summary>
    /// 按逻辑像素（dip）定客户区大小，并夹进当前屏幕。
    /// 教室机器常见 1366×768 / 125% 缩放：600dip 会接近工作区上限，这里按实际工作区收。
    /// </summary>
    private void ApplySize()
    {
        try
        {
            var work = WorkArea();
            if (work.Width <= 0 || work.Height <= 0) return;

            var scale = RenderScaling <= 0 ? 1 : RenderScaling;
            var maxW = Math.Max(700, work.Width / scale - 40);
            var maxH = Math.Max(480, work.Height / scale - 40);

            Width = Math.Clamp(WidthDip, 700, maxW);
            Height = Math.Clamp(HeightDip, 480, maxH);
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "初次配置窗口尺寸设置失败：" + ex.Message);
        }
    }

    /// <summary>主屏工作区（物理像素）。</summary>
    private PixelRect WorkArea()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this)
                         ?? Screens.All.FirstOrDefault(s => s.IsPrimary)
                         ?? Screens.All.FirstOrDefault();
            if (screen is not null) return screen.WorkingArea;
        }
        catch { }
        return default;
    }

    // ── 换页（由 OobeSession 调） ─────────────────────────────────────────

    private void StartOnce()
    {
        if (_started || _closed) return;
        _started = true;

        // ⚠️ 放到 Loaded 之后一拍再播：页面是在窗口显示后才真正挂上视觉树，
        //    欢迎页要在这时才能订阅到 session.Started。早一拍发通知，动画就整个错过。
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            _session?.Play();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 前进/后退都走这里：<paramref name="forward"/> 决定页面从右边还是左边滑进来。
    /// 首屏（<paramref name="animate"/> = false）不播转场 —— 开场动画自己会把画面立起来。
    /// </summary>
    internal void NavigateToPage(Type page, bool forward, bool animate)
    {
        if (_closed) return;

        try
        {
            if (!_pages.TryGetValue(page, out var target))
            {
                target = (Control)Activator.CreateInstance(page)!;
                _pages[page] = target;
            }

            if (ReferenceEquals(target, _currentPage))
            {
                UpdateNav();
                return;
            }

            var from = _currentPage;

            if (!StepHost.Children.Contains(target))
                StepHost.Children.Add(target);

            // 缓存里没被用到的页面一律收起（切回来时再亮）
            foreach (var child in StepHost.Children)
            {
                if (child is Control c && !ReferenceEquals(c, target) && !ReferenceEquals(c, from))
                    c.IsVisible = false;
            }

            if (from is null || !animate)
            {
                foreach (var child in StepHost.Children)
                {
                    if (child is Control c) c.IsVisible = ReferenceEquals(c, target);
                }

                target.Opacity = 1;
                target.RenderTransform = null;
                target.IsHitTestVisible = true;
            }
            else
            {
                OobeMotion.Swap(from, target, forward, distance: 60, durationMs: 300);
            }

            _currentPage = target;
            UpdateNav();
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "向导换页失败：" + page.Name);
        }
    }

    // ── 收尾 ─────────────────────────────────────────────────────────────

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _instance = null;
        FinishOobe();
    }

    /// <summary>
    /// 收尾：把草稿写进设置、标记初次配置走完。
    /// 幂等 —— Closed 与「完成」都会走到这里，第二次直接返回。
    /// </summary>
    internal void FinishOobe()
    {
        if (_finished) return;
        _finished = true;

        if (_preview)
        {
            AppLog.Info("oobe", "预览模式：不写入任何设置");
        }
        else
        {
            if (_session is not null) ApplySettings(_session.Draft);

            App.Settings.Current.OobeState = OobeStates.Done;
            App.Settings.Save();

            AppLog.Info("oobe", "初次配置完成，已按选择写入设置");
        }

        OobeSession.Detach();

        // 主窗口就是在这里才出现的（新装首启时它一直没显示过）。
        // ⚠️ 顺序与上游相反：先亮主界面、再收向导 —— Avalonia 按「最后一个窗口关闭」退出，
        //    先 Close 会把整条退出流程触发掉（见类注释）。同一个调用栈内完成，观感一致。
        try { _onDone?.Invoke(); }
        catch (Exception ex) { AppLog.Warn("oobe", "收尾回调失败：" + ex.Message); }

        try { Close(); } catch { /* 已经从 × 进来时窗口已关 */ }
    }

    /// <summary>
    /// 完成页的入口：先收尾（写设置 + 亮主窗口），再跳页。
    /// ⚠️ 顺序不能反 —— 主窗口还没显示时 Shell 的导航还没准备好，先跳会被吞掉。
    /// </summary>
    internal void FinishAndNavigate(string tag)
    {
        FinishOobe();

        try { App.MainWindow?.Shell.NavigateTo(tag); }
        catch (Exception ex) { AppLog.Warn("oobe", "跳转失败：" + tag + "：" + ex.Message); }
    }

    private static void ApplySettings(OobeDraft draft)
    {
        var s = App.Settings.Current;
        var shell = App.MainWindow;

        // 分体外观与背景材质必须**先写**：SetTheme 内部会按 Backdrop 重算材质，
        // 并通过 ThemeCompat.Notify 让侧边栏 / 浮窗按 SplitTheme + ExternalTheme 取色。
        s.Backdrop = NormalizeChoice(draft.Backdrop, "mica", "mica", "acrylic", "solid");
        s.SplitTheme = draft.SplitTheme;
        s.ExternalTheme = NormalizeChoice(draft.ExternalTheme, "system", "system", "light", "dark");

        // 外观走主窗口的 SetTheme：它内部会 Save、重算背景材质、通知侧边栏与浮窗跟着换。
        // ⛔ 别在这儿直接写 s.Theme —— 少那两步，用户会看到"主窗口换了、侧边栏还是旧的"。
        // ⚠️ 主窗口这时已经建好（只是还没显示）。万一它没建出来，也要把存档写对：
        //    否则用户选浅色、进主界面还是深色，下次启动也纠正不回来（2026-10-31 反馈）。
        var theme = string.IsNullOrWhiteSpace(draft.Theme) ? "system" : draft.Theme.Trim().ToLowerInvariant();
        if (shell is not null)
        {
            shell.SetTheme(theme);
        }
        else
        {
            s.Theme = theme;
            ThemeCompat.Notify();
        }
        AppLog.Info("oobe", "外观写入：" + theme);

        // 常用工具浮窗置顶有独立入口：它要同步通知已经开着的浮窗。
        if (shell is not null) shell.SetPaletteOnTop(draft.PaletteOnTop);
        else s.PaletteOnTop = draft.PaletteOnTop;

        s.SidebarEnabled = draft.SidebarEnabled;
        // 贴边与常驻必须在 ApplySetting() 之前写进设置 —— 它按设置重建侧边栏实例。
        s.SidebarPinned = draft.SidebarPinned;
        s.SidebarLongLabels = draft.SidebarLongLabels;
        s.SidebarMode = draft.SidebarMode == "free" ? "free" : "dock";
        s.SidebarEdge = NormalizeChoice(draft.SidebarEdge, "right", "left", "right", "both");
        s.SidebarFreeEdge = NormalizeChoice(draft.SidebarFreeEdge, "right", "left", "right", "top", "bottom");
        s.SidebarDensity = draft.SidebarDensity == "compact" ? "compact" : "standard";
        s.SidebarExpandedDensity = NormalizeChoice(draft.SidebarExpandedDensity, "same", "same", "standard", "compact");
        s.SidebarColumns = draft.SidebarColumns == 3 ? 3 : 1;
        s.SidebarHeightRatio = Math.Clamp(draft.SidebarHeightRatio, 0.45, 0.95);
        s.SidebarHeightAuto = draft.SidebarHeightAuto;
        s.SidebarCollapsedGap = Math.Clamp(draft.SidebarCollapsedGap, 0, 24);

        s.ShotAutoSave = draft.ShotAutoSave;
        s.ShotSaveDir = (draft.ShotSaveDir ?? "").Trim();

        // 启动三件套。SetAutoStart 会写 HKCU 的 Run 项，SetMinimizeOnStart 会重算自启命令行；
        // 没开自启时"最小化启动"没有意义，一并清掉，免得存档里留一个永不到达的 true。
        var autoStart = draft.AutoStart;
        shell?.SetAutoStart(autoStart);
        shell?.SetMinimizeOnStart(autoStart && draft.MinimizeOnStart);
        shell?.SetCloseToTray(draft.CloseToTray);

        s.GithubDownloadRoute = GithubRoutes.Normalize(draft.GithubRoute);
        GithubRoute.InvalidateCache();          // 刚换的线路立刻生效，不用等探测缓存过期

        s.AutoCheckUpdate = draft.AutoCheckUpdate;
        s.HubAiEnabled = draft.HubAiEnabled;
        s.UserDisplayName = (draft.UserDisplayName ?? "").Trim();
        s.CacheKeepCount = Math.Clamp(draft.CacheKeepCount, 1, 10);

        App.Settings.Save();

        // 侧边栏开关切换后要立刻生效（平时只有侧边栏布局页做这一步）。
        // ⚠️ 主窗口这时还没显示，ApplySetting 会照常把侧边栏摆出来 —— 所以放在 Save 之后、
        //    回调（Activate 主窗口）之前，用户第一次看见桌面时它已经在正确的位置上。
        try { ToolSidebarWindow.ApplySetting(); }
        catch (Exception ex) { AppLog.Warn("oobe", "侧边栏开关应用失败：" + ex.Message); }
    }

    /// <summary>下拉值归一：认不出的值落到默认档，别把草稿里的脏值写进存档。</summary>
    private static string NormalizeChoice(string? value, string fallback, params string[] allowed)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(allowed, v) >= 0 ? v : fallback;
    }
}
