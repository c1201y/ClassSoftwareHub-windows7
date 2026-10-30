using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 原生外壳：顶部标题栏 + 左侧导航 + 页面容器。
/// 导航：上半区「首页 / 软件下载 / 内置工具 / 侧边布局 / 实验性功能」，
/// 下半区「任务进行 / 提交软件 / 反馈中心 / 更新日志 / 设置」。
/// ⚠️「实验性功能」是分组父项，点它=进总览页，子项「本机核实」可直达（见 ShellPage.axaml 的注释）。
/// ⚠️「反馈中心」是**两级**：选类型页（FeedbackPage）→ 表单页（FeedbackFormPage），
///    卡片单击走 NavigateToFeedbackForm，返回走导航栏那一个返回按钮。
/// ⚠️ 所有页面（含「提交软件」）都是自绘；只有详情页那种「在应用内看一眼网页」的浮层（MainWindow.WebSheet）会用到 WebView2。
///
/// ⚠️ 移植说明（WinUI → Avalonia/FluentAvalonia）：
///   · 基类 UserControl → Platform.PageBase（页面生命周期 OnNavigatedTo/OnNavigatedFrom 由它提供）。
///   · WinUI 的 Frame 自带 Page 生命周期；Avalonia 没有这套，必须用 Navigation.Attach(ContentFrame)
///     把 FA Frame 的 Navigated 事件桥接到 PageBase 上（**必须在首次 Navigate 之前**）。
///   · ActualTheme（ElementTheme）→ ActualThemeVariant（ThemeVariant）。
///   · ToolTipService.SetToolTip → ToolTip.SetTip。
/// </summary>
public sealed partial class ShellPage : PageBase
{
    /// <summary>给 MainWindow 用：SetTitleBar 需要这个元素（原生页面的拖动区）。</summary>
    public Grid TitleBarElement => TitleBarArea;

    public ShellPage()
    {
        InitializeComponent();

        // 全局搜索（2026-10-18 自上游 1.2.0 移植）：结果弹层挂到搜索框下面。
        SearchPopup.PlacementTarget = SearchBox;
        SearchPopup.Placement = Avalonia.Controls.PlacementMode.Bottom;

        // ⚠️ 移植新增：WinUI 的 Frame 自动接 Page 生命周期，这里必须显式挂桥（幂等，可重复调用）
        Navigation.Attach(ContentFrame);

        // ⚠️ 对应原版 ActualThemeChanged：Avalonia 里是 StyledElement.ActualThemeVariantChanged
        ActualThemeVariantChanged += (_, _) => UpdateThemeButton();

        // 「内置工具」的导航子项在 ShellPage.axaml 里声明（FA 的选中模型只认 XAML 子项，原因见那里的注释）。
        // 这里做一次一致性自检：XAML 子项的 Tag 必须与 ToolCatalog.All 的 Id 一一对应。
        VerifyToolNavItems();

        // 下载任务数变了 → 同步「任务进行」上的徽标（在下载就不占着界面，但得让人随时看见有几个在下）
        Services.DownloadManager.Current.Changed += UpdateDownloadBadge;
        UpdateDownloadBadge();

        // 导航栏一律不弹 ToolTip（浮层会吃掉内容区控件的首击，细节见方法注释）。
        // 关的是「服务」而非常量属性，与项容器何时 realize 无关，所以构造期设一次即可。
        DisableNavToolTips();

        // 面板显示模式变了（宽 ↔ 窄）→ 重新算内容区要不要给导航按钮让位
        Nav.DisplayModeChanged += (_, _) => ApplyPaneInset();
        ApplyPaneInset();

        // 切页计时（Debug / CSH_PERF=1 才有输出，见 Core/PerfLog.cs）。
        ContentFrame.Navigating += (_, e) => Core.PerfLog.NavBegin(CurrentTag);

        // 每次切页停稳之后收一次内存（2026-10-04：回到"跳一页收一次"的激进思路）。
        // ⚠️ 收本体在**后台线程**、且错开 0.6 秒（等动画与首帧过去）—— 跟 2026-10-01 被否掉的
        //    "UI 线程上延时 3 秒同步 GC"不是一回事，详见 Services/MemoryTrimmer.cs 的类注释。
        ContentFrame.Navigated += (_, _) =>
        {
            Core.PerfLog.NavEnd(ContentFrame.Content);
            Services.MemoryTrimmer.ScheduleAfterNavigate();
            UpdateBackButton();
            SyncNavSelectionToFrame();          // ⚠️ 2026-10-03：返回（含 GoBack）后导航高亮跟着退
            FadeInContent();                    // ⚠️ 2026-10-04：切页淡入（原先切页是"啪"一下换掉）
            RefreshHomeUpdateBanner();           // 回到首页时复核待装更新横幅（首页实例被 Frame 缓存）
        };
    }

    /// <summary>
    /// 复核首页那条「新版本已下载就绪」横幅。
    ///
    /// ⚠️ 为什么不在 WelcomePage 的 <c>Loaded</c> 里一把梭：<c>ContentFrame.CacheSize=2</c> 会缓存页面实例，
    ///    而"用户本来就停在首页"这种情况下**根本没有任何导航发生** —— 后台把更新下完时横幅不会自己冒出来。
    ///    所以这里做成公开入口，由 <c>MainWindow.NotifyUpdateReady</c> 在下载完成时直接叫一次。
    /// </summary>
    public void RefreshHomeUpdateBanner()
    {
        if (ContentFrame.Content is WelcomePage home) home.RefreshUpdateReadyBar();
    }

    /// <summary>
    /// 自检：导航里「内置工具」的子项（在 <c>ShellPage.axaml</c> 里声明）必须与
    /// <see cref="Data.ToolCatalog.All"/> 一一对应（Tag ↔ Id，顺序一致）。
    ///
    /// <para>⚠️ 为什么子项写在 XAML 而不是代码里：FluentAvalonia 2.4.1 的导航选中模型**只认 XAML
    /// 一次声明的子项** —— 代码往 <c>MenuItems</c> 里后加的子项能渲染、但选中时认不到，
    /// 选中指示条（导航栏左边那根竖线）会退回画在父项上。详见 ShellPage.axaml 里的长注释。</para>
    ///
    /// <para>所以工具清单变成两处维护，这个方法就是防"改一处漏一处"的哨兵：对不上记一条日志。</para>
    /// </summary>
    private void VerifyToolNavItems()
    {
        try
        {
            // 2026-10-22：内置工具拆成「课堂工具 / 系统工具」两个父项，子项按 ToolDef.Group 分派，
            // 校验时把两组合起来比（集合相等即可，父项顺序不影响）。
            var nav = ClassroomToolsNav.MenuItems.OfType<NavigationViewItem>()
                .Concat(SystemToolsNav.MenuItems.OfType<NavigationViewItem>())
                .Select(i => i.Tag as string ?? "").ToList();
            var cat = Data.ToolCatalog.All.Select(t => t.Id).ToList();
            if (!nav.SequenceEqual(cat))
            {
                Platform.PortLog.Step("⚠️ 导航「内置工具」子项与 ToolCatalog 不一致：nav=[" + string.Join(",", nav)
                                      + "] catalog=[" + string.Join(",", cat) + "]（两处要一起改）");
            }
        }
        catch
        {
            // 自检失败不该影响启动
        }
    }

    /// <summary>符号字体（与 ShellPage.axaml 里 <c>{DynamicResource SymbolThemeFontFamily}</c> 同一个）。</summary>
    private static Avalonia.Media.FontFamily SymbolFontFamily()
    {
        try
        {
            if (Avalonia.Application.Current?.TryFindResource("SymbolThemeFontFamily", out var v) == true
                && v is Avalonia.Media.FontFamily f)
                return f;
        }
        catch { }
        return new Avalonia.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
    }

    /// <summary>切页淡入用的按帧计时器（重入时作废上一次）。</summary>
    private DispatcherTimer? _contentFade;

    /// <summary>正在淡入的那一页（用于识别"同一次导航被重复触发"，见 <see cref="FadeInContent"/>）。</summary>
    private Type? _fadePage;

    /// <summary>
    /// 页面切换的淡入 —— 2026-10-04（用户第 17 轮「有些地方没有动画，适当加一点」）。
    ///
    /// <para>
    /// <c>fa:Frame</c> 切页是**瞬间换掉**的，没有任何过渡：从列表点进详情、或点左侧导航换页，
    /// 观感很硬。这里给内容区做一次 170ms 淡入。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 用按帧计时器而不是 Avalonia 的 Transitions：跟本仓库其它窗口动画
    ///    （<c>FlyoutFade</c> / 侧边栏抓手淡入 / 滑动）保持同一套写法，节奏好统一调。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **只动 Opacity，不做位移**：教室里那批老机器（无 DWM 合成）上逐帧位移会明显掉帧，
    ///    而透明度变化是最廉价的一种，60fps 也扛得住。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 2026-10-06 用户反馈「打开任意页面，加载动画会显示两遍」。实测（Release 版逐帧打点）：
    ///    ① FluentAvalonia 2.4.1 的 NavigationView 对**展开分组里的子项**（如「内置工具 → 图片取色」）
    ///       会把 <c>ItemInvoked</c> 派发**两次**（间隔 6~60ms、tag 完全相同），于是切页执行两遍，
    ///       这里被调两次 —— 老代码每次都把 <c>Opacity</c> 拉回 0 重头开始，用户就看到淡入播两遍。
    ///       判据：同一页 + 上一次淡入还在跑 → 认定为同一次导航的重复触发，保持正在播放的那一次。
    ///    ② <c>Navigated</c> 是在 <c>Navigate()</c> 里同步触发的，此刻 UI 线程仍在构建新页控件树
    ///       （重页 0.8~1.6 秒），16ms 的 <see cref="DispatcherTimer"/> 排不上队；若此刻就把秒表跑起来，
    ///       等计时器终于 tick 时早已超过 170ms，动画会「一步跳到 1」（既不淡入、又先白屏一大截）。
    ///       改成**第一帧才开始计时**，动画才真的播得完。
    /// </para>
    /// </summary>
    private void FadeInContent()
    {
        try
        {
            var page = ContentFrame.CurrentSourcePageType;
            if (_contentFade is not null && page is not null && page == _fadePage)
                return;                         // 同一次导航的重复触发 → 不重启动画

            _contentFade?.Stop();
            _contentFade = null;
            _fadePage = page;

            ContentFrame.Opacity = 0;

            var sw = new System.Diagnostics.Stopwatch();      // ⚠️ 故意不在这里 Start —— 见方法注释 ②
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                if (!sw.IsRunning) sw.Start();                // 第一帧才起表，动画才播得满 170ms
                var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / 170.0, 0, 1);
                ContentFrame.Opacity = 1 - Math.Pow(1 - t, 3);      // ease-out cubic
                if (t < 1) return;
                ContentFrame.Opacity = 1;
                timer.Stop();
                _contentFade = null;
            };
            _contentFade = timer;
            timer.Start();
        }
        catch
        {
            // 动画出岔子不值得影响导航：直接显示出来
            try { ContentFrame.Opacity = 1; } catch { }
        }
    }

    /// <summary>「任务进行」上的 InfoBadge = 正在下载的任务数；一个都没有就把徽标整个摘掉。</summary>
    private void UpdateDownloadBadge()
    {
        try
        {
            var active = Services.DownloadManager.Current.ActiveCount;
            DownloadsNav.InfoBadge = active > 0 ? new InfoBadge { Value = active } : null;
        }
        catch
        {
            // 徽标画不出来不值得影响导航
        }
    }

    /// <summary>
    /// 标题栏的返回按钮：能退就亮、退到头就藏。
    /// ⚠️ 2026-10-02 起 FA 导航面板自带的返回按钮已关掉（图标字体缺 E72B、位置也不对），
    ///    返回入口只有标题栏这一个，可见性必须在这里同步。
    /// </summary>
    private void UpdateBackButton()
    {
        var canGoBack = ContentFrame.CanGoBack;
        Nav.IsBackEnabled = canGoBack;          // 保险起见照旧（面板按钮已隐藏，不影响）
        BackButton.IsVisible = canGoBack;

        // ⚠️ 2026-10-03（用户第 14 轮：「没有返回按钮时标题太靠左了」）：
        //    有返回键时图标跟在键后面（8 + 48 + 10）；没有时再贴着 8 就太靠边了，
        //    放宽到 14 —— 和 Win11 设置那种"标题自己站好位置"的观感一致。
        TitleLeading.Margin = canGoBack ? new Thickness(8, 0, 0, 0) : new Thickness(14, 0, 0, 0);
    }

    private void BackButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        UpdateBackButton();
        SyncNavSelectionToFrame();              // ⚠️ 2026-10-03：返回后导航高亮要跟着退
    }

    /// <summary>
    /// 窄窗口（面板显示模式 Minimal，窗口宽 &lt; 820 DIP）下，NavigationView 会把返回按钮和汉堡按钮
    /// **浮在内容区左上角**，不给内容留位置 —— 而每个页面的标题正好也在左上角，于是两样东西叠在一起
    /// （Nick 2026-09-26 反馈「标题和返回、汉堡重叠」）。
    /// 这里按显示模式给内容整体让出头顶那一条：Minimal 让 48（＝按钮那一行的高度）；其余模式不用让
    /// （Compact 时按钮在左侧 48 宽的面板栏里、Expanded 时在展开的面板里，都压不到内容）。
    /// </summary>
    private void ApplyPaneInset()
    {
        var inset = Nav.DisplayMode == NavigationViewDisplayMode.Minimal ? 48d : 0d;
        if (Math.Abs(ContentFrame.Margin.Top - inset) > 0.5)
            ContentFrame.Margin = new Thickness(0, inset, 0, 0);
    }

    private void Nav_BackRequested(object? sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        UpdateBackButton();
        SyncNavSelectionToFrame();
    }

    /// <summary>
    /// ⚠️ 2026-10-03（用户第 14 轮：「打开几个页面按返回按钮，导航栏那里不跟着变」）：
    /// 返回只动 Frame，NavigationView 的选中还停在上一次 <c>SelectTag</c> 的那一项 ——
    /// 比如首页 → 内置工具 → 秒表，按返回回到工具列表，左边高亮还停在原来的地方。
    /// 这里按 Frame **当前页类型**反查 tag，把高亮同步回去。
    ///
    /// <para>方向相反的两张表：<see cref="NavigateTagCore"/> 是 tag → 页型；这里是 页型 → tag。</para>
    /// ⚠️ 幂等：正常点导航项时 SelectTag 已经设对，这里算出同一个 tag 不产生任何变化；
    ///     NavigationView 的程序化 SelectedItem 赋值只改高亮、不触发 ItemInvoked，不会绕回导航成环。
    /// </summary>
    private void SyncNavSelectionToFrame()
    {
        try
        {
            var tag = TagForPage(ContentFrame.CurrentSourcePageType);
            if (tag is null) return;

            // ⚠️ 2026-10-07：这里原来是 `if (CurrentTag == tag) return;`（"高亮本来就对，不动作"）——
            //    结果把**起点那一次 SelectTag 没生效**（Init 阶段项容器还没 realize）永久固化：
            //    竖线一丢就再也回不来，只能切走再切回。改成每次都幂等地重钉一遍
            //    —— 重设同一个 SelectedItem 不产生任何变化，但能把坏状态修回来。
            CurrentTag = tag;
            SelectTag(tag);
        }
        catch
        {
            // 反查失败就维持原高亮，不值得为它抛异常
        }
    }

    /// <summary>
    /// 把"当前页"的导航高亮 + 分组展开**重新钉一遍**（幂等）。
    ///
    /// <para>⚠️ 2026-10-07 新增。根因：<see cref="SelectTag"/> 在 ShellPage 构造 / <c>Init()</c>
    /// 阶段就会跑，而此刻 NavigationView（FluentAvalonia 2.4.1）的项容器尚未 realize：
    /// 往一个**折叠的分组**里设 <c>SelectedItem</c>，FA 的选中指示条（导航栏左边那根带颜色的竖线）
    /// 找不到可视落点就整条不画；<c>IsExpanded=true</c> 也可能被紧随其后的首次布局冲掉。
    /// 现象就是 Nick 说的"在二级菜单里那根竖线不见了"。</para>
    ///
    /// <para>对策：布局停稳（Loaded 优先级）后再调一次 <see cref="SelectTag"/>，
    /// 顺带把父项展开；此时项容器已存在，FA 能把竖线正确定位到**子项**上。</para>
    /// </summary>
    private void ReassertNavSelection()
    {
        try
        {
            // ⚠️ 先置 null 再重设：如果 SelectedItem 已经等于目标值，直接再赋一次是 no-op，
            //    FluentAvalonia 不会重新定位那根选中竖线；先清空才能逼它重算（也就逼出子项的指示条）。
            Nav.SelectedItem = null;
            SelectTag(CurrentTag);
        }
        catch { }
    }

    /// <summary>页型 → 导航 tag（返回键同步高亮用）。返回 null = 这页不在导航体系里（不动作）。</summary>
    private static string? TagForPage(Type? pageType)
    {
        if (pageType is null) return null;
        var byName = pageType.Name switch
        {
            "WelcomePage" => "home",
            "SoftwarePage" => "apps",         // 分类页（cat:*）也高亮「软件下载」——人确实在这一区
            "ToolsPage" => "tools-classroom",  // 工具**索引页**高亮父项；具体工具页见下面的目录反查
            "SubmitPage" => "submit",
            "FeedbackPage" => "feedback",
            "FeedbackFormPage" => "feedback",
            "DownloadsPage" => "downloads",
            "MachineCheckPage" => "machinecheck",
            "EasiNoteGuardPage" => "easiguard",
            "ProcessGuardPage" => "procguard",
            "VirtualKeyboardPage" => "virtualkeyboard",
            "ExperimentalPage" => "experimental",
            "ChangelogPage" => "changelog",
            "SettingsPage" => "settings",
            "SidebarLayoutPage" => "sidebar",
            "AnnouncePage" => "announce",      // 教室喊话已提为独立功能项（不在 ToolCatalog，必须显式映射）
            _ => null,
        };
        if (byName is not null) return byName;

        // ⚠️ 2026-10-07：工具子页必须反查回**自己的子项 tag**，不能笼统高亮父项「内置工具」。
        //    根因：Frame 每次导航完成都会走 SyncNavSelectionToFrame → 这里算 tag → SelectTag 重钉高亮。
        //    以前 Pages.Tools.* 全部落到这里返回 "tools"（父项），把启动时 SelectTag 刚设好的
        //    子项选中**覆盖回父项** —— 竖线就压在「内置工具」上不跟页面走了（实测 --page=pick-number）。
        //    「实验性功能」子项没这问题，正是因为它们的页型在上面表里一一映射回子 tag。
        //    按 Page 反查 ToolCatalog（工具清单只有一份，新工具加了这里自动跟上）。
        var tool = Data.ToolCatalog.All.FirstOrDefault(t => t.Page == pageType);
        if (tool is not null) return tool.Id;

        // 兜底：不在目录里的 Pages.Tools.* 页型（理论没有）高亮父项总比丢高亮好
        return pageType.Namespace?.EndsWith(".Pages.Tools", StringComparison.Ordinal) == true
            ? "tools"
            : null;
    }

    /// <summary>标题栏那个太阳/月亮按钮：图标显示"点了会切到的那一边"。</summary>
    public void UpdateThemeButton()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        ThemeIcon.Glyph = dark ? "\uE706" : "\uE708";   // 深色时显示太阳（点了变浅色），反之显示月亮
        ToolTip.SetTip(ThemeButton, dark ? "切换到浅色模式" : "切换到深色模式");
    }

    private void ThemeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var next = ActualThemeVariant == ThemeVariant.Dark ? "light" : "dark";
        App.Settings.Current.Theme = next;
        App.MainWindow?.SetTheme(next);
        UpdateThemeButton();
    }

    // ============================================================
    // 自绘标题栏的窗口按钮（只有挂上 SelfDrawnFrame 时才显示）
    // ============================================================

    private Control[]? _captionInteractive;

    /// <summary>
    /// 标题栏上的交互控件清单 —— 喂给 <see cref="Core.SelfDrawnFrame.InteractiveRegions"/>。
    /// 自绘框架要把这几个矩形从「拖动区」里挖掉，否则点它们会被系统当成拖窗口，点击直接丢失。
    /// </summary>
    public IReadOnlyList<Control> CaptionInteractiveControls
        => _captionInteractive ??= new Control[]
        {
            BackButton, ThemeButton, CaptionMinButton, CaptionMaxButton, CaptionCloseButton,
            // ⛔⛔ 2026-10-19：搜索框也必须列进来 —— 它整块坐在标题栏里，
            //    不挖出来的话点它就是"系统拖动窗口"，输入框永远拿不到焦点（用户报「搜索栏点不了没有用」）。
            SearchBox
        };

    /// <summary>
    /// 切到「自绘窗口」模式：右侧不再为系统按钮留 170px，三大金刚改由我们自己画。
    ///
    /// ⚠️ 只允许在 MainWindow **成功挂上** <see cref="Core.SelfDrawnFrame"/> 之后调用 ——
    ///    挂失败会退回原生标题栏，那时系统那三个按钮还在，这里再画三个就重了。
    /// </summary>
    public void UseSelfDrawnCaption()
    {
        // ⚠️ 2026-10-03（用户第 16 轮「关闭键有时候点了没反应」）：右边距 6 → **0**。
        //    真实 Windows 的标题栏按钮是**怼着窗口右缘**的（用户也习惯点最右上角那个角）。
        //    之前留 6dip：最右边那一条既不是按钮（命中测试归标题栏 → 拖动），
        //    又是缩放带的地盘 —— 点在最角落上等于点了"空白处"。
        //    现在按钮自己贴边，配合 SelfDrawnFrame 里「交互控件优先于缩放带」的判定，
        //    右上角就是关闭键本键。
        TitleBarActions.Margin = new Thickness(0, 0, 0, 0);
        TitleBarActions.Spacing = 4;

        CaptionMinButton.IsVisible = true;
        CaptionMaxButton.IsVisible = true;
        CaptionCloseButton.IsVisible = true;

        UpdateCaptionGlyphs();
    }

    /// <summary>最大化 / 还原：按钮图标跟着窗口状态走（还原态是「两个叠起来的方框」）。</summary>
    public void UpdateCaptionGlyphs()
    {
        try
        {
            var maximized = App.MainWindow?.WindowState == WindowState.Maximized;

            CaptionMaxGlyph.Data = Geometry.Parse(maximized
                ? "M3.5,0.5 H9.5 V6.5 H3.5 Z M0.5,3.5 H6.5 V9.5 H0.5 Z"    // 向下还原
                : "M0.5,0.5 H9.5 V9.5 H0.5 Z");                            // 最大化

            // ⚠️ 2026-10-03（用户第 14 轮「金刚有时候点了没反应」）：**故意不挂 ToolTip**。
            //    Avalonia 的 Tooltip 是独立 Popup 窗口，悬停一秒就弹出来，这时按下去那一下
            //    会被 Popup 的 dismiss 吃掉（真机「点了没反应」的头号嫌疑）；
            //    GripRow 上同一坨问题当年就实测过（Tooltip 一弹，系统模态拖动循环直接被搅掉）。
            //    三大金刚是高频点击件，宁可没有提示也不能丢点击。
        }
        catch
        {
            // 图标切不过来不值得影响窗口操作
        }
    }

    private void CaptionMinimize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => App.MainWindow?.HandleWindowCommand("minimize");

    private void CaptionMaximize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => App.MainWindow?.HandleWindowCommand("toggleMaximize");

    private void CaptionClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => App.MainWindow?.HandleWindowCommand("close");

    /// <summary>
    /// 当前所在页面的 tag（NavigateTagCore 每次导航时更新）。
    /// 用途：未处理异常写日志时带上它 —— 否则「布局循环」这类只在某一页出现的问题根本没法定位。
    /// </summary>
    public static string CurrentTag { get; private set; } = "home";

    /// <summary>标题栏小图标（MainWindow 把站点图标塞进来）。</summary>
    public void SetIcon(IImage? source) => TitleIcon.Source = source;

    /// <summary>数据加载完之后调用一次：显示数据来源、进首页。</summary>
    public void Init()
    {
        // 这一步会真正把首页控件树建起来 —— 是 MainWindow 构造过程中最容易抛异常的一段。
        // 打点留痕，出问题时日志会直接停在出错的那一行前。
        Platform.PortLog.Step("ShellPage.Init: 开始（即将创建首页控件树）");

        TitleText.Text = ShellConfig.AppName;

        var start = StartupPageTag();
        Platform.PortLog.Step("ShellPage.Init: 起始页 = " + start);

        SelectTag(start);
        NavigateTag(start);
        Platform.PortLog.Step("ShellPage.Init: 首页导航完成 = " + start);

        // ⚠️ 2026-10-07（Nick：「导航栏左边跟随页面切换变位置的那根带颜色的竖线哪去了，
        //    在二级菜单里尤其明显」）：Init 阶段 NavigationView 的项容器还没 realize ——
        //    此刻往**折叠的分组**里塞 SelectedItem，FA 的选中指示条（那根竖线）找不到落点，
        //    整条就不画；IsExpanded 也可能被随后的首次布局冲掉。
        //    所以布局停稳之后再补钉一次（详见 ReassertNavSelection）。
        Dispatcher.UIThread.Post(() => { ReassertNavSelection(); FixGroupFlyoutPlacement(); }, DispatcherPriority.Loaded);

        // 再补一发延时兜底：NavigationView 的项容器是**懒 realize** 的，Loaded 那一发常常还是太早
        // （实测：起始页是二级页时，分组仍旧不展开、竖线仍旧不画）。450ms 后容器已就绪，这一发能钉住。
        var navFix = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        navFix.Tick += (_, _) => { navFix.Stop(); ReassertNavSelection(); FixGroupFlyoutPlacement(); };
        navFix.Start();

        UpdateThemeButton();
        Platform.PortLog.Step("ShellPage.Init: 全部完成");
    }

    /// <summary>
    /// 点完分组子项之后收尾：关掉可能残留的子项浮层，并按显示模式处理分组的展开标志。
    ///
    /// <para>⚠️ 2026-10-15（用户反馈「秒表按钮点第一次没反应，需要点第二次」）：根因是
    /// FluentAvalonia 的导航分组浮层。FA 2.4.1 里带子项的 <c>NavigationViewItem</c>
    /// （「内置工具」/「实验性功能」），**即使在展开（Left）模式**也把子项放在
    /// <c>AttachedFlyout</c>（Popup）里显示。点分组项打开它、点子项导航；
    /// 而**点完子项之后 FA 又会重建一次浮层**（实测：每次导航后残留浮层的 hwnd 都不同），
    /// 于是它一直挂着。Avalonia 的 Popup 会把「点击外部」用于自身关闭并**消费掉那一次指针按下**，
    /// 结果就是用户离开导航后点内容区的任何东西（模式卡、按钮…）都首击无效、第二击才生效；
    /// 键盘输入同理被吃掉。</para>
    ///
    /// <para>复现与定位证据（Win11 本机 UIA 自动化）：导航到工具页后枚举本进程顶层窗口，
    /// 会看到一条 <c>Avalonia-*</c> 无标题、rect 与导航面板重合的可见窗口；首次点击后它消失。
    /// 逐项排除：指针先停留再按下、先移动光标、ToolTip（另见 <see cref="DisableNavToolTips"/>）
    /// 都不是原因；手动 <c>SW_HIDE</c> 那扇原生窗口也无效（Avalonia 侧 Popup 状态照样是打开）——
    /// 只有「先点一次别处」才能解锁下一次点击，说明被消费的是**第一次输入事件本身**。</para>
    ///
    /// <para>⚠️ 窄条（图标）模式下只关浮层、<b>不</b>碰 <c>IsExpanded</c> 会让两者失步：
    /// FA 仍以为浮层开着，用户下次点分组项会被它当成「折叠」，子项就再也打不开了（实测）。
    /// 所以在**非展开模式**下顺带把 <c>IsExpanded</c> 复位，保证「展开标志 ↔ 浮层开关」始终一致。</para>
    ///
    /// <para>⚠️ 2026-10-16（用户反馈「左边大侧边栏展开后，点二级导航任意一个，一级就收起了」）：
    /// 上面那条复位**只对图标窄条成立**。大侧边栏（DisplayMode=Expanded，或窄条点开成整条面板
    /// 即 IsPaneOpen=true）里子项是**内联**铺在面板上的，复位会当场把一级分组收起来 ——
    /// 用户点的明明是二级项，一级却没了。所以这里改成「内联就保持展开、只关残留浮层」。</para>
    /// </summary>
    private void CloseGroupFlyout(NavigationViewItem group)
    {
        try
        {
            var root = group.GetVisualDescendants()
                            .OfType<Grid>()
                            .FirstOrDefault(g => g.Name == "NVIRootGrid");
            if (root is null) return;

            if (FlyoutBase.GetAttachedFlyout(root) is PopupFlyoutBase flyout && flyout.IsOpen)
                flyout.Hide();

            // ⚠️ 判据取「子项此刻是内联铺着、还是装在浮层里」，实测（FA 2.4.1 探针，
            //    .workbuddy/nav-probe，见提交说明）：
            //      · DisplayMode=Expanded  → IsPaneOpen=True，子项内联（视觉根=Window，浮层 closed）
            //      · 图标窄条（Compact 且面板未展开）→ IsPaneOpen=False，子项在 AttachedFlyout 里
            //      · 窄条点开成整条面板（Compact + IsPaneOpen=true）→ 子项同样内联
            //    也就是说 Both「Expanded」与「IsPaneOpen」时子项都是内联的，绝不能复位 IsExpanded；
            //    只有图标窄条（靠浮层显示子项）才需要复位。
            //    探针同时确认：FA 自己**不会**在选中子项后收起分组，所以用户看到的「一级收起」
            //    就是这里复位出来的。
            var inlineChildren = Nav.DisplayMode == NavigationViewDisplayMode.Expanded || Nav.IsPaneOpen;

            if (inlineChildren)
            {
                // 内联模式：分组必须保持展开（否则用户点二级、一级跟着消失）
                if (!group.IsExpanded) group.IsExpanded = true;
                return;
            }

            if (group.IsExpanded) group.IsExpanded = false;
        }
        catch
        {
            // 浮层关不掉不值得影响导航
        }
    }

    /// <summary>
    /// 关掉导航栏（含所有项与子项）的 ToolTip。
    ///
    /// <para>⚠️ 2026-10-15：导航项的 ToolTip 是独立的一条问题（与上面那组「首击被吞」的浮层问题
    /// 不是同一个东西，但同属「Popup 遮挡 / 吃点击」这一族）。鼠标在导航项上停留到悬停延时后，
    /// 提示浮层会弹在导航面板右侧、压到内容区上沿（真机截图已拍下），落在那一块的点击会先被浮层
    /// 消费掉。导航项的文案在展开面板里本来就完整可见，ToolTip 纯属冗余，宁可没有提示也不能丢点击
    /// —— 与第 14 轮「金刚按钮点了没反应」（见 <see cref="UpdateCaptionGlyphs"/> 的注释）同一取舍。</para>
    ///
    /// <para>⚠️ 关的是「服务」而不是逐项清 <c>Tip</c>：Avalonia 的 <c>ToolTipService.Update</c>
    /// 是从命中元素沿 <c>VisualParent</c> 链**向上**找第一个 <c>ServiceEnabled == false</c> 的祖先
    /// 并直接返回，所以在这里关一次即可覆盖整支子树，与项容器何时被懒 realize 无关。</para>
    /// </summary>
    private void DisableNavToolTips() => ToolTip.SetServiceEnabled(Nav, false);

    /// <summary>
    /// 修复 FA 2.4.1 分组浮层定位：窄条（Compact）模式下点「内置工具」「实验性功能」图标，
    /// 子项浮层应贴在图标右侧（RightEdgeAlignedTop），实测却掉到窗口左缘。
    /// 根因（FA 2.4.1 NavigationViewItem.OnNavigationViewItemBasePositionChanged）：
    /// Position 变化时控件模板尚未应用（_rootGrid == null），AttachedFlyout 的 Placement
    /// 从未被设置，浮层回退默认定位，且 FA 事后不会再补。
    /// 这里在项容器就绪后手动补设一次；幂等，可随兜底计时器重复调用。
    /// </summary>
    private void FixGroupFlyoutPlacement()
    {
        FixFlyoutPlacement(ClassroomToolsNav);
        FixFlyoutPlacement(SystemToolsNav);
        FixFlyoutPlacement(ExperimentalNav);

        static void FixFlyoutPlacement(NavigationViewItem item)
        {
            var root = item.GetVisualDescendants()
                           .OfType<Grid>()
                           .FirstOrDefault(g => g.Name == "NVIRootGrid");
            if (root is null)
            {
                Platform.PortLog.Step("NavFlyout: " + item.Tag + " 模板未就绪，本轮跳过");
                return;
            }
            if (FlyoutBase.GetAttachedFlyout(root) is PopupFlyoutBase flyout)
            {
                flyout.Placement = PlacementMode.RightEdgeAlignedTop;
                Platform.PortLog.Step("NavFlyout: " + item.Tag + " Placement=RightEdgeAlignedTop");
            }
        }
    }

    /// <summary>
    /// 启动落在哪一页：<c>--page=&lt;tag&gt;</c>，tag 跟导航项的一样（<c>--page=feedback</c>、
    /// <c>--page=machinecheck</c>、<c>--page=tools</c>…）。没给、给了不认识的、或者参数本身读不到，
    /// 一律回首页。
    ///
    /// 为什么留这个参数：以前想让程序直接落在某一页，只能临时改这个方法，验证完再改回来 ——
    /// 一个页面固定吃掉两趟全量构建。有了它就只编一趟，排障和自动验证都省一半时间。
    /// 正常启动不传这个参数，行为跟以前一模一样。
    /// </summary>
    private static string StartupPageTag()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (!arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) continue;
                var tag = arg["--page=".Length..].Trim();
                if (tag.Length > 0) return tag;
            }
        }
        catch
        {
            // 命令行读不到就当没给
        }

        return "home";
    }

    /// <summary>外部（首页卡片等）跳转导航用：先高亮导航项，再切页面。</summary>
    public void NavigateTo(string tag)
    {
        // ⚠️ 防重入只挡「SelectTag 内部回环出来的 ItemInvoked」，别再自己压一遍闸 ——
        //    SelectTag 开头就检查 _inSelectTag，外面先置 true 会让它直接 no-op，
        //    高亮只能指望导航完成后的 SyncNavSelectionToFrame 兜底（跳同一页时连那次都没有）。
        if (_inSelectTag) return;
        SelectTag(tag);
        NavigateTag(tag);
    }

    /// <summary>
    /// 从外部（首页卡片等）进「实验性功能」：切到总览页，并把分组展开。
    /// 展开这一步是给"从外面跳进来"补的 —— 不然看不出这一组里还有子项。
    /// ⚠️ 在导航栏里点父项那条路**不走它**：那种情况 NavigationView 自己会 toggle 展开/收起。
    /// </summary>
    public void NavigateToExperimental()
    {
        NavigateTo("experimental");
        ExperimentalNav.IsExpanded = true;
    }

    /// <summary>
    /// 从外部（首页卡片等）进「内置工具」：切到工具索引页，并把分组展开。
    /// 与 <see cref="NavigateToExperimental"/> 对称 —— 从外面跳进来时看不出这一组还有子项。
    /// </summary>
    public void NavigateToTools()
    {
        NavigateTo("tools-classroom");
        ClassroomToolsNav.IsExpanded = true;
    }

    /// <summary>
    /// 进 Hub AI 对话页（软件下载页的入口卡、设置页的「去看看」都调它）。
    /// 2026-10-18 上游新增；2026-10-22 移植。
    /// </summary>
    public void NavigateToHubAi() => NavigateTo("hubai");

    /// <summary>进 Hub AI 二级设置页（设置 → Hub AI 那张卡）。</summary>
    public void NavigateToHubAiSettings() => NavigateTo("hubai-settings");

    /// <summary>
    /// 跳到「内置工具」里的某个工具页（浮窗里的「详细设置」用）。
    /// 先把工具列表页铺一层，这样工具页左上角的返回按钮能正常退回列表。
    /// </summary>
    public void NavigateToTool(Type pageType) => NavigateToTool(pageType, null, viaList: true);

    /// <summary>
    /// 带参数跳工具页。
    /// <paramref name="viaList"/> = false 时**不铺工具列表**：返回一次就回到来的那一页 ——
    /// 软件详情页点「校验」去哈希工具，用户心理是"看一眼再回来"，不该先退到工具列表。
    /// 导航高亮钉到**这一页自己的子项**上（反查 ToolCatalog，见 TagForPage 的注释）——
    /// 竖线跟着页面走，而不是停在「内置工具」父项上。
    /// </summary>
    public void NavigateToTool(Type pageType, object? parameter, bool viaList = true)
    {
        SelectTag(TagForPage(pageType) ?? "tools-classroom");
        if (viaList && ContentFrame.CurrentSourcePageType != typeof(ToolsPage))
        {
            // 铺列表页只为让工具页左上角的"返回"能退回列表 —— 这次导航要**压掉进页动画**
            //（SuppressNavigationTransitionInfo）。否则两次连续导航各播一次入场动画，
            // 从悬浮窗/侧边栏打开时，用户看到的就是"加载动画播了两次"（列表闪一下、工具页再闪一下）。
            // 列表页只是垫背的，不该有存在感；工具页自己那一次动画保留。
            ContentFrame.Navigate(typeof(ToolsPage), null,
                new FluentAvalonia.UI.Media.Animation.SuppressNavigationTransitionInfo());
        }
        ContentFrame.Navigate(pageType, parameter);
    }

    /// <summary>
    /// 进反馈中心的**表单页**（「报告问题」/「提出建议」卡片单击时调）。
    ///
    /// 导航高亮一起切到「反馈中心」—— 人确实还在反馈中心这一区里，只是从"选类型"走到了"填内容"。
    /// ⚠️ 这里**不铺选择页**：用户本来就是从选择页点进来的，它已经在返回栈里，
    ///    导航栏返回按钮（<c>ContentFrame.GoBack()</c>）一按就回去。
    /// </summary>
    public void NavigateToFeedbackForm(string kind)
    {
        SelectTag("feedback");
        ContentFrame.Navigate(typeof(FeedbackFormPage), kind);
    }

    /// <summary>设置页「关于」里的法律条款入口：打开条款页并定位到指定文档（agreement/privacy/disclaimer）。</summary>
    public void NavigateToLegal(string key)
    {
        ContentFrame.Navigate(typeof(LegalPage), key);
    }

    /// <summary>只切页面，不动导航高亮（软件下载页内部按分类切换时用）。</summary>
    public void NavigateTag(string tag)
    {
        try
        {
            NavigateTagCore(tag);
        }
        catch (Exception ex)
        {
            // 某个页面自己加载失败（比如 XAML 里引了不存在的资源键）不该把整个应用带走 ——
            // 之前踩过：应用会记住上次停留的页，页面一坏就变成「一启动就崩」，用户连设置都进不去。
            LogNavFailure(tag, ex);
            if (tag != "home")
            {
                try { NavigateTagCore("home"); } catch { }
            }
        }
    }

    private void NavigateTagCore(string tag)
    {
        CurrentTag = tag;   // 崩溃日志靠它记下"当时在哪一页"（见 App.OnUnhandledException）

        // 内置工具子页：tag 就是 ToolCatalog 里的 Id（导航子项在 ShellPage.axaml 里 XAML 声明，
        // 见该处长注释 —— 必须 XAML 声明，FA 2.4.1 才把选中竖线画到子项上）。
        // 按 Id 反查拿页面类型，工具清单只有一份，不用给每个工具单独写 case。
        var toolDef = Data.ToolCatalog.All.FirstOrDefault(t => t.Id == tag);
        if (toolDef?.Page is not null)
        {
            // ⭐ 载体选择（上游 2026-10-25 补，与工具窗口 NavigateTo 同一套）：
            //    有 Web 版、本机跑得起来 → 宿主页；否则原生页。⛔ 降级对用户是无感的，别加提示。
            // ⚠️ 为什么补：以前这里直接 Navigate 原生页，漏了载体检查 —— 导航栏点击、--page= 直达
            //    两条路全都进不了网页版，同一工具两副嘴脸。
            if (Services.ToolCarrier.RequestFor(toolDef.WebToolId, toolDef.Page, null) is { } toolRequest)
                ContentFrame.Navigate(typeof(Pages.ToolHostPage), toolRequest);
            else
                ContentFrame.Navigate(toolDef.Page);
            return;
        }

        switch (tag)
        {
            case "announce":
                // 教室喊话（上游 2026-10-25：从工具里提出来单独成项）。纯原生页，不经 ToolCarrier ——
                // 它的本体是「开关 + 后台轮询 + 全屏播报」，不是可替换的网页小工具。
                ContentFrame.Navigate(typeof(Pages.Tools.AnnouncePage));
                return;
            case "submit":
                ContentFrame.Navigate(typeof(SubmitPage));
                return;
            case "feedback":
                ContentFrame.Navigate(typeof(FeedbackPage));
                return;
            case "feedback-form":
                // 反馈中心的表单页。正常流程是从「反馈中心」点类型卡进来（带 kind 参数，
                // 走 NavigateToFeedbackForm）；这个 case 只服务 --page=feedback-form 直达 ——
                // 不带参数，页面自己退回草稿里记着的类型。
                ContentFrame.Navigate(typeof(FeedbackFormPage));
                return;
            case "downloads":
                ContentFrame.Navigate(typeof(DownloadsPage));
                return;
            case "machinecheck":
                ContentFrame.Navigate(typeof(MachineCheckPage));
                return;
            case "easiguard":
                ContentFrame.Navigate(typeof(EasiNoteGuardPage));
                return;
            case "procguard":
                ContentFrame.Navigate(typeof(ProcessGuardPage));
                return;
            case "virtualkeyboard":
                ContentFrame.Navigate(typeof(VirtualKeyboardPage));
                return;
            case "autoshutdown":
                ContentFrame.Navigate(typeof(AutoShutdownPage));
                return;
            // ── 上游 dv1.1.3-insider1.3 ~ dv1.2.0-insider1.1 新增的四项（2026-10-18 移植） ──
            case "uninstall":
                ContentFrame.Navigate(typeof(UninstallPage));
                return;
            case "appdata":
                ContentFrame.Navigate(typeof(AppDataPage));
                return;
            case "startup":
                ContentFrame.Navigate(typeof(StartupPage));
                return;
            case "systemrepair":
                ContentFrame.Navigate(typeof(SystemRepairPage));
                return;
            case "experimental":
                // 「实验性功能」分组的父项**自己就是总览入口**：点它 = 展开/收起分组 + 进这一页。
                ContentFrame.Navigate(typeof(ExperimentalPage));
                return;
            case "changelog":
                ContentFrame.Navigate(typeof(ChangelogPage));
                return;
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage));
                return;
            case "log-viewer":
                // 设置 → 诊断 → 日志查看（原版 2026-10-02 起是设置里的正式入口，不出现在导航栏）。
                ContentFrame.Navigate(typeof(LogViewerPage));
                return;
            case "sidebar":
                ContentFrame.Navigate(typeof(SidebarLayoutPage));
                return;
            case "tools-classroom":
                // 课堂工具（2026-10-22 自上游移植：原「内置工具」拆成两项）
                ContentFrame.Navigate(typeof(ToolsPage), "classroom");
                return;
            case "tools-system":
                // 系统工具（同上）：同一个 ToolsPage 的另一种形态
                ContentFrame.Navigate(typeof(ToolsPage), "system");
                return;
            case "hubai":
                // Hub AI 对话页（2026-10-18 上游新增）：导航上半区，紧跟「软件下载」
                ContentFrame.Navigate(typeof(HubAiPage));
                return;
            case "hubai-settings":
                // Hub AI 二级设置页：不在导航栏，入口是「设置 → Hub AI」那张卡
                ContentFrame.Navigate(typeof(HubAiSettingsPage));
                return;
            case "tools":
                // 兼容旧 tag（历史调用点还在用的都走课堂组）
                ContentFrame.Navigate(typeof(ToolsPage), "classroom");
                return;
            case "mirror":
                // 系统镜像下载（2026-10-02：没有 tag 的话排障探针进不去这一页 —— 加上直达入口）
                ContentFrame.Navigate(typeof(Pages.Tools.MirrorToolPage));
                return;
            case "apps":
                ContentFrame.Navigate(typeof(SoftwarePage), "");
                return;
            case "home":
            default:
                if (tag.StartsWith("cat:", StringComparison.Ordinal))
                    ContentFrame.Navigate(typeof(SoftwarePage), tag.Substring(4));
                else
                    ContentFrame.Navigate(typeof(WelcomePage));
                return;
        }
    }

    /// <summary>页面加载失败的兜底日志（和 App 的崩溃日志写同一个文件，事后好查）。</summary>
    private static void LogNavFailure(string tag, Exception ex)
    {
        try
        {
            Core.AppLog.Error("crash", $"导航到「{tag}」失败: {ex}");
        }
        catch
        {
            // 日志写不进去就算了，别在这里再抛
        }
    }

    /// <summary>防重入闸：SelectTag 会设置 SelectedItem / IsSelected，FA 内部会连环触发
    ///     ItemInvoked → NavigateTo → SelectTag；嵌套一圈就可能把 FA 的 ChangeSelection 走崩
    ///     （2026-10-24 崩溃 0xc0000005，dump 实锤栈：SelectExperimentalHighlight → set_SelectedItem
    ///      → ChangeSelection → RaiseItemInvoked → Nav_ItemInvoked → …）。</summary>
    private bool _inSelectTag;

    private void SelectTag(string tag)
    {
        if (_inSelectTag) return;
        _inSelectTag = true;
        try
        {
        // 「实验性功能」是个分组父项，它**不进 NavigationView 的选中体系**（原因见 ShellPage.axaml 的注释）——
        // 高亮得手动打：先清掉别的选中项，再点亮父项自己。
        // ⚠️ 这里**不碰 IsExpanded**：点父项时 NavigationView 自己会 toggle 展开/收起，
        //    在这儿硬展开的话，分组就再也收不起来了（每次点都被撑开）。
        //    需要"顺带展开"的入口（首页卡片那种）走 NavigateToExperimental。
        if (tag == "experimental")
        {
            SelectExperimentalHighlight();
            return;
        }

        ExperimentalNav.IsSelected = false;      // 走别的页时，手动把父项的高亮熄掉

        foreach (var top in AllTopItems())
        {
            if ((top.Tag as string) == tag)
            {
                Nav.SelectedItem = top;
                return;
            }

            // 往下一层找（「实验性功能 → 本机核实」这种子项）。
            // 找到子项时顺手把父项**展开** —— 否则高亮藏在折叠的组里，看着像点了没反应。
            foreach (var child in top.MenuItems.OfType<NavigationViewItem>())
            {
                if ((child.Tag as string) != tag) continue;

                top.IsExpanded = true;
                Nav.SelectedItem = child;
                // ⚠️ 2026-10-07：「内置工具」这一组的子项是**代码动态添加**的。FA 在分组尚未 realize 时
                //    拿到子项的 SelectedItem，会认不到这个子项、把选中**退回父项** ——
                //    现象就是进「随机抽号」时竖线压在「内置工具」上（实测；XAML 声明的「实验性功能」子项无此问题）。
                //    对策：等这一轮布局把分组撑开、子项容器真的存在之后，再补设一次。
                top.IsExpanded = true;
                var parent = top;
                var target = child;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        parent.IsExpanded = true;
                        if (!ReferenceEquals(Nav.SelectedItem, target))
                        {
                            Nav.SelectedItem = null;      // 先清空，逼 FA 重新定位指示条
                            Nav.SelectedItem = target;
                        }
                    }
                    catch { }
                }, DispatcherPriority.Background);
                return;
            }
        }
        }
        finally
        {
            _inSelectTag = false;
        }
    }
    /// <summary>
    /// 把导航高亮打在「实验性功能」父项上。
    /// ⚠️ 父项**不能塞进 Nav.SelectedItem**：NavigationView 拿到一个"带子项的分组头"时会把选中
    ///    转给第一个子项 —— 这正是当年"点实验性功能却跳到本机核实"的根因。所以只能手动点亮。
    /// ⚠️ 也**不能只设 SelectedItem = null**：那样上一个选中的项还会留着选中底色
    ///    （实测：进总览页后「首页」那块底色还在），得挨个熄掉。
    /// </summary>
    private void SelectExperimentalHighlight()
    {
        // ⛔ 2026-10-24：这里原来有 Nav.SelectedItem = null —— FA 2.4.1 的
        //    ChangeSelection 在"选中项带子项/分组"的树状态下会被这句带崩（0xc0000005，dump 实锤）。
        //    只走 IsSelected 反选：视觉上同样能把上一项的底色清掉（IsSelected=false 会反选）。
        foreach (var item in AllNavigationItems()) item.IsSelected = false;
        ExperimentalNav.IsSelected = true;
    }

    /// <summary>导航里的全部项（含一层子项）—— 手动改高亮时用。</summary>
    private IEnumerable<NavigationViewItem> AllNavigationItems()
    {
        foreach (var top in AllTopItems())
        {
            yield return top;
            foreach (var child in top.MenuItems.OfType<NavigationViewItem>()) yield return child;
        }
    }

    /// <summary>导航栏最外层的那批项（上半区 + 下半区，不含子项）。</summary>
    private IEnumerable<NavigationViewItem> AllTopItems()
        => Nav.MenuItems.OfType<NavigationViewItem>()
              .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>());

    /// <summary>上一次从导航栏派发下来的 <c>ItemInvoked</c>（tag + 时刻），用于丢弃 FA 的重复派发。</summary>
    private string _lastInvokedTag = "";
    private long _lastInvokedAt;

    private void Nav_ItemInvoked(object? sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item) return;

        // ⚠️ 没有 Tag 的项不导航（防止 `?? "home"` 那种兜底把"点分组"变成"跳回首页"）。
        if (item.Tag is not string tag || tag.Length == 0) return;

        // ⚠️ 2026-10-06：FluentAvalonia 2.4.1 的 NavigationView 对**展开分组里的子项**
        //    （如「内置工具 → 图片取色」）会把 ItemInvoked 派发**两次**（间隔 6~60ms、tag 完全相同）。
        //    后果：同一页被构建两遍、切页淡入也播两遍（用户反馈「页面加载动画会显示两遍」）。
        //    这里把紧接着的重复派发丢掉。顶层项只派发一次，不受影响。
        var now = Environment.TickCount64;
        if (tag == _lastInvokedTag && now - _lastInvokedAt < 350)
            return;
        _lastInvokedTag = tag;
        _lastInvokedAt = now;

        // ⚠️ 走 NavigateTo 而不是 NavigateTag：点顶层项时 NavigationView 本来也会自己改高亮，
        //    但**子项的高亮它靠不住**（「实验性功能 → 本机核实」这种嵌套项，选中态经常不跟着走）。
        //    显式 SelectTag 一遍，两条路径都吃到同一套高亮逻辑。
        NavigateTo(tag);

        // ⚠️ 2026-10-15：点的是分组子项 → 父分组的子项浮层必须主动关掉，否则它会一直挂着，
        //    并把用户离开导航后的**第一次点击**吃掉（用户报的「秒表按钮点第一次没反应」）。
        //    详见 CloseGroupFlyout 的注释。
        //    延后一帧执行：FA 在导航收尾时会重建这个浮层，得等它建完再关。
        var group = AllTopItems()
            .FirstOrDefault(top => top.MenuItems.OfType<NavigationViewItem>().Contains(item));
        if (group is not null)
            Dispatcher.UIThread.Post(() => CloseGroupFlyout(group), DispatcherPriority.Background);
    }

    // ══════════════════════════════════════════════════════════════
    //  全局搜索（2026-10-18 自上游 1.2.0 移植）
    //  WinUI 用 AutoSuggestBox 的原生建议列表；Avalonia 没有该控件 →
    //  这里用 TextBox + 自绘结果弹层（Popup + StackPanel），交互对齐：
    //  输入即查、回车进第一条、点击进对应页、Esc 收起、点外面收起。
    // ══════════════════════════════════════════════════════════════

    /// <summary>当前搜索结果（扁平化，回车取第一条）。</summary>
    private readonly List<Data.SearchHit> _searchHits = new();

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = (SearchBox.Text ?? "").Trim();
        _searchHits.Clear();
        SearchResults.Children.Clear();

        if (query.Length == 0)
        {
            SearchPopup.IsOpen = false;
            return;
        }

        foreach (var group in Data.SearchIndex.Query(query))
        {
            if (group.Hits.Count == 0) continue;

            // 组标题（与站点一致的"软件 / 内置工具 / 页面"分组）
            SearchResults.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontSize = 11,
                Opacity = 0.5,
                Margin = new Thickness(8, 6, 8, 2),
            });

            foreach (var hit in group.Hits)
            {
                _searchHits.Add(hit);
                SearchResults.Children.Add(BuildSearchRow(hit));
            }
        }

        SearchPopup.IsOpen = _searchHits.Count > 0;
    }

    /// <summary>一条搜索结果：图标/首字 + 标题 + 副标题 + 类型小字。</summary>
    private Control BuildSearchRow(Data.SearchHit hit)
    {
        var grid = new Grid { MinHeight = 44, Margin = new Thickness(4, 0, 4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconHost = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(6),
            Background = Services.ThemeBrush.Get(this, "SubtleFillColorSecondaryBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        if (hit.Kind == Data.SearchHitKind.App)
        {
            iconHost.Child = new TextBlock
            {
                Text = hit.IconLetter, FontSize = 13, FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }
        else
        {
            iconHost.Child = new FontIcon
            {
                Glyph = hit.IconGlyph, FontSize = 14,
                Foreground = Services.ThemeBrush.Get(this, "AccentTextFillColorPrimaryBrush"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }
        grid.Children.Add(iconHost);

        var text = new StackPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(10, 0, 0, 0);
        text.Children.Add(new TextBlock
        {
            Text = hit.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (hit.Subtitle.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = hit.Subtitle, FontSize = 12, Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        grid.Children.Add(text);

        var badge = new TextBlock
        {
            Text = hit.Badge, FontSize = 11, Opacity = 0.6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetColumn(badge, 2);
        badge.Margin = new Thickness(10, 0, 6, 0);
        grid.Children.Add(badge);

        var row = new Button
        {
            Content = grid,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        row.Click += (_, _) => GoSearchHit(hit);
        return row;
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchPopup.IsOpen = false;
            SearchBox.Text = "";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _searchHits.Count > 0)
        {
            GoSearchHit(_searchHits[0]);
            e.Handled = true;
        }
    }

    /// <summary>选中一条结果：软件进详情页，工具 / 页面走导航 tag。</summary>
    private void GoSearchHit(Data.SearchHit hit)
    {
        SearchPopup.IsOpen = false;
        SearchBox.Text = "";

        if (hit.Kind == Data.SearchHitKind.App)
        {
            // 软件直接进详情页（与 SoftwarePage 卡片点击同一条路：Frame.Navigate(DetailPage, appId)）。
            ContentFrame.Navigate(typeof(DetailPage), hit.Key);
            SyncNavSelectionToFrame();
            return;
        }

        if (hit.Target.Length > 0) NavigateTo(hit.Target);
    }
}
