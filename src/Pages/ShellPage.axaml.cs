using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
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

        // 角标只在本版本装好后的头两周出现（见 BadgesAreFresh）。
        // ⚠️ 必须在 ApplyNavLayout 之前算 —— 那里会调 ApplyNavBadges。
        _badgesFresh = BadgesAreFresh();

        // 全局搜索（2026-10-18 自上游 1.2.0 移植）：结果弹层挂到搜索框下面。
        SearchPopup.PlacementTarget = SearchBox;
        SearchPopup.Placement = Avalonia.Controls.PlacementMode.Bottom;

        // ⚠️ 移植新增：WinUI 的 Frame 自动接 Page 生命周期，这里必须显式挂桥（幂等，可重复调用）
        Navigation.Attach(ContentFrame);

        // ⚠️ 对应原版 ActualThemeChanged：Avalonia 里是 StyledElement.ActualThemeVariantChanged
        ActualThemeVariantChanged += (_, _) => UpdateThemeButton();

        // 导航项右侧的 New / Beta 角标：必须在可移动项与实验性功能子项都就位之后挂。
        // ⚠️ 放到 Loaded 而不是构造函数里 —— 角标底色来自 ThemeBrush，要等这个控件进了树、
        //    拿到真实的 ActualThemeVariant 才准（构造期拿到的是系统主题，跟应用主题可能对不上）。
        Loaded += (_, _) => ApplyNavBadges();
        ActualThemeVariantChanged += (_, _) => ApplyNavBadges();

        // 「内置工具」分组的子项、以及「查阅单词 / 文档库 / 教室喊话」这三个可变页面，
        // 全按 Data/NavLayout 摆位（2026-11-04：用户可以在设置里把常用的拎到一级、把不常用的收进分组）。
        // ⚠️ 本移植版的导航项（含两个工具分组的子项）都是 XAML 里硬写的，不是代码生成 ——
        //    ApplyNavLayout 会**复用**这些对象本身（按 tag 反查后原样插回去），不重建。
        ApplyNavLayout();

        // 自检必须放在 ApplyNavLayout 之后：检查的是摆位完成后的实际结果。
        VerifyToolNavItems();

        // 下载任务数变了 → 同步「任务进行」上的徽标（在下载就不占着界面，但得让人随时看见有几个在下）
        Services.DownloadManager.Current.Changed += UpdateDownloadBadge;
        UpdateDownloadBadge();

        // 导航栏一律不弹 ToolTip（浮层会吃掉内容区控件的首击，细节见方法注释）。
        // 关的是「服务」而非常量属性，与项容器何时 realize 无关，所以构造期设一次即可。
        DisableNavToolTips();

        // 面板显示模式变了（宽 ↔ 窄）→ 重新算内容区要不要给导航按钮让位；
        // 顺带把起始页那笔「等内联再钉」的高亮补上（见 SelectTag 里的锚点崩溃说明）。
        // ⚠️ 补钉必须再推迟一轮：DisplayModeChanged 是在模式刚切完、子项容器还没重建时发的，
        //    当场设 SelectedItem 会踩到同一处锚点断言。
        Nav.DisplayModeChanged += (_, _) =>
        {
            ApplyPaneInset();

            if (_pendingChildTag is not { } pending) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_pendingChildTag == pending) SelectTag(pending);
            }, DispatcherPriority.Loaded);
        };
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

    // ══════════════════════════════════════════════════════════════
    //  导航栏布局自定义（2026-11-04 需求，自上游 dv1.2.0-insider1.10 移植）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 按 <see cref="NavLayout"/> 把可移动项摆到该在的位置（2026-11-04 导航自定义）。
    ///
    /// <para>
    /// 可重复调用：设置页改完一项就调一次 <see cref="ReloadNavLayout"/>，导航立刻跟着变。
    /// 做法是**先全部撤下来再重摆** —— 逐个移动要处理索引漂移，而且「从一个分组换到另一个分组」
    /// 根本没法用 Remove/Insert 在原对象上表达。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 撤的是**导航项对象本身**：XAML 里带 x:Name 的那几个（教室喊话 / 查阅单词 / 文档库）
    /// 撤下来后对象还在，原样插回去即可（图标、角标、无障碍名字都不丢），不必重建；
    /// 两个工具分组下面的子项同理 —— 它们本来就在 XAML 里声明，这里只是**挪位置**。
    /// 本移植版从不重建已有项，只有「清单里有、XAML 里没有」的新 tag 才现造一个。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 顺序规则与上游不同（本移植版的导航顺序本来就与上游不一致，硬要求是"默认状态与改动前逐项一致"）：
    /// 上游把「查阅单词 / 文档库」插到「课堂工具」之前、把「教室喊话」插到「函数图像」之前；
    /// 本移植版这三项原本就在两个工具分组**之后**（查阅单词 → 侧边布局 → 教室喊话），
    /// 所以改成**锚点表**：每一项提到一级时插在它本来就该在的那个固定项之前（见 <see cref="AnchorFor"/>）。
    /// 这样两个设置数组都为空时，结果与改动前逐项一致：项、顺序、图标、文案全不变。
    /// </para>
    /// </summary>
    private void ApplyNavLayout()
    {
        // 0) 默认存档（两个数组都为空）= 用户没改过布局 ⇒ **一律不碰导航树**，直接用 XAML 里那份。
        //
        // ⚠️ 2026-11-05 实测出来的坑：这里原本无条件「先全撤下再逐项插回」，结果把
        //    FluentAvalonia 的 ViewportManager 搞坏了 —— 打开「实验性功能」分组下的任何子页
        //    （--page=pagefile / ppttouch / ghostfile / machinecheck …）都会在首帧抛
        //    InvalidOperationException: An anchor control must be a visual descendent of the ScrollContentPresenter，
        //    整个进程当场退出。选中二级子项时 FA 要拿 NavigationViewItem 的容器去注册滚动锚点，
        //    而那个容器已经被我们 Remove 过一次、引用悬空了。
        //    默认状态本来就不需要搬动任何一项（XAML 的顺序就是默认顺序），跳过即可，零风险。
        var settings = App.Settings.Current;
        if (settings.NavTopLevel is not { Length: > 0 } && settings.NavSecondLevel is not { Length: > 0 })
        {
            ApplyNavBadges();
            return;
        }

        // 0.1) 先把"现在导航里有的对象"按 tag 记下来 —— 搬动时复用同一个实例（见方法注释）
        var existing = new Dictionary<string, NavigationViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in EnumerateNavItems(Nav.MenuItems))
        {
            if (item.Tag is string tag && tag.Length > 0) existing[tag] = item;
        }

        var managed = new HashSet<string>(
            NavLayout.All.Select(p => p.Tag), StringComparer.OrdinalIgnoreCase);

        // 1) 把上次摆在一级的可移动项全部撤下来（含 XAML 里那几个静态项）
        for (var i = Nav.MenuItems.Count - 1; i >= 0; i--)
        {
            if (Nav.MenuItems[i] is NavigationViewItem { Tag: string tag } && managed.Contains(tag))
                Nav.MenuItems.RemoveAt(i);
        }

        // 2) 两个分组的子项整体摘下来（对象留在 existing 里，第 5 步按新顺序挂回去）
        ClassroomToolsNav.MenuItems.Clear();
        SystemToolsNav.MenuItems.Clear();

        // 3) 一级：按 NavLayout 的顺序逐项插到各自锚点前面
        foreach (var placeable in NavLayout.All.Where(p => NavLayout.IsTopLevel(p.Tag)))
            InsertTopLevel(placeable, existing);

        // 4) 二级：按分组挂回各自父项。顺序与总览页卡片同源（NavLayout.SecondLevel），两处不会对不上。
        foreach (var group in new[] { ToolGroup.Classroom, ToolGroup.System })
        {
            var parent = group == ToolGroup.System ? SystemToolsNav : ClassroomToolsNav;
            foreach (var placeable in NavLayout.SecondLevel(group))
                parent.MenuItems.Add(NavigationItemFor(placeable, existing));
        }

        // 5) 当前页可能刚被搬了位置（一级 ⇄ 二级）—— 重新点亮，否则高亮会停在已撤下的那一项上。
        // ⚠️ 先清空再选：上面增删过 MenuItems，NavigationView 里缓存的选中容器可能已经悬空，
        //    直接重选会在下一帧踩到 ViewportManager 的 anchor 断言（见方法开头那段实测说明）。
        Nav.SelectedItem = null;
        SelectTag(CurrentTag);

        // 6) 角标：重建后新造出来的项还没挂（旧对象上的已随对象带走，_navLabels 记着原文，重复调用无害）
        ApplyNavBadges();

        AppLog.Step("shell", $"导航布局：一级 {Nav.MenuItems.OfType<NavigationViewItem>().Count()} 项，"
            + $"课堂工具 {ClassroomToolsNav.MenuItems.Count} 项，系统工具 {SystemToolsNav.MenuItems.Count} 项，"
            + $"提到一级的 {NavLayout.All.Count(p => NavLayout.IsTopLevel(p.Tag))} 项");
    }

    /// <summary>
    /// 设置页改完导航项之后调这个，立即生效（不重启、不重开窗口）。
    /// </summary>
    public void ReloadNavLayout()
    {
        ApplyNavLayout();

        // 正停在工具总览页时，卡片列表也得跟着变：那个页 NavigationCacheMode=Enabled，
        // 不重新导航一次就不会重算（变化本身在 OnNavigatedTo 里）。
        if (CurrentTag is "tools-classroom" or "tools-system") NavigateTo(CurrentTag);
    }

    /// <summary>
    /// 把一个提到一级的可移动项插到它的锚点之前。
    /// 锚点认不出来（理论上不会）就追加到末尾 —— 总比整项消失强。
    /// </summary>
    private void InsertTopLevel(
        NavPlaceable placeable, IReadOnlyDictionary<string, NavigationViewItem> existing)
    {
        var item = NavigationItemFor(placeable, existing);
        var anchor = AnchorFor(placeable);

        var index = anchor is null ? -1 : Nav.MenuItems.IndexOf(anchor);
        if (index < 0) index = Nav.MenuItems.Count;

        Nav.MenuItems.Insert(index, item);
    }

    /// <summary>
    /// 这一项提到一级时插在**哪个固定项之前**。
    ///
    /// <para>
    /// ⛔ 不能照抄上游的「课堂类插在课堂工具前、系统类插在系统工具前、教室喊话插在函数图像前」——
    ///    本移植版的静态导航顺序是「… 函数图像 / 课堂工具 / 系统工具 / 查阅单词 / 侧边布局 / 教室喊话 / 实验性功能」，
    ///    照抄会让默认状态下三项都换位置（硬要求：默认必须与改动前逐项一致）。
    /// </para>
    /// <para>
    /// 规则：查阅单词与文档库在「侧边布局」之前（文档库紧跟查阅单词，两者同属"查资料"这一类），
    /// 教室喊话在「实验性功能」之前；工具项则回到自己那一组的父项之前（课堂类在「课堂工具」前、
    /// 系统类在「系统工具」前）—— 默认状态下没有任何工具提到一级，所以这条只在用户改过之后才生效。
    /// 同一锚点下多项的先后由 <see cref="NavLayout.All"/> 的顺序决定（页面项在前、工具按工具清单顺序）。
    /// </para>
    /// </summary>
    private NavigationViewItem? AnchorFor(NavPlaceable placeable)
    {
        switch (placeable.Tag.ToLowerInvariant())
        {
            case "dictionary":
            case "docs":
                return SidebarNav;
            case "announce":
                return ExperimentalNav;
            default:
                return placeable.Group == ToolGroup.System ? SystemToolsNav : ClassroomToolsNav;
        }
    }

    /// <summary>
    /// 可移动项对应的导航项：导航里已经有这个 tag 就复用原对象，没有才现造一个。
    /// </summary>
    private NavigationViewItem NavigationItemFor(
        NavPlaceable placeable, IReadOnlyDictionary<string, NavigationViewItem> existing)
    {
        if (existing.TryGetValue(placeable.Tag, out var item)) return item;

        // ⚠️ 走到这里说明「NavLayout 里有、ShellPage.axaml 里没有」—— 加工具时别忘了同步 XAML
        //    （FA 2.4.1 的选中竖线只认 XAML 声明的子项，代码造的项能显示但高亮会退回父项）。
        AppLog.Warn("shell", $"导航项 {placeable.Tag} 不在 XAML 里，运行时补建（选中竖线可能不稳）");
        return new NavigationViewItem
        {
            Content = placeable.Name,
            Tag = placeable.Tag,
            IconSource = NavIconSource(placeable.Glyph),
        };
    }

    /// <summary>
    /// 代码造导航项时用的图标源（对应 WinUI 的 <c>new FontIcon { Glyph = … }</c> 那一手）。
    /// ⚠️ FA 里 <c>NavigationViewItem</c> 的图标属性叫 <c>IconSource</c>，且只收 IconSource 家族 ——
    ///    字体要显式带上 SymbolThemeFontFamily，否则会落到系统默认字体、画出一堆方块。
    /// </summary>
    private FontIconSource NavIconSource(string glyph)
    {
        var source = new FontIconSource { Glyph = glyph, FontSize = 24 };
        try
        {
            if (this.TryFindResource("SymbolThemeFontFamily", ActualThemeVariant, out var value)
                && value is FontFamily family)
                source.FontFamily = family;
        }
        catch
        {
            // 取不到就交给 FA 的默认模板处理（最坏是图标字体不对，不影响导航）
        }
        return source;
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
            // 校验时把两组合起来比。2026-10-29：按**集合**比而不是按顺序 ——
            // 分组之后两组的声明顺序与 ToolCatalog 的混合顺序本来就不一致，按 SequenceEqual
            // 每次启动都会误报一条不一致；真正要防的是"少一项 / 多一项"。
            // 「教室喊话」在 ToolCatalog 里但走导航一级项（见 ToolCatalog 末尾注释），不参与分组校验。
            // 自上游 1.8：「函数图像」同样走导航一级项，靠 ToolDef.Standalone 标记跳过分组校验。
            // 「实验性功能」组混装两类：ToolCatalog 里的工具（如自动关机，Group=System 却按风险放实验性），
            // 以及不进 ToolCatalog 的实验功能（程序专杀 / 虚拟键盘）—— 所以这一组只要求"能覆盖 catalog"，
            // 不要求反过来一一对应。
            var grouped = ClassroomToolsNav.MenuItems.OfType<NavigationViewItem>()
                .Concat(SystemToolsNav.MenuItems.OfType<NavigationViewItem>())
                .Select(i => i.Tag as string ?? "").Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            var experimental = ExperimentalNav.MenuItems.OfType<NavigationViewItem>()
                .Select(i => i.Tag as string ?? "").Where(id => id.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            var cat = Data.ToolCatalog.All
                .Where(t => !string.Equals(t.Id, "announce", StringComparison.Ordinal) && !t.Standalone)
                .Select(t => t.Id)
                .ToHashSet(StringComparer.Ordinal);

            var missing = cat.Except(grouped).Except(experimental).OrderBy(x => x).ToList();
            var extra = grouped.Except(cat).OrderBy(x => x).ToList();
            if (missing.Count > 0 || extra.Count > 0)
            {
                Platform.PortLog.Step("⚠️ 导航「内置工具」子项与 ToolCatalog 不一致："
                                      + (missing.Count > 0 ? "缺导航项=[" + string.Join(",", missing) + "] " : "")
                                      + (extra.Count > 0 ? "导航项不在 catalog=[" + string.Join(",", extra) + "] " : "")
                                      + cat.Count + " 个工具（两处要一起改）");
            }
        }
        catch
        {
            // 自检失败不该影响启动
        }
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

    // ══════════════════════════════════════════════════════════════
    //  导航项右侧的 New / Beta 角标（2026-11-04，自上游 dv1.2.0-insider1.10 移植）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 导航项右侧的文字角标：tag →（文字，是否用警告色）。
    ///
    /// <para>
    /// 目的：每次更新用户不知道加了什么，靠这个让新入口自己冒出来。三种状态的含义
    /// （2026-11-03 Nick 定的口径）：
    /// <list type="bullet">
    ///   <item><b>New</b>（绿色）＝ 这个版本刚加，用户第一次见；</item>
    ///   <item><b>Beta</b>（黄色）＝ 功能**已经确定要做**，只是还在打磨；</item>
    ///   <item><b>实验性功能</b>（页面标题旁的胶囊，<c>Controls/ExperimentalBadge</c>）＝
    ///         连要不要做都没定，随时可能拿掉 —— 和 Beta 不是一回事，别混用。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ⚠️ 不再需要"每次发版手动改这张表"：角标**只在本版本装好后的 15 天内出现**
    /// （<see cref="BadgeLifetimeDays"/>），过期自动消失，不用谁去删行（2026-11-04 Nick 定）。
    /// 换了版本会重新起算，所以新版里的新功能仍然会被提示一轮。
    /// 表里留着的行 = 这个功能还在"新 / Beta"的语义里，只是平时不显示；功能转正时再删。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, (string Text, bool Warning)> NavBadges =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ghostfile"] = ("New", false),
            ["dictionary"] = ("New", false),
            ["docs"] = ("New", false),
            ["ppttouch"] = ("Beta", true),
        };

    /// <summary>
    /// 导航角标挂多久（天）：**本版本第一次运行**起算。改这个数就行。
    /// <para>
    /// 15 天是 Nick 2026-11-04 定的口径 —— 装完头两周足够注意到，再长就变装饰。
    /// </para>
    /// </summary>
    private const int BadgeLifetimeDays = 15;

    /// <summary>本版本还在角标有效期内（构造时算一次，一次运行中不会变）。</summary>
    private bool _badgesFresh;

    /// <summary>
    /// 角标还能不能挂。顺带承担"本版本第一次运行"的打点：版本号跟存档里记的不一样就重新计时。
    /// <para>
    /// ⚠️ 记的是**首次运行**时间，不是安装时间 —— 装完隔几天才打开的情况更常见，按运行时间算才准。
    /// ⚠️ 存档读不出来 / 时间解析失败时返回 true（照常显示）：宁可多提示一次，
    ///    也别因为一个坏字段把功能提示整个吞掉。
    /// </para>
    /// </summary>
    private static bool BadgesAreFresh()
    {
        try
        {
            var settings = App.Settings.Current;
            var version = ShellConfig.ShellVersion;

            if (!string.Equals(settings.BadgeSinceVersion, version, StringComparison.Ordinal))
            {
                settings.BadgeSinceVersion = version;
                settings.BadgeSinceUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                App.Settings.Save();
                AppLog.Step("shell", $"导航角标：新版本 {version} 首次运行，{BadgeLifetimeDays} 天计时从这里开始");
                return true;
            }

            if (!DateTime.TryParse(settings.BadgeSinceUtc, CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out var since))
                return true;

            // 时钟被往回拨时 age 是负的，仍算"新鲜"（显示），不折腾用户
            var age = DateTime.UtcNow - since.ToUniversalTime();
            var fresh = age < TimeSpan.FromDays(BadgeLifetimeDays);

            AppLog.Step("shell", fresh
                ? $"导航角标：本版本第 {age.TotalDays:0.#} 天，还有 {(BadgeLifetimeDays - age.TotalDays):0.#} 天到期"
                : $"导航角标：本版本已过 {age.TotalDays:0.#} 天，到期不再显示");

            return fresh;
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "导航角标计时读取失败（照常显示）：" + ex.Message);
            return true;
        }
    }

    /// <summary>
    /// 导航项原本的文字。角标是把 <see cref="NavigationViewItem.Content"/> 换成「文字 + 角标」的组合，
    /// 换完就取不到原文了；而主题切换时要重造一次（颜色是代码设的具体值，不会自己跟着变），
    /// 所以第一次换之前把原文记下来。
    /// </summary>
    private readonly Dictionary<NavigationViewItem, string> _navLabels = new();

    /// <summary>把 <see cref="NavBadges"/> 挂到对应的导航项上。必须在导航项都建好之后调用。</summary>
    private void ApplyNavBadges()
    {
        try
        {
            foreach (var item in EnumerateNavItems(Nav.MenuItems))
            {
                if (item.Tag is not string tag) continue;

                // 记下原文（第一次进来时 Content 还是 XAML 里写的字符串）
                if (!_navLabels.ContainsKey(item) && item.Content is string label)
                    _navLabels[item] = label;

                if (!_navLabels.TryGetValue(item, out var text)) continue;

                // _badgesFresh = false（装好超过 15 天）时就当表是空的：一次也不挂，文字原样还原
                if (_badgesFresh && NavBadges.TryGetValue(tag, out var badge))
                {
                    // ⚠️ NavigationViewItem 默认 HorizontalContentAlignment = Left，
                    //    不改成 Stretch 的话内容不会被撑开，上面 Grid 的 * 列等于没设。
                    item.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                    item.Content = ComposeLabel(text, badge.Text, badge.Warning);

                    // 内容换成组合元素后，列表项自己念不出名字了，
                    // 显式把名字挂回列表项上（Avalonia 的 AutomationProperties 与 WinUI 同名）。
                    AutomationProperties.SetName(item, text);
                }
                else if (item.Content is not string)
                {
                    // 从表里删掉这一行 = 角标自动摘掉，文字原样还原
                    item.Content = text;
                }
            }
        }
        catch (Exception ex)
        {
            // 角标画不出来不值得影响导航
            AppLog.Catch("shell", "挂导航角标", ex, LogLevel.Debug);
        }
    }

    /// <summary>
    /// 文字和角标并排。用 <see cref="Grid"/> 而不是水平 <see cref="StackPanel"/>：
    /// 面板展开宽度 296（<c>OpenPaneLength</c>，2026-11-04 从 252 加宽 —— 252 下
    /// 「PowerPoint 触控优化」作为子项还多一层缩进，加个 Beta 就撑爆，实测会截成「PowerPoint 触控…」），
    /// 再长就让 Grid 的 <c>*</c> 列先省略文字，角标永远完整。
    ///
    /// <para>
    /// 角标右对齐到内容区右边缘，所以列与列之间看着有空白 —— 这是刻意的：
    /// 一列角标对齐成一条竖线，比各自紧贴文字更好扫读。
    /// ⚠️ 移植差异：WinUI 用 <c>Grid.ColumnSpacing = 8</c> 拉开两列，Avalonia 的 Grid **没有** ColumnSpacing，
    /// 改成给角标本身留左边距（8），观感一致。
    /// </para>
    /// </summary>
    private Control ComposeLabel(string text, string badgeText, bool warning)
    {
        var label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            // 字体不显式设：让它继承导航项自己的字体，跟其它项保持一致
        };

        var badge = MakeTextBadge(badgeText, warning);

        // 文字列用 *（占满剩余宽度、能省略），角标列 Auto（永远完整）。
        // ⚠️ 两列都 Auto 时水平布局**不会**约束文字 —— 文本原样画出去被裁掉，角标一起被挤出可视区。
        var grid = new Grid
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(label);
        grid.Children.Add(badge);

        AutomationProperties.SetName(grid, text);

        Grid.SetColumn(label, 0);
        Grid.SetColumn(badge, 1);

        return grid;
    }

    /// <summary>
    /// 文字角标：<see cref="Border"/> + <see cref="TextBlock"/> 两个原生基础控件的组合。
    ///
    /// <para>
    /// ⛔ 不用 <see cref="InfoBadge"/>：它原生只认数字和图标，要塞文字得整个替换 ControlTemplate ——
    /// 那就是"自己画一个控件"，跟"能原生就原生"这条铁律正相反（2026-11-04 Nick 定）。
    /// </para>
    ///
    /// <para>
    /// 2026-11-04 第二次调：Nick 对着 WinUI Gallery 的「Experimental」标签说我们这版
    /// "第三方味道很浓"。差在四处，全部往系统那边靠：
    /// <list type="bullet">
    ///   <item><b>字重</b>：SemiBold → Normal。小字加粗是网页 tag 的做法，系统标签一律常规字重；</item>
    ///   <item><b>内边距</b>：6,1 → 7,1。垂直只留 1 像素容易被压扁，水平给够才不显得挤；</item>
    ///   <item><b>底色</b>：自己算的"语义色 + 15% 透明" → 系统 <c>SystemFillColor*Background</c> 的**实色**
    ///         （深色下 Success 是 #393D1B、Caution 是 #433519）—— InfoBar 的严重性底色就是这对资源。
    ///         叠透明度会跟着窗口背景跑，Mica / 亚克力上发灰，这就是"第三方味"的来源；</item>
    ///   <item><b>圆角</b>：4，跟 InfoBar / 卡片同一档，⛔ 不要做成胶囊。</item>
    /// </list>
    /// 颜色仍取系统语义色（Success / Caution），深浅主题各自适配。
    /// </para>
    ///
    /// ⚠️ 移植说明：WinUI 的 <c>ThemeResource</c> 取法 → 本仓库一律走 <see cref="Services.ThemeBrush.Get"/>
    /// （它按元素所在那棵树取色；XAML 里才用 DynamicResource）。返回的就是 Brush，直接赋给 Border。
    /// </summary>
    private Border MakeTextBadge(string text, bool warning)
    {
        var fg = Services.ThemeBrush.Get(this,
            warning ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");

        // 底色用系统那对**调好的实色**（Success/Caution Background），⛔ 不是"语义色压透明度"。
        // 叠透明度会跟着窗口背景走，Mica / 亚克力上发灰发脏 —— 这正是"第三方味"的来源（2026-11-04）。
        // InfoBar 的严重性底色用的也是同一对资源。
        var bg = Services.ThemeBrush.Get(this,
            warning ? "SystemFillColorCautionBackgroundBrush" : "SystemFillColorSuccessBackgroundBrush");

        return new Border
        {
            Background = bg,
            // 4 = InfoBar / 卡片同一档圆角，跟系统控件对齐（3 会更像网页 chip）
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 1, 7, 1),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            // 左边 8 = 原版 Grid.ColumnSpacing（Avalonia 没有这一项）；
            // 右边 2 = 离导航项右边缘留一档，别真的顶到边
            Margin = new Thickness(8, 0, 2, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeight.Normal,
                Foreground = fg,
            },
        };
    }

    /// <summary>连子项一起往下走（实验性功能、课堂工具、系统工具这三组下面都是有子项的）。</summary>
    private static IEnumerable<NavigationViewItem> EnumerateNavItems(IList<object> items)
    {
        foreach (var raw in items)
        {
            if (raw is not NavigationViewItem item) continue;

            yield return item;

            foreach (var child in EnumerateNavItems(item.MenuItems)) yield return child;
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
    /// 浮在内容区左上角**，不给内容留位置 —— 而每个页面的标题正好也在左上角，于是两样东西叠在一起。
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

            // 「课堂工具 / 系统工具」共用同一个 ToolsPage，页型反查不出是哪一组 ——
            // 不纠正的话进系统工具页也会算成 tools-classroom，高亮被打回课堂工具
            // （现象：点系统工具，左侧指示条却压在课堂工具上）。
            if (tag == "tools-classroom" && ContentFrame.Content is ToolsPage toolsPage
                && toolsPage.Group == Data.ToolGroup.System)
                tag = "tools-system";

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
    /// <para>根因：<see cref="SelectTag"/> 在 ShellPage 构造 / <c>Init()</c>
    /// 阶段就会跑，而此刻 NavigationView（FluentAvalonia 2.4.1）的项容器尚未 realize：
    /// 往一个**折叠的分组**里设 <c>SelectedItem</c>，FA 的选中指示条（导航栏左边那根带颜色的竖线）
    /// 找不到可视落点就整条不画；<c>IsExpanded=true</c> 也可能被紧随其后的首次布局冲掉，
    /// 表现为二级菜单里选中竖线不显示。</para>
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
            // 自上游 1.10 新增的三项：返回 / GoBack 后高亮要跟着退
            "DocLibraryPage" => "docs",
            "PptTouchPage" => "ppttouch",
            "GhostFilePage" => "ghostfile",
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
            // 搜索框也必须列进来：它整块坐在标题栏里，
            // 不挖出来的话点它就是"系统拖动窗口"，输入框永远拿不到焦点。
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

            // 故意不挂 ToolTip：Tooltip 是独立 Popup 窗口，悬停弹出后紧接着的那次点击
            // 会被 Popup 的 dismiss 吃掉。三大金刚是高频点击件，宁可没有提示也不能丢点击。
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

        // Init 阶段 NavigationView 的项容器还没 realize：
        // 此刻往折叠的分组里塞 SelectedItem，FA 的选中指示条（那根竖线）找不到落点，
        // 整条就不画；IsExpanded 也可能被随后的首次布局冲掉。
        // 所以布局停稳之后再补钉一次（详见 ReassertNavSelection）。
        Dispatcher.UIThread.Post(() => { ReassertNavSelection(); FixGroupFlyoutPlacement(); }, DispatcherPriority.Loaded);

        // 再补一发延时兜底：NavigationView 的项容器是**懒 realize** 的，Loaded 那一发常常还是太早，
        // 起始页是二级页时分组仍旧不展开、竖线仍旧不画；450ms 后容器已就绪，这一发能钉住。
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
    /// <para>导航项的 ToolTip 是独立的一条问题（与「首击被吞」的浮层问题同属「Popup 遮挡 / 吃点击」一族）。
    /// 鼠标在导航项上停留到悬停延时后，提示浮层会弹在导航面板右侧、压到内容区上沿，
    /// 落在那一块的点击会先被浮层消费掉。导航项的文案在展开面板里本来就完整可见，
    /// ToolTip 纯属冗余，宁可没有提示也不能丢点击。</para>
    ///
    /// <para>关的是「服务」而不是逐项清 <c>Tip</c>：Avalonia 的 <c>ToolTipService.Update</c>
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
    /// 外部链接（csh://settings/…）进设置页的某个分组：切到设置页，再滚到那一块。
    /// 分组名认不出就停在页面顶部（<see cref="SettingsPage.ActivateSection"/> 自己兜底）。
    /// </summary>
    public void NavigateToSettings(string? section)
    {
        NavigateTo("settings");
        if (ContentFrame.Content is SettingsPage page) page.ActivateSection(section);
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

        // 这条路不经过 NavigateTagCore，授权提示得单独补一次
        PromptLicense("feedback-form");
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
            // 否则应用记住上次停留的页后，页面一坏就会「一启动就崩」，连设置都进不去。
            LogNavFailure(tag, ex);
            if (tag != "home")
            {
                try { NavigateTagCore("home"); } catch { }
            }
        }
    }

    /// <summary>需要在线授权的入口 → 提示文案。没列在这里的页面完全不受影响。</summary>
    private static readonly (string Tag, string Reason)[] LicensePrompts =
    {
        ("submit", "软件投稿要走官方服务端：当前不是正版，请输入激活码激活。"),
        ("announce", "教室喊话要走官方服务端：当前不是正版，请输入激活码激活。"),
        ("feedback", "反馈中心要走官方服务端：当前不是正版，请输入激活码激活。"),
        ("feedback-form", "提交反馈要走官方服务端：当前不是正版，请输入激活码激活。"),
        ("mirror", "系统镜像下载加速要走官方服务端：当前不是正版，请输入激活码激活。"),
        ("apps", "软件下载加速要走官方服务端：当前不是正版，请输入激活码激活。"),
    };

    /// <summary>
    /// 进入在线服务页面时主动提示一次授权。判定与去重都在 <see cref="Services.License.LicenseGate"/>：
    /// 官方构建、已激活、本次运行里已经点过「以后再说」的入口都不会再弹。
    /// </summary>
    private static void PromptLicense(string tag)
    {
        // 软件页的分类直达 tag 是 cat:&lt;分类&gt;，与 "apps" 是同一张页面
        var key = tag.StartsWith("cat:", StringComparison.Ordinal) ? "apps" : tag;

        foreach (var (candidate, reason) in LicensePrompts)
        {
            if (!string.Equals(candidate, key, StringComparison.Ordinal)) continue;
            _ = Services.License.LicenseGate.EnsureAsync(reason, key);
            return;
        }
    }

    private void NavigateTagCore(string tag)
    {
        CurrentTag = tag;   // 崩溃日志靠它记下"当时在哪一页"（见 App.OnUnhandledException）

        // 在线服务入口：非官方构建且未激活时，进来就提示一次（每页每次运行最多一次）
        PromptLicense(tag);

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
                // 自上游 1.8：导航项已移除，但这个 case 必须留着 —— 入口还有三处：
                // 软件下载页顶部按钮、首页「提交软件」卡片、命令行 --page=submit。
                // 删了它们会一起掉进 default 直接回首页，而且不报错。
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
            case "procguard":
                ContentFrame.Navigate(typeof(ProcessGuardPage));
                return;
            case "virtualkeyboard":
                ContentFrame.Navigate(typeof(VirtualKeyboardPage));
                return;
            case "autoshutdown":
                ContentFrame.Navigate(typeof(AutoShutdownPage));
                return;
            // ── 上游 dv1.1.3-insider1.3 ~ dv1.2.0-insider1.1 新增的两项（2026-10-18 移植） ──
            case "uninstall":
                ContentFrame.Navigate(typeof(UninstallPage));
                return;
            case "startup":
                ContentFrame.Navigate(typeof(StartupPage));
                return;
            case "sysfeatures":
                // 系统功能（自上游 dv1.2.0-insider1.8 移植）：HKLM / powercfg 设置，排队后一次提权写入。
                ContentFrame.Navigate(typeof(SystemFeaturesPage));
                return;
            case "systemtweaks":
                // 系统优化（自上游 dv1.2.0-insider1.8 移植）：HKCU 直写，整机组策略键走一次 reg import 提权。
                ContentFrame.Navigate(typeof(SystemTweaksPage));
                return;
            case "pagefile":
                // 虚拟内存（自上游 dv1.2.0-insider1.8 移植）：PagingFiles 在 HKLM，提权助手写 MULTI_SZ，重启后生效。
                ContentFrame.Navigate(typeof(PageFilePage));
                return;
            case "ppttouch":
                // PowerPoint 触控优化（自上游 dv1.2.0-insider1.9 移植，2026-11-01；dv1.2.0 正式版转正进系统工具）
                ContentFrame.Navigate(typeof(PptTouchPage));
                return;
            case "ghostfile":
                // 幽灵文件清理（自上游 dv1.2.0-insider1.10 移植，2026-11-03；dv1.2.0 正式版转正进系统工具）
                ContentFrame.Navigate(typeof(GhostFilePage));
                return;
            case "docs":
                // 文档库列表页（自上游 dv1.2.0-insider1.10 移植）：正文页走 DocArticlePage，不在导航里出现。
                ContentFrame.Navigate(typeof(DocLibraryPage));
                return;
            case "dictionary":
                // 查阅单词（自上游 dv1.2.0-insider1.8 移植，需求清单第 17 条）：纯原生页，
                // 词源调度在 Services/Dictionary 里，不经 ToolCarrier —— 它不是可替换的网页小工具。
                ContentFrame.Navigate(typeof(DictionaryPage));
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
            Core.AppLog.Error("crash", $"页面导航失败｜目标={tag}｜异常={ex}");
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
        // 每次重钉都先作废上一条「暂缓高亮」：走到下面子项分支且不内联时会重新记上。
        _pendingChildTag = null;

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

                // ⚠️ 2026-11-06：子项**只有在内联铺在面板上时**才能展开分组、才能塞进 SelectedItem。
                //    窗口还没测量完（显示模式判定还停在 Minimal）、或者面板是图标窄条时，FA 2.4.1 把
                //    分组子项装在 AttachedFlyout 的 Popup 里：这一下 <c>IsExpanded = true</c> 会把浮层
                //    打开、子项容器在 Popup 里被 realize，随后浮层被关掉／子项搬回内联；
                //    Popup 里那个容器早已脱离可视树，而 ViewportManager 还拿它当滚动锚点 ——
                //    下一次渲染就抛
                //      InvalidOperationException: An anchor control must be a visual descendent of the
                //      ScrollContentPresenter.
                //    整个进程当场退出（只在渲染回调里，托管栈拦不住）。实测：起始页取任何「折叠分组里的
                //    子项」（--page=pagefile / ppttouch / ghostfile / machinecheck / pick-number）100% 复现，
                //    起始页是一级页时正常。
                //    处置：面板没内联就整段不做 —— 只记下待钉的 tag，等 DisplayModeChanged 变成 Expanded
                //    之后再展开 + 钉高亮（构造函数里挂的那一发）。
                if (!NavChildrenInline)
                {
                    _pendingChildTag = tag;
                    return;
                }

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
                        // 这一发是延后执行的，期间面板可能又切回不内联（窗口被拖窄）——
                        // 那种情况下再设 SelectedItem 就是上面说的锚点崩溃，直接放弃这次补钉。
                        if (!NavChildrenInline) return;
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
    /// 分组子项此刻是否**内联铺在导航面板上**（可以安全地展开分组、当 <c>SelectedItem</c> 用）。
    ///
    /// <para>判据只认 <c>DisplayMode == Expanded</c>。⚠️ 不要拿 <c>IsPaneOpen</c> 当并集补进来：
    /// 窗口首次测量之前是 Minimal 且 <c>IsPaneOpen=true</c>，那时候子项其实还挂在 AttachedFlyout 的
    /// Popup 里，按"面板开着"放行就会踩到 <see cref="SelectTag"/> 里记的那个锚点崩溃（实测）。</para>
    /// </summary>
    private bool NavChildrenInline => Nav.DisplayMode == NavigationViewDisplayMode.Expanded;

    /// <summary>
    /// 因面板不内联而**暂缓**的起始页高亮（见 <see cref="SelectTag"/> 里的实测说明）。
    /// 非 null = 有子项等着内联之后再钉；<see cref="NavChildrenInline"/> 一旦成立就由
    /// DisplayModeChanged 补钉并清空。
    /// </summary>
    private string? _pendingChildTag;
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

        // 点的是分组子项 → 父分组的子项浮层必须主动关掉，否则它会一直挂着，
        // 并把用户离开导航后的第一次点击吃掉。
        // 详见 CloseGroupFlyout 的注释。
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

    /// <summary>选中一条结果：软件进详情页，文章进正文页，工具 / 页面走导航 tag。</summary>
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

        // 文档库（2026-11-05，自上游 1.10 移植）：文章不是导航项，直接进正文页。
        if (hit.Kind == Data.SearchHitKind.Doc)
        {
            NavigateToDocArticle(hit.Key);
            return;
        }

        if (hit.Target.Length > 0) NavigateTo(hit.Target);
    }

    /// <summary>
    /// 进「文档库」里的一篇文章（列表点击、全局搜索、文章内 <c>csh://article/…</c> 链接共用）。
    /// 导航高亮停在「文档库」—— 正文页属于这一区，导航里不给每篇文章单独建项。
    /// </summary>
    public void NavigateToDocArticle(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        try
        {
            SelectTag("docs");
            ContentFrame.Navigate(typeof(DocArticlePage), id);
        }
        catch (Exception ex)
        {
            AppLog.Warn("shell", "打开文档失败：" + ex.Message);
        }
    }
}
