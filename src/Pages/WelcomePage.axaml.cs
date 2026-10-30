using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Updating;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 首页：大标题 → 版本图 + 版本号 → 快速开始卡片 → 收录情况 → 快捷入口 → 硬件/系统信息。
/// 全部原生，不加载网页。
///
/// ⚠️ 移植说明：WinUI 的 <c>Page</c> → <see cref="PageBase"/>（UserControl）；
///    原版 <c>Loaded</c> 事件、<c>InitializeComponent</c> 语义保持一致。
/// </summary>
public sealed partial class WelcomePage : PageBase
{
    public WelcomePage()
    {
        InitializeComponent();

        // ⚠️ 为什么要在 Loaded 里订阅 App.Content.Changed：安装包不自带内容包之后，软件清单是
        //    **联网才到**的 —— 首启时首页先空着，内容同步完不会自己通知这个页面。
        //    先退订再订阅：本页被 Frame 缓存成常驻实例，Loaded 会反复触发，这样写保证只挂一次。
        // ⚠️ 与 Populate 分开调：更新状态卡要在**每次进首页**时都复核一遍（Frame 缓存了页面实例，
        //    回到首页只是重新挂上可视树 → Avalonia 会再次触发 Loaded）。
        Loaded += (_, _) =>
        {
            App.Content.Changed -= OnContentChanged;
            App.Content.Changed += OnContentChanged;
            Populate();
            RefreshUpdateCard();

            // 「发现新版本」态：MainWindow 后台检查拿到结果放进 UpdateNotice，
            // 本页订阅它的变化重刷更新卡 —— 否则用户停在首页时卡片不会即时变。
            Services.Updating.UpdateNotice.Changed -= OnUpdateNoticeChanged;
            Services.Updating.UpdateNotice.Changed += OnUpdateNoticeChanged;
        };
    }

    /// <summary>导航离开 → 停掉推荐条定时器。Frame 会缓存页面实例，不停它就会在别的页面上继续 60fps 重画。</summary>
    public override void OnNavigatedFrom() => StopRecommendScroll();

    /// <summary>内容清单变了（同步完成 / 手动刷新）→ 把「已收录 XX 款软件」那行刷一下。</summary>
    private void OnContentChanged()
    {
        // ContentStore 的同步跑在后台线程 ⇒ 回 UI 线程再动控件
        if (!Dispatcher.UIThread.CheckAccess())
        {
            _ = Dispatcher.UIThread.InvokeAsync(OnContentChanged);
            return;
        }

        RefreshCollectedCount();
    }

    /// <summary>UpdateNotice 变了（后台检查发现新版本 / 清掉）→ 回 UI 线程重刷更新卡。</summary>
    private void OnUpdateNoticeChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshUpdateCard();
            return;
        }

        _ = Dispatcher.UIThread.InvokeAsync(RefreshUpdateCard);
    }

    // ── 更新状态（2026-10-06 起，挂在快速开始区的「查看更新内容」主题色卡上） ──

    /// <summary>
    /// 「查看更新内容」卡的两种状态：
    /// ①「待装」——后台下载完成、存档里有待装标记，且包还在、tag 还不是当前版本 → 右端换出「立即安装」按钮；
    /// ②「已是最新」——其余所有情况 → 副标题写当前版本号 + 已是最新版本，点击进「更新日志」页。
    ///
    /// 之所以不做「发现新版本」这一态：那需要在首页发起网络检查，会拖慢首屏。
    /// 后台本来就会自动检查并下载，真下了包自然落到状态①，够用了。
    ///
    /// 待装标记的自检清理逻辑与旧 InfoBar 完全一致：装上新版本再进来、或包被清理了 → 顺手清档，不摆死卡片。
    /// </summary>
    /// <summary>
    /// 有更新时把「查看更新内容」卡片换成**绿色横幅**（与 InfoBar Success 同套配色）；
    /// 无更新时恢复原来的强调色卡片 —— 它仍是「查看更新内容」的入口。
    /// 2026-10-22 上游 1.4：Nick 说「立即更新的标志很不明显，有更新的话用这个横幅」。
    /// </summary>
    private void ApplyUpdateHighlight(bool highlight)
    {
        try
        {
            Avalonia.Media.IBrush Fill(string key) =>
                Application.Current?.Resources[key] as Avalonia.Media.IBrush ?? Avalonia.Media.Brushes.Transparent;

            if (highlight)
            {
                ChangelogCard.Background = Fill("SystemFillColorSuccessBackgroundBrush");
                ChangelogIcon.Glyph = "\uE930";                       // 对勾
                ChangelogIcon.Foreground = Fill("TextFillColorPrimaryBrush");
                ChangelogCardTitle.Foreground = Fill("TextFillColorPrimaryBrush");
                ChangelogCardSubtitle.Foreground = Fill("TextFillColorPrimaryBrush");
                ChangelogChevron.Foreground = Fill("TextFillColorPrimaryBrush");
            }
            else
            {
                ChangelogCard.Background = Fill("AccentFillColorDefaultBrush");
                ChangelogIcon.Glyph = "\uE81C";                       // 时钟
                ChangelogIcon.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
                ChangelogCardTitle.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
                ChangelogCardSubtitle.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
                ChangelogChevron.Foreground = Fill("TextOnAccentFillColorPrimaryBrush");
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[welcome] 更新高亮失败: " + ex.Message);
        }
    }

    private void RefreshUpdateCard()
    {
        var s = App.Settings.Current;
        var path = s.UpdatePendingPath ?? "";
        var pending = false;

        if (path.Length > 0)
        {
            // 标记悬空（包被清了 / 档是手抄的），或待装 tag == 当前版本＝已经装上了 → 待装周期结束
            if (!System.IO.File.Exists(path)
                || s.UpdatePendingTag == ShellConfig.VersionPrefix + ShellConfig.ShellVersion)
            {
                s.UpdatePendingPath = "";
                s.UpdatePendingTag = "";
                s.UpdatePendingMd5 = "";
                s.UpdatePendingSha256 = "";
                App.Settings.Save();
            }
            else
            {
                pending = true;
            }
        }

        // ⚠️ 有更新就整张卡换成绿色横幅（上游 1.4）
        ApplyUpdateHighlight(pending || Services.Updating.UpdateNotice.Pending is not null);

        // 目标元素＝快速开始区那张「查看更新内容」主题色卡。
        // 注意：卡片本身就是强调色底 ⇒ 不再靠 BorderBrush 表达"待装"，改用右端「立即安装」按钮。
        if (pending)
        {
            var tag = s.UpdatePendingTag.Length > 0 ? s.UpdatePendingTag : "新版本";
            ChangelogCardTitle.Text = $"{tag} 已下载就绪";
            ChangelogCardSubtitle.Text = "安装包已通过校验，可立即安装";
            UpdateInstallButton.IsVisible = true;
            UpdateNowButton.IsVisible = false;
            ChangelogChevron.IsVisible = false;
            return;
        }

        // 只是**查到**了新版本、包还没下（2026-10-18 自上游 1.2.0 移植）：
        // 首页给一颗「立即更新」，不用让用户自己去设置页找。待装态优先级更高（上面已 return）。
        var noticed = Services.Updating.UpdateNotice.Pending;
        if (noticed is not null)
        {
            ChangelogCardTitle.Text = $"发现新版本 {noticed.Tag}";
            ChangelogCardSubtitle.Text = "可立即更新，或稍后在设置里下载";
            UpdateInstallButton.IsVisible = false;
            UpdateNowButton.IsVisible = true;
            ChangelogChevron.IsVisible = false;
            return;
        }

        ChangelogCardTitle.Text = "查看更新内容";
        ChangelogCardSubtitle.Text =
            $"当前版本 {ShellConfig.VersionPrefix}{ShellConfig.ShellVersion} · 已是最新版本";
        UpdateInstallButton.IsVisible = false;
        UpdateNowButton.IsVisible = false;
        ChangelogChevron.IsVisible = true;
    }

    /// <summary>「立即更新」：记着的那条可用更新 → 交给更新流程（发起检查/下载）。</summary>
    private async void UpdateNow_Click(object? sender, RoutedEventArgs e)
    {
        var release = Services.Updating.UpdateNotice.Pending;
        if (release is null) return;

        try
        {
            await Services.Updating.UpdateFlow.AskAsync(release, TopLevel.GetTopLevel(this));
            RefreshUpdateCard();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("update", ex, "首页「立即更新」");
        }
    }

    /// <summary>兼容旧入口：外部（后台下载完成回调）原来调的是「刷新待装横幅」，现在同一套逻辑。</summary>
    internal void RefreshUpdateReadyBar() => RefreshUpdateCard();

    /// <summary>点「查看更新内容」卡 → 去「更新日志」页（待装态由「立即安装」按钮接管，这里让开）。</summary>
    private void ChangelogCard_Tapped(object? sender, TappedEventArgs e)
    {
        if (UpdateInstallButton.IsVisible) return;
        App.MainWindow?.Shell.NavigateTo("changelog");
    }

    private async void InstallPending_Click(object? sender, RoutedEventArgs e)
    {
        // 防连点：安装会退出应用，多点只会并发起安装器
        if (sender is Button b) b.IsEnabled = false;
        await UpdateFlow.InstallPendingNowAsync();
    }

    private void Populate()
    {
        // ⚠️ 首页大标题下面那行站点名副标题（原 HomeTitleText）已按 Nick 2026-10-06 要求整行删除，
        //    连带 ui.AppTitle / title 取值一起清掉 —— 别再补回来。
        // 只显示软件自己的版本（前缀 VersionPrefix + ShellVersion，如正式版 dv1.1.1 或预览版 dv1.1.1-insider1.0）。
        // ⚠️ 别再往这里挂"网站版本"——软件是独立发布物，不摆成网站版本的附属品（2026-09-26 删）。
        ShellVersionText.Text = ShellConfig.VersionPrefix + ShellConfig.ShellVersion;

        RefreshCollectedCount();

        LoadBanner();
        BuildQuickInfo();
        BuildQuickLinks();
        BuildRecommendations();
    }

    // ══════════════════════════════════════════════════════════════════
    //  推荐应用（2026-10-18 自上游 1.2.0 移植：Store「精选实用软件」样式）
    //
    //  · 一排整卡缓慢向左匀速滚动、**无缝循环**：铺两组首尾相接，位移一整组宽度后画面完全重合；
    //  · 鼠标悬停暂停、移开继续；「换一批」换个种子重挑；
    //  · 点卡片进详情页。
    //  ⚠️ 移植说明：WinUI 用 Storyboard + RepeatBehavior.Forever；Avalonia 的动画也能循环，
    //     但「悬停暂停」要能停在任意位置再继续，用定时器推进位移最直接、行为也最好理解。
    // ══════════════════════════════════════════════════════════════════

    private const double RecommendCardW = 128;      // 整卡宽度（向 Store 靠）
    private const double RecommendGap = 12;         // 卡片间距
    private const double RecommendSpeed = 24;       // 滚动速度（px/秒），宁慢勿快
    private const double RecommendTopH = 128;       // 上半部分（图标区）高度
    private const double RecommendIconSize = 64;    // 中央清晰图标边长（约卡片一半）
    private const double RecommendCardRadius = 8;   // 整卡圆角
    private const double RecommendIconRadius = 12;  // 图标圆角

    /// <summary>
    /// 兜底色板：图标还没取到 / 没有图标地址时先用它（按 id 稳定散列，同一软件永远同色）。
    /// </summary>
    private static readonly Color[] RecommendTileColors =
    {
        Color.FromArgb(255, 0x1D, 0x9E, 0x75), // 青绿
        Color.FromArgb(255, 0x37, 0x8A, 0xDD), // 蓝
        Color.FromArgb(255, 0xD4, 0x53, 0x7E), // 玫红
        Color.FromArgb(255, 0xE2, 0x4B, 0x4A), // 红
        Color.FromArgb(255, 0x7F, 0x77, 0xDD), // 紫
        Color.FromArgb(255, 0xBA, 0x75, 0x17), // 琥珀
        Color.FromArgb(255, 0x18, 0x5F, 0xA5), // 深蓝
        Color.FromArgb(255, 0x5F, 0x5E, 0x5A), // 灰
    };

    private int _recommendSeed = Random.Shared.Next();
    private List<SoftwareApp> _recommendPicks = new();
    private double _recommendOffset;
    private double _recommendDistance;
    private DispatcherTimer? _recommendTimer;

    private void BuildRecommendations()
    {
        var apps = App.Content.Apps;

        StopRecommendScroll();

        _recommendPicks = apps.Count == 0
            ? new List<SoftwareApp>()
            : PickRecommendations(apps, 12, _recommendSeed);

        if (_recommendPicks.Count == 0)
        {
            RecommendSection.IsVisible = false;
            RecommendStrip.Children.Clear();
            return;
        }

        RecommendSection.IsVisible = true;

        // 面板要等一帧才拿到可用宽度 ⇒ 下一帧再决定每组几张、再铺卡片
        Dispatcher.UIThread.Post(LayoutRecommend, DispatcherPriority.Loaded);

        RecommendViewport.SizeChanged -= RecommendViewport_SizeChanged;
        RecommendViewport.SizeChanged += RecommendViewport_SizeChanged;
    }

    private void RecommendViewport_SizeChanged(object? sender, SizeChangedEventArgs e) => LayoutRecommend();

    /// <summary>
    /// 按视口宽度铺卡片：一组 = 挑中的软件循环铺满视口（至少 picks.Count 张），
    /// 再**复制一组**首尾相接 ⇒ 位移一整组宽度后画面完全重合，做到无缝循环。
    /// </summary>
    private void LayoutRecommend()
    {
        if (_recommendPicks.Count == 0) return;

        var viewport = RecommendViewport.Bounds.Width;
        if (viewport <= 0) return;   // 还没布局出来，等 SizeChanged 再铺

        var unit = RecommendCardW + RecommendGap;
        var perGroup = Math.Max(_recommendPicks.Count,
            (int)Math.Ceiling((viewport + RecommendGap) / unit));

        var group = new List<SoftwareApp>(perGroup);
        for (var i = 0; i < perGroup; i++) group.Add(_recommendPicks[i % _recommendPicks.Count]);

        // 视口裁剪：超出部分藏起来（Avalonia 里 Clip 是几何对象，跟 WinUI 一样）
        RecommendViewport.Clip = new RectangleGeometry(new Rect(0, 0, viewport, RecommendTopH + 60));

        RecommendStrip.Children.Clear();

        // 两组首尾相接：位移一组宽度即无缝回到起点
        for (var copy = 0; copy < 2; copy++)
            foreach (var app in group)
                RecommendStrip.Children.Add(BuildRecommendCard(app));

        _recommendDistance = group.Count * unit;     // 一整组的位移距离
        _recommendOffset = 0;
        ((TranslateTransform)RecommendStrip.RenderTransform!).X = 0;
        StartRecommendScroll();
    }

    /// <summary>定时器推进位移：每秒 RecommendSpeed 像素，越过一整组宽度就回绕（无缝）。</summary>
    private void StartRecommendScroll()
    {
        StopRecommendScroll();
        if (_recommendDistance <= 0) return;

        // ⚠️ 这支定时器是**常驻 60fps 重画**（主窗可见时每 16ms 重画整窗）。两种情况必须让开：
        //    ① 侧边栏/浮窗正在滑（<see cref="MotionGate"/>）—— 抢帧实测会把滑动顶到 30~130ms 一帧；
        //    ② 主窗不在前台（侧边栏展开、切到别的程序、最小化）—— 没人看，白烧 CPU。
        //    只是跳过这一帧的位移，定时器不重建，恢复时 last 已是当前时刻，不会一下窜一大段。
        var host = TopLevel.GetTopLevel(this) as Window;
        var last = DateTime.UtcNow;
        _recommendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _recommendTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var delta = (now - last).TotalSeconds;
            last = now;
            if (_recommendPaused) return;
            if (MotionGate.Busy) return;
            if (host is not null && !host.IsActive) return;

            _recommendOffset += RecommendSpeed * delta;
            if (_recommendOffset >= _recommendDistance) _recommendOffset -= _recommendDistance;
            ((TranslateTransform)RecommendStrip.RenderTransform!).X = -_recommendOffset;
        };
        _recommendTimer.Start();
    }

    private void StopRecommendScroll()
    {
        _recommendTimer?.Stop();
        _recommendTimer = null;
    }

    private bool _recommendPaused;

    private void Recommend_PointerEntered(object? sender, PointerEventArgs e) => _recommendPaused = true;

    private void Recommend_PointerExited(object? sender, PointerEventArgs e) => _recommendPaused = false;

    private void Recommend_Reroll(object? sender, RoutedEventArgs e)
    {
        _recommendSeed = Random.Shared.Next();
        BuildRecommendations();
    }

    /// <summary>
    /// 一张推荐卡（照 Store 的画法）：**整卡圆角**，上半部分图标放大铺底 + 着色罩 + 中央圆角清晰图标；
    /// 下半部分**加粗名称 + 一句短简介**。
    /// ⛔ 不是把图标拉满整格。
    /// </summary>
    private Control BuildRecommendCard(SoftwareApp app)
    {
        var tint = RecommendTileColors[(int)(StableRank.Of(app.Id) % (uint)RecommendTileColors.Length)];

        var card = new Border
        {
            Width = RecommendCardW,
            CornerRadius = new CornerRadius(RecommendCardRadius),
            // ⚠️ 整卡底色用**主题卡片面**，不跟图标色走 —— 下半文字区是一块统一的深色面（Store 就是这样），
            //    图标色只留在上半的着色底里。主题画刷保证浅色主题下也不会白字配浅底。
            Background = (Services.ThemeBrush.Get(this, "CardBackgroundFillColorDefaultBrush") as IBrush)
                         ?? new SolidColorBrush(tint),
            BorderThickness = new Thickness(1),
            BorderBrush = Services.ThemeBrush.Get(this, "CardStrokeColorDefaultBrush") as IBrush,
        };

        // 上半：图标放大铺底 + 半透明主题色罩（替代 WinUI 的 AcrylicBrush 毛玻璃）+ 中央清晰图标
        var topGrid = new Grid();

        if (app.IconImage is { } backdrop)
            topGrid.Children.Add(new Image { Source = backdrop, Stretch = Stretch.UniformToFill });

        topGrid.Children.Add(new Border
        {
            Background = new SolidColorBrush(tint, 0.55),
            CornerRadius = new CornerRadius(RecommendCardRadius, RecommendCardRadius, 0, 0),
        });

        if (app.IconImage is { } crisp)
        {
            topGrid.Children.Add(new Border
            {
                Width = RecommendIconSize,
                Height = RecommendIconSize,
                CornerRadius = new CornerRadius(RecommendIconRadius),
                Background = new ImageBrush((IImageBrushSource)crisp)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }
        else
        {
            // 没有图标就先摆名字首字（图标加载成功后靠绑定收掉会很啰嗦，这里直接不摆 —— 反正会重铺）
            topGrid.Children.Add(new TextBlock
            {
                Text = app.AvatarText,
                FontSize = 26,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }

        var top = new Border
        {
            Height = RecommendTopH,
            CornerRadius = new CornerRadius(RecommendCardRadius, RecommendCardRadius, 0, 0),
            Background = new SolidColorBrush(tint),
            Child = topGrid,
        };

        // 下半：加粗名称 + 短简介
        var text = new StackPanel { Spacing = 3, Margin = new Thickness(10, 9, 10, 10) };
        text.Children.Add(new TextBlock
        {
            Text = app.Name,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Services.ThemeBrush.Get(this, "TextFillColorPrimaryBrush") as IBrush,
        });
        text.Children.Add(new TextBlock
        {
            Text = app.IntroText,
            FontSize = 11,
            Height = 30,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Services.ThemeBrush.Get(this, "TextFillColorSecondaryBrush") as IBrush,
        });

        var content = new StackPanel();
        content.Children.Add(top);
        content.Children.Add(text);
        card.Child = content;

        card.Tapped += (_, _) =>
        {
            var frame = this.FindAncestorOfType<Frame>() ?? App.MainWindow?.Shell.ContentFrame;
            frame?.Navigate(typeof(DetailPage), app.Id);
        };
        ToolTip.SetTip(card, app.Name);

        return card;
    }

    /// <summary>
    /// 挑推荐：**用 Id 的稳定哈希**排序，同一个种子每次挑出来的一样、换种子才换一批。
    /// 优先"有图标 + 简介"的，免得一排全是灰块；不够就用其余软件补齐。
    /// </summary>
    private static List<SoftwareApp> PickRecommendations(List<SoftwareApp> apps, int count, int seed)
    {
        return apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .OrderBy(a => StableRank.Of(a.Id + "#" + seed))
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// 首页那条「收录情况」InfoBar（<see cref="CollectedBar"/>）：标题＝已收录 N 款软件，
    /// 正文＝今日新增 N 款＋具体名字。2026-10-07 Nick 要求**两项合并**到这一条，放在设置卡片正下方。
    ///
    /// 数字来自**本机已同步的**软件清单（<c>App.Content.Apps</c>，源头是站点仓库的 软件数据/apps/）——
    /// 零网络请求、离线也准。
    ///
    /// 清单还没同步下来时（Apps 为空）**整条收起**，不显示"已收录 0 款"这种假数字。
    /// </summary>
    private void RefreshCollectedCount()
    {
        var count = App.Content.Apps.Count;

        if (count <= 0)
        {
            CollectedBar.IsOpen = false;       // 清单都还没到，更谈不上「新增」
            return;
        }

        CollectedBar.Title = $"已收录 {count} 款软件";
        CollectedBar.Message = string.Empty;   // 正文只留给「今日新增」，没有就空着
        CollectedBar.IsOpen = true;

        _ = RefreshTodayAddedAsync(count);
    }

    /// <summary>
    /// 补「今日新增」那半句：**具体是哪几款**（用中文名，不是 slug）。
    ///
    /// 数据得联网：软件 json 里没有任何日期字段，只能去问 GitHub 的提交历史
    /// （算法与代价见 <see cref="NewlyAddedService"/>）。
    /// **只增不补** —— 只有算出 &gt;0 才写正文；算不出来、或今天确实没新增，正文就保持空。
    /// ⛔ 不摆「今日新增 0 款」，免得那条 InfoBar 里多一个会骗人的数字。
    /// </summary>
    private async Task RefreshTodayAddedAsync(int collected)
    {
        // 口径改成「最近 48 小时」（2026-10-18 自上游 1.2.0 移植）：
        // 原来按"今天 0 点"算，一到凌晨就清零，跨午夜看不到昨天下午刚上架的软件。
        var added = await NewlyAddedService.GetRecentAsync(TimeSpan.FromHours(48));
        if (added is null || added.Count <= 0) return;

        // 名字从本机清单里取（slug → 中文名）；万一清单里找不到这一条（刚加还没来得及同步？）
        // 就退回 slug 本身，总比不显示强
        var names = added.Ids.Select(id => App.Content.FindById(id)?.Name ?? id).ToList();

        // await 回来不一定还在 UI 线程（服务内部一律 ConfigureAwait(false)）
        if (!Dispatcher.UIThread.CheckAccess())
        {
            _ = Dispatcher.UIThread.InvokeAsync(() => ApplyTodayAdded(collected, names));
            return;
        }

        ApplyTodayAdded(collected, names);
    }

    /// <summary>把名单写进 InfoBar 正文。名单太长只列前 6 个，后面补「等 N 款」。</summary>
    private void ApplyTodayAdded(int collected, IReadOnlyList<string> names)
    {
        // 这期间清单又变了（同步完成）→ 已经有人重刷过，别用旧数字盖回去
        if (App.Content.Apps.Count != collected) return;

        CollectedBar.Title = $"已收录 {collected} 款软件";
        CollectedBar.Message = "今日新增 " + names.Count + " 款：" + (names.Count <= 6
            ? string.Join("、", names)
            : string.Join("、", names.Take(6)) + $" 等 {names.Count} 款");
        CollectedBar.IsOpen = true;
    }

    /// <summary>一行快捷入口：项目仓库 / 作者主页 / 赞助作者 / 加入Q群 / 更新日志。</summary>
    private void BuildQuickLinks()
    {
        var links = QuickLinks.Build(App.Content.Ui);
        QuickGrid.ItemsSource = links;
        ApplyQuickLinkIcons(links);

        // 主题切换后按新主题色重新染 SVG（2026-10-18 自上游 1.2.0 移植）；
        // ⚠️ 只在页面活着的时候听，别让已离开的页面被事件勾住。
        ActualThemeVariantChanged -= WelcomePage_ThemeChanged;
        ActualThemeVariantChanged += WelcomePage_ThemeChanged;

        // 面板是随 ItemsSource 才生成的 ⇒ 等一帧再按实际宽度收格宽（SizeChanged 也会兜一次）
        Dispatcher.UIThread.Post(() => FitQuickRow(QuickGrid.Bounds.Width), DispatcherPriority.Loaded);
        QuickGrid.SizeChanged -= QuickGrid_SizeChanged;
        QuickGrid.SizeChanged += QuickGrid_SizeChanged;
    }

    private void QuickGrid_SizeChanged(object? sender, SizeChangedEventArgs e) => FitQuickRow(e.NewSize.Width);

    /// <summary>
    /// 快捷入口**必须排在一行**（上游 2026-10-17 Nick：窗口变窄会折行，很难看）。
    /// 做法是按可用宽度重算 <see cref="WrapPanel.ItemWidth"/>：宽屏维持 192 不动，
    /// 窄了就把五颗平分一行；只有窄到 104×5 以下才允许折行（那时确实放不下了）。
    /// </summary>
    private void FitQuickRow(double width)
    {
        if (QuickGrid.ItemsPanelRoot is not WrapPanel panel) return;

        var count = QuickGrid.ItemCount;
        if (count == 0 || width <= 0) return;

        // ⛔ 不能正好用 width / count：格宽乘回来会因浮点/取整差出零点几像素，
        //    第五颗就被挤到下一行（上游实测 900 宽的窗口仍然 4+1）。留 1px 余量。
        var itemWidth = Math.Clamp(Math.Floor(width / count) - 1d, 104d, 192d);
        if (Math.Abs(panel.ItemWidth - itemWidth) > 0.5) panel.ItemWidth = itemWidth;
    }

    private void WelcomePage_ThemeChanged(object? sender, EventArgs e)
    {
        if (QuickGrid.ItemsSource is IEnumerable<QuickLink> links) ApplyQuickLinkIcons(links);
    }

    /// <summary>
    /// 把每颗入口的 SVG 染成当前主题的前景色调。
    /// ⚠️ 颜色必须从**本页所在的树**里取（`ThemeBrush.Get(this, …)`）—— 从应用级资源取会在
    ///    "浅色界面 + 深色系统"时染成白字色，看不见（Nick 撞过这个坑）。
    /// </summary>
    private void ApplyQuickLinkIcons(IEnumerable<QuickLink> links)
    {
        var color = (Services.ThemeBrush.Get(this, "TextFillColorPrimaryBrush") as ISolidColorBrush)?.Color
                    ?? Colors.Black;
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.IconSvg)) continue;
            try { link.IconSource = Services.QuickLinkIconLoader.GetIcon(link.IconSvg!, color); }
            catch { /* 取不到就退回 FontIcon，不影响点击 */ }
        }
    }

    /// <summary>
    /// 点的路线跟原版 <c>Quick_ItemClick</c> 一致：
    /// 带 Tag 的是**应用内页面**（目前只有「更新日志」）：走导航栏那套，左侧高亮也会跟过去。
    /// </summary>
    private void QuickGrid_Tapped(object? sender, TappedEventArgs e)
    {
        // ItemsControl 冒泡上来的 Tapped：从命中的控件往上找它的数据上下文
        if ((e.Source as StyledElement)?.DataContext is not QuickLink link) return;

        if (link.Tag.Length > 0)
        {
            App.MainWindow?.Shell.NavigateTo(link.Tag);
            return;
        }

        if (link.Url.Length > 0)
            App.MainWindow?.OpenExternal(link.Url);
    }

    /// <summary>「硬件信息 / 系统信息」：一行一项的列表（图标 + 标签 + 值），不用卡片。</summary>
    private void BuildQuickInfo()
    {
        var q = SystemInfo.Gather();
        FillRows(HardwareList, q.Hardware);
        FillRows(SystemList, q.System);
    }

    private void FillRows(StackPanel panel, IReadOnlyList<SystemInfo.InfoLine> lines)
    {
        panel.Children.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            // ⚠️ 原版用 Page.Resources 里一个命名的 Border Style（InfoRowDivider）；
            //    Avalonia 的 Style 不能像 WinUI 那样直接赋给 Controls.Style 属性，
            //    这里按同样的外观（高 1px + 分隔线颜色）直接构造 Border。
            if (i > 0)
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = ThemeBrush.Get(this, "DividerStrokeColorDefaultBrush"),
                });
            panel.Children.Add(BuildRow(lines[i]));
        }
    }

    private static Grid BuildRow(SystemInfo.InfoLine line)
    {
        // ⚠️ Avalonia 的 Grid 没有 Padding / ColumnSpacing：
        //    原版 Padding="0,9,0,9" → 整行 Margin；原版 ColumnSpacing="12" → 第 1、2 列各加 12 的左 Margin。
        var row = new Grid { Margin = new Thickness(0, 9, 0, 9) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = RowGlyphs.TryGetValue(line.Label, out var glyph) ? glyph : "\uE946",
            FontSize = 14,
            Opacity = 0.75,
            Width = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = line.Label,
            FontSize = 13,
            Opacity = 0.65,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ⚠️ 原版 TextBlock.IsTextSelectionEnabled（WinUI 才有的属性）→ Avalonia 里能选中复制的文本控件是
        //    SelectableTextBlock，用它保留"值可选中复制"的语义。
        var value = new SelectableTextBlock
        {
            Text = line.Value,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(value, line.Value);

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(value);
        return row;
    }

    /// <summary>每行的图标（码位取自 Segoe Fluent Icons 官方名字表）。</summary>
    private static readonly Dictionary<string, string> RowGlyphs = new()
    {
        ["处理器"] = "\uEEA1",   // CPU
        ["内存"] = "\uEEA0",     // RAM
        ["硬盘"] = "\uEDA2",     // HardDrive
        ["触摸"] = "\uEDA4",     // Touchscreen
        ["显卡"] = "\uE7F4",     // TVMonitor
        ["显存"] = "\uE714",     // Video
        ["显示器"] = "\uE7F3",   // SettingsDisplaySound
        ["操作系统"] = "\uE770",  // System
        ["系统版本"] = "\uE946",  // Info
        ["安装日期"] = "\uE787",  // Calendar
        ["虚拟内存"] = "\uEDA2",  // HardDrive
        ["虚拟化"] = "\uEEA3",   // VirtualMachineGroup
        ["DirectX"] = "\uE7FC",  // Game
    };

    private void LoadBanner()
    {
        try
        {
            // 换 banner 时这里和 csproj 的 EmbeddedResource 一起改。
            // ⚠️ 缓存文件名也带上版本号：ExtractToCache 靠"长度不同"判过期，
            //    万一同尺寸换图会被判成没过期，带上版本号就不会串图。
            var path = EmbeddedAssets.ExtractToCache("dv1.2.png", "banner-dv1.2.png");
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                // ⚠️ WinUI 的 BitmapImage(new Uri(path)) → Avalonia 的 Bitmap(文件名)。
                Banner.Source = new Bitmap(path);
        }
        catch { /* 图片读不到就不显示 */ }
    }

    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        // ⚠️ 原版 sender 收窄成 FrameworkElement；Avalonia 对应 Control。
        var tag = (sender as Control)?.Tag as string;
        switch (tag)
        {
            case "web":
                // 「体验网页版」→ 交给系统默认浏览器打开整站
                // （应用内的 WebSheet 浮层留给软件详情页那种"顺手看一眼"的场景）
                App.MainWindow?.OpenExternal(ShellConfig.SiteUrl);
                break;
            case "apps":
                App.MainWindow?.Shell.NavigateTo("apps");
                break;
            case "tools":
                // 走专用入口：除了切到工具索引页，还要把导航里的分组展开
                //（否则从首页跳进来时，左侧「内置工具」是收着的，看不出里面还有子项）
                App.MainWindow?.Shell.NavigateToTools();
                break;
            case "sidebar":
                App.MainWindow?.Shell.NavigateTo("sidebar");
                break;
            case "experimental":
                // 走专用入口：除了切到总览页，还要把导航里的分组展开（从外面跳进来时看不出里面有子项）
                App.MainWindow?.Shell.NavigateToExperimental();
                break;
            case "submit":
                App.MainWindow?.Shell.NavigateTo("submit");
                break;
            case "feedback":
                App.MainWindow?.Shell.NavigateTo("feedback");
                break;
            case "settings":
                App.MainWindow?.Shell.NavigateTo("settings");
                break;
        }
    }
}
