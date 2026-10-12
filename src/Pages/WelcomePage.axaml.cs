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
using ClassSoftwareHub.Desktop.Core;
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
        };
    }

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
                App.Settings.Save();
            }
            else
            {
                pending = true;
            }
        }

        // 目标元素＝快速开始区那张「查看更新内容」主题色卡。
        // 注意：卡片本身就是强调色底 ⇒ 不再靠 BorderBrush 表达"待装"，改用右端「立即安装」按钮。
        if (pending)
        {
            var tag = s.UpdatePendingTag.Length > 0 ? s.UpdatePendingTag : "新版本";
            ChangelogCardTitle.Text = $"{tag} 已下载就绪";
            ChangelogCardSubtitle.Text = "安装包已通过校验，可立即安装";
            UpdateInstallButton.IsVisible = true;
            ChangelogChevron.IsVisible = false;
        }
        else
        {
            ChangelogCardTitle.Text = "查看更新内容";
            ChangelogCardSubtitle.Text =
                $"当前版本 {ShellConfig.VersionPrefix}{ShellConfig.ShellVersion} · 已是最新版本";
            UpdateInstallButton.IsVisible = false;
            ChangelogChevron.IsVisible = true;
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
        var added = await NewlyAddedService.GetTodayAsync();
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
    private void BuildQuickLinks() => QuickGrid.ItemsSource = QuickLinks.Build(App.Content.Ui);

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
            var path = EmbeddedAssets.ExtractToCache("dv1.1.png", "banner-dv1.1.png");
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
