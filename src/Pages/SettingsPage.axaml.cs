using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Controls;
using ClassSoftwareHub.Desktop.Views;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services.Updating;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>原生设置页：关于（最上，突出版本号）/ 外观 / 窗口行为 / 内容。改动即时生效。</summary>
public sealed partial class SettingsPage : PageBase
{
    private bool _loading = true;
    private bool _contentBusy;
    /// <summary>
    /// 打开设置页后把滚动位置拉回顶部的定时器。
    /// ⚠️ 必须存字段 —— DispatcherTimer 被 GC 收走就不会触发（本项目踩过）。
    /// </summary>
    private DispatcherTimer? _settleTimer;

    public SettingsPage()
    {
        InitializeComponent();

        // 「版本记录」展开时才去读本机记录。
        // ⚠️ 原版监听 Toolkit SettingsExpander 的 Expanded 事件；FA 没有这个事件，
        //    这里改监听 IsExpandedProperty（见 SettingsPage.axaml 里那条移植说明）。
        HistoryExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property == SettingsExpander.IsExpandedProperty && HistoryExpander.IsExpanded)
                LoadLocalHistory();
        };

        // 「本地安装包」展开时才去读目录。
        // ⚠️ 原版监听 Toolkit SettingsExpander 的 Expanded 事件；FA 没有这个事件，
        //    这里改监听 IsExpandedProperty（同 HistoryExpander 的写法）。
        InstallerExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property == SettingsExpander.IsExpandedProperty && InstallerExpander.IsExpanded)
                RefreshInstallerList();
        };

        // 回声洞折叠区固定收起：模板里那个内层 Expander 要等可视树就绪才压得动，
        // 用 AttachedToVisualTree 兜一道（原版挂在 Loaded 上，语义等价）。
        EchoCaveExpander.AttachedToVisualTree += (_, _) => CollapseEchoCave();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        _loading = true;

        var s = App.Settings.Current;
        BackdropCombo.SelectedIndex = s.Backdrop switch
        {
            "mica" => 1,
            "solid" => 2,
            _ => 0
        };
        TopMostSwitch.IsChecked = s.AlwaysOnTop;
        AutoStartSwitch.IsChecked = s.AutoStart;
        MinimizeSwitch.IsChecked = s.MinimizeOnStart;
        TraySwitch.IsChecked = s.CloseToTray;

        // 2026-10-07（对齐上游 dv1.1.0）：原「常用工具窗口 / 侧边栏」与「截图自动保存」两组设置项，
        // 上游 2026-10-01 搬去了「侧边布局」页 —— 这里的初始化与处理函数一并删干净（见 SidebarLayoutPage）。

        ThemeCombo.SelectedIndex = s.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };

        // 外部组件（分体）外观
        SplitThemeSwitch.IsChecked = s.SplitTheme;
        ExtThemeCombo.SelectedIndex = s.ExternalTheme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        UpdateExtThemeAvailability();
        UpdateMinimizeAvailability();

        ChannelCombo.SelectedIndex = UpdateChannels.Parse(s.UpdateChannel) == UpdateChannel.Insider ? 1 : 0;
        AutoCheckSwitch.IsChecked = s.AutoCheckUpdate;
        // 安装包保留数量：下拉项 1~10 与索引一一对应；存档里的怪值夹回 1~10 再定位
        KeepCombo.SelectedIndex = Math.Clamp(s.InstallerKeepCount, 1, 10) - 1;
        UpdateCurrent.Text = $"当前版本：{ShellConfig.VersionPrefix}{ShellConfig.ShellVersion}· 更新源：{UpdateService.CreateDefault().Source.DisplayName}";

        RefreshContentInfo();
            BuildAboutHeader();
            SettingsQuickGrid.ItemsSource = Core.QuickLinks.Build(App.Content.Ui);
            _loading = false;

        // GitHub 下载取用路径（软件内容 → GitHub 应用更新加速源）；与上游设置页一致，默认自动。
        GithubRouteCombo.SelectedIndex = Services.GithubRoute.Current switch
        {
            Services.GithubRoutes.SelfHosted => 1,
            Services.GithubRoutes.Official => 2,
            _ => 0
        };
        UpdateGithubRouteHint();

        // 「版本记录」固定折叠：里面是十几条运行记录，摊开会把设置页拉得极长。
        // ⚠️ 只在 XAML 里写 IsExpanded="False" 不够 —— 实测加载过程中仍会被撑开，这里再压一次。
        HistoryExpander.IsExpanded = false;
        InstallerExpander.IsExpanded = false;
        CollapseEchoCave();

        // 打开设置页固定从顶部开始：卡片高度会被设置值二次刷新，ScrollViewer 的锚点跟着漂，
        // 实测会直接停到「常用工具」那一段（2026-09-28）。
        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            // ⚠️ 原版 ScrollViewer.ChangeView(null, 0, null, true)；Avalonia 没有 ChangeView，
            //    直接写 Offset（同样的"带动画滚回顶部"在这里不做 —— 原样本意就是瞬间归零）。
            RootScroll.Offset = new Vector(RootScroll.Offset.X, 0);

            // 三个折叠区在导航后 200ms（早已过布局）再压一次，防"首帧摊开"。
            HistoryExpander.IsExpanded = false;
            InstallerExpander.IsExpanded = false;
            CollapseEchoCave();
        };
        _settleTimer.Start();

        // 回声洞折叠区固定收起（原版在 Loaded 里做；移植版页面每次新建，导航入场再压一次）。
        CollapseEchoCave();
    }

    // ══════════════════════════ 诊断 ══════════════════════════

    /// <summary>设置 → 诊断 → 日志查看（Nick 2026-10-02：正式入口，不再放实验性分组）。
    /// 走 ShellPage 的导航（左侧「设置」保持高亮），返回键能回到设置页。</summary>
    private void OpenLogViewer_Click(object? sender, RoutedEventArgs e)
    {
        App.MainWindow?.Shell.NavigateTo("log-viewer");
    }

    // ══════════════════════════ 回声洞 ══════════════════════════

    /// <summary>回声洞那张卡片整卡可点＝换一条（悬停底色铺满整行，正文自己不带框）。</summary>
    private void EchoCave_Click(object? sender, RoutedEventArgs e) => EchoCave.ShowNext();

    /// <summary>
    /// 把回声洞折叠区收起来（设置页一打开必须是折叠的样子，2026-10-03 Nick 指定）。
    /// ⚠️ 外层 SettingsExpander.IsExpanded 与模板里那个内层 Expander 都要压 ——
    ///    内层才是真身（模板里绑的是 TwoWay，它起手展开会把 true 反推回外层），只压外层会被顶回来。
    /// ⛔ 别删。实测口径：收起后外层/内层 ActualHeight 都是 70（只剩表头行），摊开 125~165（随内容长短）。
    /// </summary>
    private void CollapseEchoCave()
    {
        // 光设 false 在这个控件上不够稳：只在 IsExpanded **发生变化**时才驱动 VisualState。
        // 所以先拨到 true 再拨回 false，强制它把"折叠"真的跑一遍。
        if (!EchoCaveExpander.IsExpanded) EchoCaveExpander.IsExpanded = true;
        EchoCaveExpander.IsExpanded = false;

        var inner = FindDescendant<Expander>(EchoCaveExpander);
        if (inner is not null)
        {
            if (!inner.IsExpanded) inner.IsExpanded = true;
            inner.IsExpanded = false;
        }
    }

    /// <summary>深度优先找一个后裔控件（模板里的内层控件用；找不到返回 null）。</summary>
    private static T? FindDescendant<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 「投稿」弹层里的提交：走「提交软件」同一套自建服务（令牌在服务端）。
    /// 回执**就留在弹层里** —— 成功时给一句回执、停一下自己收起，失败时留着让用户看。
    /// </summary>
    private async void SubmitEchoCave_Click(object? sender, RoutedEventArgs e)
    {
        var text = EchoSubmitBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            ShowEchoSubmitStatus("还没写内容。");
            return;
        }

        EchoSubmitButton.IsEnabled = false;
        EchoSubmitButton.Content = "正在提交";
        ShowEchoSubmitStatus("正在提交…");

        var (ok, message) = await Services.EchoCaveService.SubmitAsync(text);

        EchoSubmitButton.IsEnabled = true;
        EchoSubmitButton.Content = "提交";
        ShowEchoSubmitStatus(message);
        // 服务端还没接上（或网络被挡）时给条退路：去 GitHub 网页投。成功时不给，免得画蛇添足。
        EchoSubmitFallback.IsVisible = !ok;

        if (ok)
        {
            EchoSubmitBox.Text = "";

            // 回执看一眼够了就自己收（成功才收；失败留着，还能点兜底链接）。
            await Task.Delay(1800);
            if (EchoSubmitStatus.Text == message) EchoSubmitButton.Flyout?.Hide();
        }
    }

    private async void SubmitEchoCaveFallback_Click(object? sender, RoutedEventArgs e)
    {
        if (await EchoCave.OpenSubmitPage()) EchoSubmitButton.Flyout?.Hide();
        else ShowEchoSubmitStatus("无法打开浏览器，请手动访问 GitHub 投稿。");
    }

    private void ShowEchoSubmitStatus(string message)
    {
        EchoSubmitStatus.Text = message;
        EchoSubmitStatus.IsVisible = true;
    }

    private void RefreshContentInfo()
    {
        var c = App.Content;
        ContentSummary.Text = c.HasData
            ? $"{c.Apps.Count} 个软件 · {c.Categories.Count} 个分类" +
              (c.ContentVersion.Length > 0 ? $" · 内容版本 {c.ContentVersion}" : "")
            : "没有读到内容。";

        ContentSource.Text = $"内容来源：{c.SourceLabel}\n{c.Source}";

        ContentIssues.Text = c.Issues.Count > 0
            ? $"有 {c.Issues.Count} 条解析提示：" + string.Join("；", c.Issues.Take(3).Select(i => i.File + " — " + i.Message))
            : "";
        ContentIssues.IsVisible = c.Issues.Count > 0;

        AboutApp.Text = ShellConfig.AppName;
        AboutVersion.Text = ShellConfig.VersionPrefix + ShellConfig.ShellVersion;

        // ⚠️ 移植说明：原版本轮改为**以编译进程序的常量为准**，不再读内容包的 app.version
        //    （内容包的 text/ 不联网更新、装机即冻结，读它会一直显示装机那天那版）。
        //    原版用 ShellConfig.SiteVersionDisplay（带代号的长串）；本移植版暂无该常量，
        //    退回编译期常量 SiteVersionTarget —— 语义一致（同样不受内容包冻结影响）。
        AboutSiteVersion.Text = $"站点版本：{ShellConfig.SiteVersionTarget}";
    }

    private void UpdateMinimizeAvailability()
    {
        var on = AutoStartSwitch.IsChecked == true;
        MinimizeSwitch.IsEnabled = on;
        MinimizeHint.Opacity = on ? 0.55 : 0.4;
        MinimizeHint.Text = on
            ? "开机启动时直接收进托盘（任务栏上不留按钮），想用的时候点托盘图标。"
            : "只有打开「开机自动启动」时，这个选项才有用。";
    }

    private void ThemeChoice_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;
        App.MainWindow?.SetTheme(theme);       // 里面会 Save + 重算背景 + 通知外部组件
        App.MainWindow?.Shell.UpdateThemeButton();
    }

    // ── 外部组件（分体）外观 ─────────────────────────────────

    private void SplitTheme_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.SplitTheme = SplitThemeSwitch.IsChecked == true;
        App.Settings.Save();
        UpdateExtThemeAvailability();
        ThemeCompat.Notify();                  // 外部组件（侧边栏/浮窗/截图窗）立刻换过来
    }

    private void ExtTheme_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ExtThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;
        App.Settings.Current.ExternalTheme = theme;
        App.Settings.Save();
        ThemeCompat.Notify();
    }

    private void UpdateExtThemeAvailability()
    {
        ExtThemeCombo.IsEnabled = SplitThemeSwitch.IsChecked == true;
        // ⚠️ 2026-10-08 起音量/亮度/合成器小浮窗改跟**外部组件**外观走（Nick：分体深色下浮窗也得变深），
        //    提示文案同步更新，别再让用户以为这个开关管不到它们。
        ExtThemeHint.Text = SplitThemeSwitch.IsChecked == true
            ? "侧边栏 / 常用工具浮窗 / 截图编辑窗 / 音量亮度浮窗都跟着这个走"
            : "现在是关的：外部组件跟主界面的颜色模式保持一致";
    }

    private void BackdropChoice_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BackdropCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string kind) return;
        App.Settings.Current.Backdrop = kind;
        App.Settings.Save();
        App.MainWindow?.SetBackdrop(kind);
    }

    private void TopMostSwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AlwaysOnTop = TopMostSwitch.IsChecked == true;
        App.Settings.Save();
        App.MainWindow?.SetAlwaysOnTop(TopMostSwitch.IsChecked == true);
    }

    private void AutoStartSwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AutoStart = AutoStartSwitch.IsChecked == true;
        App.Settings.Save();
        UpdateMinimizeAvailability();
        App.MainWindow?.SetAutoStart(AutoStartSwitch.IsChecked == true);
    }

    private void MinimizeSwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.MinimizeOnStart = MinimizeSwitch.IsChecked == true;
        App.Settings.Save();
        App.MainWindow?.SetMinimizeOnStart(MinimizeSwitch.IsChecked == true);
    }

    private void TraySwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.MainWindow?.SetCloseToTray(TraySwitch.IsChecked == true);
    }

    // ── 常用工具 / 侧边栏 / 截图：2026-10-07 整组搬去「侧边布局」页 ──────────
    // 上游 2026-10-01 把「浮窗置顶 / 屏幕边缘侧边栏 / 放置模式 / 贴哪条边」四项与「截图」两项
    // 都移到了 SidebarLayoutPage（那页本就是侧边栏与常用工具的布置中心）。
    // 这里对应的 13 个处理函数（PaletteTopSwitch_Toggled / SidebarSwitch_Toggled /
    // RebuildEdgeCombo / AddEdgeItem / SelectEdge / UpdateSidebarHints / SidebarMode_SelectionChanged /
    // SidebarEdge_SelectionChanged / ShotAutoSave_Toggled / RefreshShotDir / ShotDir_Change_Click /
    // ShotDir_Open_Click / ShotDir_Reset_Click）一并删除，逻辑原样搬到了那边。

    private void ReloadContent_Click(object? sender, RoutedEventArgs e)
    {
        App.Content.Load();
        RefreshContentInfo();
    }

    /// <summary>手动同步内容包（远端发布了才有东西下；失败也不影响本机数据）。</summary>
    private async void SyncContent_Click(object? sender, RoutedEventArgs e)
    {
        if (_contentBusy) return;
        _contentBusy = true;

        ContentSource.Text = "正在同步内容包…";
        try
        {
            var result = await Services.ContentUpdater.SyncAsync(
                new Progress<string>(text => ContentSource.Text = text));

            if (result.Updated) App.Content.Load();

            ContentIssues.Text = result.Message ?? result.Error ?? "";
            ContentIssues.IsVisible = ContentIssues.Text.Length > 0;
            RefreshContentInfo();
        }
        catch (Exception ex)
        {
            ContentIssues.Text = "同步失败：" + ex.Message;
            ContentIssues.IsVisible = true;
        }
        finally
        {
            _contentBusy = false;
        }
    }

    private void OpenContentDir_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.Content.Source;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* 打不开就算了 */ }
    }

    // ══════════════════════════ 更新 ══════════════════════════

    private readonly UpdateService _updater = UpdateService.CreateDefault();
    private bool _updateBusy;

    private void ChannelChoice_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ChannelCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string channel) return;
        App.Settings.Current.UpdateChannel = channel;
        App.Settings.Current.UpdateChannelSetByUser = true;
        App.Settings.Save();

        UpdateStatus.Text = channel == UpdateChannels.Insider
            ? "Insider 通道：优先收 Pre-release；正式版发出来比当前还新时，也会自动跟上。"
            : "稳定通道：只收 Latest（正式发布）的版本。";
        UpdateNotes.IsVisible = false;
    }

    private void AutoCheckSwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AutoCheckUpdate = AutoCheckSwitch.IsChecked == true;
        App.Settings.Save();
    }

    /// <summary>检查更新：查到新版先弹窗问用户要不要装（不强制）。</summary>
    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        CheckButton.IsEnabled = false;
        UpdateNotes.IsVisible = false;
        UpdateStatus.Text = "正在检查更新…";

        try
        {
            var channel = UpdateChannels.Parse(App.Settings.Current.UpdateChannel);
            var result = await _updater.CheckAsync(channel, ShellConfig.ShellVersion);

            App.Settings.Current.LastUpdateCheck = DateTimeOffset.Now.ToUnixTimeSeconds();
            App.Settings.Save();

            UpdateStatus.Text = result.Message;

            if (result is { HasUpdate: true, Release: { } release })
            {
                var notes = Snip(release.Notes, 400);
                UpdateNotes.Text = notes;
                UpdateNotes.IsVisible = notes.Length > 0;
                UpdateStatus.Text = $"发现新版本 {release.Tag}，可自行选择是否安装。";

                // 先问；选「稍后」就什么都不做
                // ⚠️ 原版首参 XamlRoot 是 WinUI 的弹窗宿主；移植版 UpdateFlow 改收 owner（TopLevel）。
                var choice = await UpdateFlow.AskAsync(release, TopLevel.GetTopLevel(this));
                if (choice == UpdateFlow.UpdateChoice.Later) return;

                // 「后台下载」：不弹进度窗，下完发系统通知，装不装等用户点。
                if (choice == UpdateFlow.UpdateChoice.Background)
                {
                    if (UpdateFlow.StartBackgroundDownload(_updater, release))
                        UpdateStatus.Text = $"已转为后台下载 {release.Tag}，完成后通过系统通知提醒。";
                    else
                        UpdateStatus.Text = "已有一个更新正在后台下载，完成后会通知。";
                    return;
                }

                if (!await UpdateFlow.RunAsync(_updater, release, owner: TopLevel.GetTopLevel(this)))
                    UpdateStatus.Text = "更新失败：可稍后重试，或前往发布页手动下载新版本。";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            _updateBusy = false;
            CheckButton.IsEnabled = true;
        }
    }

    /// <summary>关于区的软件图标（内嵌资源，单文件发布下也能取到）。</summary>
    private void BuildAboutHeader()
    {
        try
        {
            var path = Services.EmbeddedAssets.ExtractToCache("AppIcon-512.png", "AppIcon-512.png");
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                // ⚠️ WinUI BitmapImage(new Uri(path)) → Avalonia Bitmap(文件名)。
                AboutIcon.Source = new Bitmap(path);
        }
        catch { /* 图标取不到就不显示 */ }
    }

    /// <summary>
    /// 跟首页那一行同一个口径：带 Tag 的走应用内导航（「更新日志」），其余才丢给系统浏览器。
    /// ⚠️ 原版 GridView 的 ItemClick（e.ClickedItem）→ Avalonia 用 Tapped 冒泡 + 从
    ///    DataContext 取 QuickLink（与 WelcomePage.QuickGrid_Tapped 同一套写法）。
    /// </summary>
    private void Quick_ItemClick(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is not Core.QuickLink link) return;

        if (link.Tag.Length > 0)
        {
            App.MainWindow?.Shell.NavigateTo(link.Tag);
            return;
        }

        if (link.Url.Length > 0)
            App.MainWindow?.OpenExternal(link.Url);
    }

    // ── 版本记录（本机运行过的版本） ────────────────────────────

    /// <summary>本机记录：这台电脑运行过哪些版本（启动时自动记的），不是仓库的 Release 列表。</summary>
    private void LoadLocalHistory()
    {
        LocalHistoryList.Children.Clear();
        var records = Services.VersionHistory.Load();
        if (records.Count == 0)
        {
            LocalHistoryList.Children.Add(new TextBlock
            {
                Text = "本机还没有版本记录（这次启动就会记下第一条）。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var current = Core.ShellConfig.ShellVersion;
        foreach (var record in records)
        {
            var isCurrent = string.Equals(record.Version, current, StringComparison.OrdinalIgnoreCase);
            var channel = record.Channel.Length > 0 ? $"  [{record.Channel}]" : "";

            var row = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = Services.VersionHistory.Display(record) + channel + (isCurrent ? "  ·  当前版本" : ""),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"首次运行 {record.FirstSeen:yyyy-MM-dd HH:mm} · 最近一次 {record.LastSeen:yyyy-MM-dd HH:mm}",
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            });
            LocalHistoryList.Children.Add(row);
        }
    }

    // ── 历史版本（回滚） ──────────────────────────────────────

    private async void LoadHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        LoadHistoryButton.IsEnabled = false;
        HistoryRing.IsVisible = true;
        HistoryRing.IsActive = true;
        HistoryList.Children.Clear();

        try
        {
            var rollbackChannel = UpdateChannels.Parse(App.Settings.Current.UpdateChannel);
            var releases = await _updater.GetHistoryAsync(rollbackChannel, 20);
            if (releases.Count == 0)
            {
                HistoryList.Children.Add(new TextBlock
                {
                    Text = _updater.Source.IsConfigured ? "没查到版本（仓库里还没发过 Release）。" : "还没配置更新仓库地址。",
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }
            foreach (var release in releases)
                HistoryList.Children.Add(HistoryRow(release));

            // 正式版用户看不到预览版：说清楚，免得以为列表缺斤少两
            if (rollbackChannel == UpdateChannel.Stable)
            {
                HistoryList.Children.Add(new TextBlock
                {
                    Text = "当前是稳定通道，只列正式版；想看 Insider 版（Beta）就先在上面把更新通道切成 Insider 再加载。",
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            }
        }
        catch (Exception ex)
        {
            HistoryList.Children.Add(new TextBlock
            {
                Text = "加载失败：" + ex.Message,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        finally
        {
            _updateBusy = false;
            LoadHistoryButton.IsEnabled = true;
            HistoryRing.IsActive = false;
            HistoryRing.IsVisible = false;
        }
    }

    private Control HistoryRow(UpdateRelease release)
    {
        // ⚠️ 原版 Grid 的 ColumnSpacing=12 / Padding → Avalonia 用 Margin 模拟（右列左缩进 12）。
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var date = release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
        var size = release.Primary?.SizeText ?? "";
        var badge = release.Prerelease ? "Insider" : "正式版";

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = $"{release.Tag}   [{badge}]",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        info.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[] { date, size }.Where(s => s.Length > 0)),
            FontSize = 12,
            Opacity = 0.65,
        });

        // ⚠️ 对象初始化器里 `VerticalAlignment = VerticalAlignment.Center` 会把右侧解析成
        //    Button 自己的实例属性（CS0176），这里写全限定名。
        var install = new Button { Content = "装这个", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        install.Click += async (_, _) => await RollbackAsync(release);
        ToolTip.SetTip(install, "安装这个版本（覆盖当前程序，设置和内容不会丢）");

        Grid.SetColumn(info, 0);
        Grid.SetColumn(install, 1);
        row.Children.Add(info);
        row.Children.Add(install);
        return row;
    }

    private async Task RollbackAsync(UpdateRelease release)
    {
        // ⚠️ 原版 ContentDialog.XamlRoot 是 WinUI 的弹窗宿主；FA 的 ContentDialog.ShowAsync
        //    直接收 TopLevel（与 Services.Updating.UpdateFlow 内部同一套写法）。
        var confirm = new ContentDialog
        {
            Title = "回滚到这个版本？",
            Content = $"将要安装 {release.Tag}（{(release.Prerelease ? "Insider" : "正式版")}）。\n" +
                      "会覆盖当前程序文件；你的设置和软件内容不会丢。",
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        var top = TopLevel.GetTopLevel(this);
        var result = top is null ? await confirm.ShowAsync() : await confirm.ShowAsync(top);
        if (result != ContentDialogResult.Primary) return;

        await UpdateFlow.RunAsync(_updater, release, "正在安装指定版本", TopLevel.GetTopLevel(this));
    }

    private static string Snip(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Replace("\r", "").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    // ── 安装包自动清理（updates 目录） ─────────────────────────

    /// <summary>保留数量改动：存档 + 后台立刻清一轮（不用等下次启动），列表若摊开着就跟着刷新。</summary>
    private void KeepCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (KeepCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var keep)) return;

        App.Settings.Current.InstallerKeepCount = Math.Clamp(keep, 1, 10);
        App.Settings.Save();

        var target = App.Settings.Current.InstallerKeepCount;
        _ = Task.Run(() =>
        {
            try { InstallerCleanup.Clean(target); }
            catch { /* 清理失败不打扰界面，下次启动会再试 */ }
        }).ContinueWith(_ =>
        {
            // ⚠️ 移植：原版 DispatcherQueue.TryEnqueue → Avalonia Dispatcher.UIThread.Post。
            Dispatcher.UIThread.Post(() =>
            {
                if (InstallerExpander.IsExpanded) RefreshInstallerList();
            });
        });
    }

    /// <summary>「本地安装包」展开时现读目录（平时折叠，不占加载时间）。</summary>
    private void RefreshInstallerList()
    {
        InstallerList.Children.Clear();
        InstallerDirText.Text = $"存放位置：{InstallerCleanup.InstallerDirectory}";

        var files = InstallerCleanup.Scan();
        if (files.Count == 0)
        {
            InstallerList.Children.Add(new TextBlock
            {
                Text = "本地暂无安装包。更新完成后会自动存放于此。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var f in files)
            InstallerList.Children.Add(BuildInstallerRow(f));
    }

    /// <summary>单行：左边名称 + 大小/时间，右边「覆盖安装」「删除」。</summary>
    private StackPanel BuildInstallerRow(InstallerFileInfo f)
    {
        var meta = $"{FormatSize(f.Length)}　·　{f.ModifiedUtc.LocalDateTime:yyyy-MM-dd HH:mm}"
                   + (f.HasMd5File ? "　·　含校验文件" : "");

        var name = new TextBlock
        {
            Text = f.Name,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var sub = new TextBlock
        {
            Text = meta,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        };

        // 「覆盖安装」：留着这个包的直接用途 —— 原地覆盖装一遍当前（或更旧）版本，不用重新下载。
        // ⚠️ 对象初始化器里 VerticalAlignment 会被解析成控件自身属性（CS0176），写全限定名（同 HistoryRow）。
        var install = new Button { Content = "覆盖安装", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        ToolTip.SetTip(install, "用这个安装包原地覆盖安装，安装目录与各项设置保持不变。");
        install.Click += async (_, _) => await InstallFromLocalAsync(f);

        var del = new Button { Content = "删除", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        del.Click += async (_, _) => await DeleteInstallerAsync(f);

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        actions.Children.Add(install);
        actions.Children.Add(del);

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(name);
        left.Children.Add(sub);

        // ⚠️ 原版 Grid ColumnSpacing=12；本仓库不用 Grid 的 ColumnSpacing，右列靠按钮自身左边距让位。
        install.Margin = new Thickness(12, 0, 0, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Grid.SetColumn(left, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(left);
        grid.Children.Add(actions);

        var wrap = new StackPanel { Spacing = 0 };
        wrap.Children.Add(grid);
        return wrap;
    }

    /// <summary>
    /// 用本地已有的安装包覆盖安装（不必重新下载）。
    /// ⚠️ 真正的"先退应用、安装程序后起"由 UpdateFlow.InstallLocalAsync 保证 ——
    ///    顺序反了安装会被 Inno 静默取消（见 UpdateService.RunInstaller 的注释）。
    /// </summary>
    private async Task InstallFromLocalAsync(InstallerFileInfo f)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = f.Name,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = "安装程序会按同一个应用标识原地覆盖安装，安装目录与各项设置都保持不变，无需重新下载。"
                 + "安装过程中应用会自动关闭，装好后自动重新打开（约十几秒），此过程并非程序异常。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        // ⚠️ 原版 ContentDialog.XamlRoot → FA 的 ShowAsync(TopLevel)（同 RollbackAsync 的写法）。
        var confirm = new ContentDialog
        {
            Title = "用这个安装包覆盖安装",
            Content = body,
            PrimaryButtonText = "覆盖安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        var top = TopLevel.GetTopLevel(this);
        var result = top is null ? await confirm.ShowAsync() : await confirm.ShowAsync(top);
        if (result != ContentDialogResult.Primary) return;

        await UpdateFlow.InstallLocalAsync(f.FullPath);
    }

    private async Task DeleteInstallerAsync(InstallerFileInfo f)
    {
        var confirm = new ContentDialog
        {
            Title = "删除安装包",
            Content = $"删除 {f.Name}？删除后装回该版本需重新下载。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        var top = TopLevel.GetTopLevel(this);
        var result = top is null ? await confirm.ShowAsync() : await confirm.ShowAsync(top);
        if (result != ContentDialogResult.Primary) return;

        InstallerCleanup.TryDeleteWithMd5(f.FullPath);
        RefreshInstallerList();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.0#} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.0#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }

    // ── GitHub 应用更新加速源（软件内容） ─────────────────────────
    // 与网页端设置页一致：自动 / 自建加速服务 / GitHub 源。
    // 实际改写下载链接的逻辑在 Services.GithubRoute（自建优先 → 公益镜像测速择优 → GitHub 官方保底）。

    private void GithubRoute_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GithubRouteCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string route) return;
        App.Settings.Current.GithubDownloadRoute = Services.GithubRoutes.Normalize(route);
        App.Settings.Save();
        Services.GithubRoute.InvalidateCache();
        UpdateGithubRouteHint();
    }

    private void UpdateGithubRouteHint()
    {
        var current = Services.GithubRoute.Current;
        GithubRouteHint.Text = current switch
        {
            Services.GithubRoutes.SelfHosted => "更新包等自动下载优先走社区自建加速节点（本站 Worker 代签、限时直链）；节点不可用时自动测速挑选最快的公益镜像，全部不可用再回 GitHub 源。",
            Services.GithubRoutes.Official => "更新包等自动下载直接连接 github.com，不做任何改写。",
            _ => "更新包等自动下载优先走社区自建加速节点；节点不可用时自动测速挑选最快的公益镜像，全部不可用再回 GitHub 源。",
        };
        // 只要不是「GitHub 源」就给一条说明：自建节点已就绪（Worker 代签、限时直链），
        // 不可用时自动按测速挑最快公益镜像，全不可用再回 GitHub 官方直链。
        GithubRouteWarning.IsOpen = current != Services.GithubRoutes.Official;
    }

    // ── 法律条款（关于 → 用户协议 / 隐私政策 / 免责声明） ──────────

    private void OpenLegal_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard card || card.Tag is not string key) return;
        App.MainWindow?.Shell.NavigateToLegal(key);
    }
}
