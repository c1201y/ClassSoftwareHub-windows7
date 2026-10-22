using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>软件下载页：搜索 + 分类筛选 + 软件卡片墙（全原生，不加载任何网页）。</summary>
public sealed partial class SoftwarePage : PageBase
{
    private string _category = "";
    /// <summary>
    /// 关键词过滤链（2026-10-18 起**没有 UI 入口** —— 页内搜索框已删，顶部总搜索接手了）。
    /// 刻意保留字段与 <see cref="Apply"/> 里的过滤：以后若要把"筛列表"接回顶部搜索，
    /// 只需要给它赋值，不用再把整条链路重写一遍。当前恒为空串，行为等于不过滤。
    /// </summary>
    private string _keyword = "";
    private string _view = "tile";          // tile | grid
    private bool _loading;
    private readonly List<ToggleButton> _chips = new();

    /// <summary>Hub AI 浮层的位移动画载体（Avalonia 里 TranslateTransform 不能 x:Name，代码里建）。</summary>
    private TranslateTransform? _hubAiTransform;

    /// <summary>
    /// 上一次真正重建列表时的「状态指纹」（分类|关键词|视图|内容包版本|内容来源）。
    ///
    /// 为什么要有它：本页实例会被宿主 Frame 留下来（页面缓存），
    /// 但 <see cref="Apply"/> 每次都把 ItemsSource 换成新的 List —— 列表见到新集合
    /// 会把**所有卡片容器整个重建一遍**（含图标），那 200ms 左右的停顿又回来了，缓存等于白做。
    /// 指纹没变就什么都不做，"退出去再进来"就变成零成本。
    /// </summary>
    private string _appliedKey = "";

    public SoftwarePage()
    {
        InitializeComponent();

        // ⚠️ 原版构造函数里还有 Core.WheelRelay.Attach(this, TileGrid, CompactGrid)；
        //    本移植版把 ScrollViewer 提到了页面最外层，滚轮本来就由它统一接管，不需要再接力。
        _hubAiTransform = new TranslateTransform();
        HubAiPanel.RenderTransform = _hubAiTransform;
    }

    public override void OnNavigatedTo(object? parameter)
    {
        // ⛔ **只有带了分类参数才改分类**（2026-10-22）。
        //    以前无条件 `_category = parameter as string ?? ""`，于是从「系统工具」点进某个软件、
        //    再返回时参数是 null ⇒ 分类被清成「全部」，用户得重新点一遍（Nick 反馈的核心问题）。
        //    现在：带参数 = 用户主动切分类；不带参数（返回/从导航进来）= **保持上次选择**。
        if (parameter is string requestedCategory && requestedCategory.Length > 0)
            _category = requestedCategory;

        // 总闸关掉时入口卡整体隐藏（Nick 2026-10-18 定：不占位、不留灰块）。
        // ⚠️ 必须每次导航都重读一次 —— 本页实例长驻，用户去设置里开关一次再回来，
        //    页面不会重建，只在构造时读一次会一直是旧状态。
        HubAiEntry.IsVisible = App.Settings.Current.HubAiEnabled;

        _view = App.Settings.Current.AppCardView == "grid" ? "grid" : "tile";
        _loading = true;
        ViewPicker.SelectedIndex = _view == "grid" ? 1 : 0;
        _loading = false;
        UpdateView();

        // 内容同步好之后自己要刷新 —— 安装包不自带清单，首启就停在"正在获取"这一屏上，
        // 用户多半不会为了看到列表专门切一次页（上游 2026-10-05）。
        // ⚠️ 页面实例会被框架缓存（长驻），先 -= 再 += 保证只挂一次。
        App.Content.Changed -= OnContentChanged;
        App.Content.Changed += OnContentChanged;

        if (Fingerprint() == _appliedKey) return;   // 状态没变 → 保留现有卡片，别重建

        BuildChips();
        Apply();
    }

    public override void OnNavigatedFrom()
    {
        App.Content.Changed -= OnContentChanged;
        base.OnNavigatedFrom();
    }

    /// <summary>内容变了（同步完成 / 同步状态变化）：重建列表，或只刷一下空清单那几句提示。</summary>
    private void OnContentChanged()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(OnContentChanged);
            return;
        }

        if (Fingerprint() != _appliedKey)
        {
            BuildChips();
            Apply();
            return;
        }

        // 指纹没变 = 内容本身没换，只是"正在同步"的开关翻了页 → 不必重建卡片，
        // 否则每次同步开始/结束都要付一次列表重建的代价。
        if (App.Content.Apps.Count == 0) UpdateEmptyState(0);
    }

    /// <summary>
    /// 当前"页面状态"的指纹；任一要素变了才值得重建列表与分类条。
    ///
    /// ⚠️ 内容那一位必须用 <see cref="Core.ContentStore.Revision"/>（每次 Load 都涨），
    /// **不能**用 ContentVersion / Source（一个恒为空、一个恒为同一路径，索引不到"清单被补全了"，
    /// 首启同步完页面就不会自己刷新 —— 2026-10-05 实测踩到）。
    /// </summary>
    private string Fingerprint()
        => string.Join("\u0001", _category, _keyword, _view,
                       App.Content.Revision.ToString(), App.Content.Source);

    /// <summary>磁贴（3 列，带简介）/ 网格（5 列，紧凑）切换，选择会记进设置。</summary>
    private void ViewPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ViewPicker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        _view = tag == "grid" ? "grid" : "tile";
        App.Settings.Current.AppCardView = _view;
        App.Settings.Save();
        UpdateView();
    }

    private void UpdateView()
    {
        var tile = _view != "grid";
        // ⚠️ Avalonia 没有 Visibility 枚举 → 用 bool IsVisible。
        TileGrid.IsVisible = tile;
        CompactGrid.IsVisible = !tile;
    }

    /// <summary>
    /// 分类条。
    /// ⚠️ 上游 1.3 换成系统 <see cref="SelectorBar"/>；FluentAvalonia 2.4.1 **没有这个控件**，
    ///    故沿用本移植版已有的 ToggleButton chip 条（选中态 / 计数 / 横向滚动都已实现，语义等价）。
    /// </summary>
    private void BuildChips()
    {
        Chips.Children.Clear();
        _chips.Clear();

        _loading = true;
        try
        {
            AddChip($"全部 {App.Content.Apps.Count}", "");

            foreach (var category in App.Content.Categories)
                AddChip($"{category.Name} {App.Content.CountInCategory(category.Key)}", category.Key);
        }
        finally
        {
            // 回填时不触发 Apply（否则每次切页都会重建一遍列表）
            _loading = false;
        }
    }

    private void AddChip(string text, string key)
    {
        var chip = new ToggleButton
        {
            Content = text,
            Tag = key,
            IsChecked = key == _category,
        };
        chip.Click += Chip_Click;
        _chips.Add(chip);
        Chips.Children.Add(chip);
    }

    private void Chip_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ToggleButton chip || chip.Tag is not string key) return;

        _category = key;
        foreach (var other in _chips)
            if (!ReferenceEquals(other, chip)) other.IsChecked = false;
        chip.IsChecked = true;
        Apply();
    }

    private void Apply()
    {
        var list = App.Content.Apps
            .Where(a => _category.Length == 0 || a.Category == _category)
            .Where(a => a.Matches(_keyword))
            .ToList();

        TileGrid.ItemsSource = list;
        CompactGrid.ItemsSource = list;

        // 预热详情页色带要用的图标主色（2026-10-18 自上游 1.2.0 移植）：
        // 详情页不能"等页面画完再去取色"，否则会先显示一种颜色、过一会儿跳成另一种。
        WarmPalette(list);

        TitleText.Text = _category.Length == 0
            ? (_keyword.Length == 0 ? "软件下载" : $"搜索：{_keyword}")
            : App.Content.CategoryName(_category);

        UpdateEmptyState(list.Count);

        _appliedKey = Fingerprint();   // 记下这次是按什么状态建的，下次同状态直接跳过
    }

    /// <summary>
    /// 预热详情页色带要用的图标主色。
    /// ⛔⛔ 详情页不能"等页面画完再去取色"—— 那必然是先显示一种颜色、过一会儿跳成另一种。
    /// 列表这里提前把前几十个算好（<c>IconPalette.Preload</c> 内部有缓存与去重，重复调用不重复下载）。
    /// 只做首屏那几十个：再往后滚到的软件，用户点进去时图标早就加载过了，那时再算也来得及。
    /// </summary>
    private static void WarmPalette(List<SoftwareApp> list)
    {
        const int limit = 32;

        for (var i = 0; i < list.Count && i < limit; i++)
            Services.IconPalette.Preload(list[i].Icon);
    }

    /// <summary>
    /// 空清单时别只写一句"没有匹配的软件"——要分清三种情况：
    /// ① 搜索/分类筛掉了 ② 正常：安装包不自带清单，正在联网取 ③ 异常：真的没取到
    /// </summary>
    private void UpdateEmptyState(int shown)
    {
        if (shown > 0)
        {
            EmptyPanel.IsVisible = false;
            return;
        }

        EmptyPanel.IsVisible = true;

        EmptyDetail.Text = $"当前内容来源：{App.Content.SourceLabel}" +
                           (App.Content.Issues.Count > 0 ? $"\n读取问题：{App.Content.Issues[0].Message}" : "");

        var hasFilter = _keyword.Length > 0 || _category.Length > 0;
        if (hasFilter)
        {
            EmptyTitle.Text = "未找到匹配的软件";
            EmptyText.Text = "请更换关键词，或单击「全部」查看所有软件。";
            EmptyText.IsVisible = true;
            EmptyDetail.Text = $"当前内容来源：{App.Content.SourceLabel}（共 {App.Content.Apps.Count} 个软件）";
            EmptyRetry.IsVisible = false;
            return;
        }

        EmptyRetry.IsVisible = true;
        EmptyRetry.IsEnabled = true;
        EmptyRetry.Content = "重新获取清单";

        // 正在联网取 —— 这是最正常的首启状态（安装包不再内置清单），别吓唬用户
        if (App.Content.IsSyncing)
        {
            EmptyTitle.Text = "正在获取软件清单";
            EmptyText.Text = "正在从网络获取最新的软件清单，请稍候。";
            EmptyRetry.IsEnabled = false;
            EmptyRetry.Content = "正在获取";
            return;
        }

        EmptyTitle.Text = "软件清单为空";
        EmptyText.Text = "软件清单不随安装包提供，需要联网获取。" +
                         "若始终为空，多半是当前网络连不上（教学机、校园网常见），换个网络再点下面的按钮。";
    }

    /// <summary>空状态里的「重新获取清单」：拉一次网络内容再重读（失败就照实说）。</summary>
    private async void EmptyRetry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        EmptyRetry.IsEnabled = false;
        EmptyRetry.Content = "正在获取";
        EmptyTitle.Text = "正在获取软件清单";
        EmptyText.Text = "正在从网络获取最新清单。";
        EmptyDetail.Text = "";

        try
        {
            var result = await Services.ContentUpdater.SyncAsync(
                new Progress<string>(text => EmptyDetail.Text = text));

            if (result.Updated) App.Content.Load();

            BuildChips();
            Apply();

            if (App.Content.Apps.Count == 0)
            {
                EmptyTitle.Text = "仍未获取到清单";
                EmptyText.Text = "网络不可用，或当前网络访问获取源不通。可以换个网络再试；" +
                                 "如问题持续，请将本页截图提供给维护人员。";
                EmptyDetail.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            EmptyTitle.Text = "获取失败";
            EmptyText.Text = "网络不可用或暂时无法访问，请稍后重试。";
            EmptyDetail.Text = ex.Message;
        }
        finally
        {
            EmptyRetry.IsEnabled = true;
            EmptyRetry.Content = "重新获取清单";
        }
    }

    /// <summary>
    /// 卡片单击 → 进软件详情页。
    /// ⚠️ 原版是 <c>Frame.Navigate</c>（WinUI Page.Frame 自带宿主 Frame）；
    ///    Avalonia 的页面是 UserControl，没有 Frame 属性 → 沿可视树向上找宿主 <see cref="Frame"/>。
    /// </summary>
    private void AppGrid_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is SoftwareApp app)
            this.FindAncestorOfType<Frame>()?.Navigate(typeof(DetailPage), app.Id);
    }

    // 卡片自己的悬停反馈由 XAML 里 Class="card" 的 :pointerover 样式给出（见页面顶部注释）。

    // ══════════════════════════════════════════════════════
    //  Hub AI 入口与浮层（2026-10-18，1:1 对齐上游 1.3）
    //  ⚠️ 浮层**不做对话**：它只是"提问入口"，发送后把问题交给 HubAiPage，浮层收起。
    //     HubAiPage 历史、工具调用、推荐卡片与用量统计都在完整页里。
    // ══════════════════════════════════════════════════════

    private void HubAiEntry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowHubAiPanel(true);

    private void HubAiClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowHubAiPanel(false);

    /// <summary>
    /// 浮层升到完整页（2026-10-18）。走 <c>Shell.NavigateTo</c> 而不是 <c>Frame.Navigate</c> ——
    /// 后者只会换掉内容区，导航栏的选中态还停在「软件下载」，看着像点错了。
    /// ⚠️ 导航 tag <c>hubai</c> 的接线在 ShellPage（Lead 负责），这里只按 tag 走。
    /// </summary>
    private void HubAiExpand_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowHubAiPanel(false);
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    /// <summary>
    /// 浮窗显隐（2026-10-18 加动画）。
    /// ⚠️ 原来是瞬间 IsVisible 切换，弹出来很生硬（Nick：「有点生硬，能不能滑入滑出」）。
    /// 现在：展开 = 从下往上滑 24px + 淡入；收起 = 反向，动画结束后才真正隐藏
    /// （过早设 IsVisible=false 会让动画直接消失）。
    /// </summary>
    private void ShowHubAiPanel(bool show)
    {
        if (show)
        {
            HubAiPanel.IsVisible = true;

            // ⚠️ 必须先摆到起点：动画只给 To 时会从**当前值**动画过去，
            //    不设起点就是 0→0，只剩淡入、看不出滑动。
            if (_hubAiTransform is not null) _hubAiTransform.Y = 24;
            HubAiPanel.Opacity = 0;

            AnimatePanel(fromY: 24, toY: 0, fromOpacity: 0, toOpacity: 1, durationMs: 240, onDone: null);
            HubAiInput.Focus();
            return;
        }

        if (!HubAiPanel.IsVisible) return;

        AnimatePanel(fromY: 0, toY: 24, fromOpacity: HubAiPanel.Opacity, toOpacity: 0, durationMs: 170,
            onDone: () => HubAiPanel.IsVisible = false);
    }

    /// <summary>
    /// 滑入滑出。⚠️ WinUI 的 Storyboard + DoubleAnimation → Avalonia 的 Animation
    /// （FillMode=Forward 让终值留住；位移与透明度两条同时跑，跑完再回调）。
    /// </summary>
    private async void AnimatePanel(double fromY, double toY, double fromOpacity, double toOpacity,
                                    int durationMs, Action? onDone)
    {
        if (_hubAiTransform is null) return;

        var easing = toOpacity > 0.5 ? (Easing)new CubicEaseOut() : new CubicEaseIn();

        var slide = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(TranslateTransform.YProperty, fromY) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(TranslateTransform.YProperty, toY) } },
            },
        };

        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, fromOpacity) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, toOpacity) } },
            },
        };

        try
        {
            await Task.WhenAll(slide.RunAsync(_hubAiTransform), fade.RunAsync(HubAiPanel));
        }
        catch
        {
            // 动画起不来就直接落终值（与 MainWindow 的 WebSheet 动画同一套兜底）
        }

        // 动画跑完会回到基准值 → 把基准值写成终值，避免收起时闪回
        _hubAiTransform.Y = toY;
        HubAiPanel.Opacity = toOpacity;
        onDone?.Invoke();
    }

    private void HubAiSend_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SendHubAiMessage();

    private void HubAiInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SendHubAiMessage();
    }

    /// <summary>
    /// 浮层里发送 = 把问题交给完整页，不在浮层里做对话（2026-10-18）。
    /// 完整页有历史、工具调用、推荐卡片与用量统计，这里再实现一套必然两边行为不一致。
    /// </summary>
    private void SendHubAiMessage()
    {
        var text = HubAiInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        HubAiInput.Text = string.Empty;
        ShowHubAiPanel(false);

        HubAiPage.PendingQuestion = text;
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    /// <summary>
    /// 造一条消息气泡。背景走系统的 <c>SubtleFillColorSecondaryBrush</c> 而不是 App.axaml 里那套
    /// HubAi 资源 —— 代码里取主题字典的键很别扭（ThemeDictionaries 是延迟解析的），
    /// 而这条气泡本来就该跟系统"次级填充"一致，不必另立品牌色。
    /// </summary>
    private static Border MakeBubble(string text, bool alignRight)
    {
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            MaxWidth = alignRight ? 310 : 320,
            HorizontalAlignment = alignRight ? Avalonia.Layout.HorizontalAlignment.Right
                                             : Avalonia.Layout.HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
        };

        try
        {
            bubble.Background = Services.ThemeBrush.Get(bubble, "SubtleFillColorSecondaryBrush");
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("hubai", ex, "取气泡背景画刷");
        }

        return bubble;
    }
}
