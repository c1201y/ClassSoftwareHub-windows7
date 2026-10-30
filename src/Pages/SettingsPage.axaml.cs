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
    /// 「本地缓存」「本地日志」「本地安装包」「版本记录」「从仓库装回旧版本」五个列表的分页：
    /// 每页最多这么多条。展开后一屏拉不到头 → 改成翻页；数量不超一页时整条分页栏收起来。
    /// </summary>
    private const int ListPageSize = 10;

    private int _cachePage = 1;
    private int _logPage = 1;
    private int _installerPage = 1;
    private int _localHistoryPage = 1;
    private int _historyPage = 1;

    /// <summary>
    /// 「从仓库装回旧版本」加载过的那一份（最多 20 条）。
    /// ⚠️ 翻页**只翻这份缓存**，⛔不重新打网络 —— GitHub 那一步又慢又容易被限流，
    ///    点一下「下一页」就重新拉一次，体验会很糟。要刷新得再点那颗「从仓库加载」按钮。
    /// </summary>
    private IReadOnlyList<UpdateRelease>? _historyReleases;

    /// <summary>「从仓库装回旧版本」列表末尾那句说明（稳定通道只列正式版）；没有就不显示。</summary>
    private string? _historyNote;

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
            {
                _localHistoryPage = 1;      // 重新展开＝从第一页看起
                LoadLocalHistory();
            }
        };

        // 「本地安装包」展开时才去读目录。
        // ⚠️ 原版监听 Toolkit SettingsExpander 的 Expanded 事件；FA 没有这个事件，
        //    这里改监听 IsExpandedProperty（同 HistoryExpander 的写法）。
        InstallerExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property == SettingsExpander.IsExpandedProperty && InstallerExpander.IsExpanded)
            {
                _installerPage = 1;         // 重新展开＝从第一页看起
                RefreshInstallerList();
            }
        };

        // 「本地缓存」「本地日志」展开时才去读目录（同上面两个的写法）。
        CacheExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property == SettingsExpander.IsExpandedProperty && CacheExpander.IsExpanded)
            {
                _cachePage = 1;             // 重新展开＝从第一页看起
                RefreshCacheList();
            }
        };
        LogExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property == SettingsExpander.IsExpandedProperty && LogExpander.IsExpanded)
            {
                _logPage = 1;               // 重新展开＝从第一页看起
                RefreshLogList();
            }
        };

        // 回声洞折叠区固定收起：模板里那个内层 Expander 要等可视树就绪才压得动，
        // 用 AttachedToVisualTree 兜一道（原版挂在 Loaded 上，语义等价）。
        EchoCaveExpander.AttachedToVisualTree += (_, _) => CollapseEchoCave();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        _loading = true;

        // 后台同步完成时这一屏要自己更新（安装包不自带清单，首次进来多半还空着）。
        // ⚠️ 页面实例会被框架缓存（长驻），先 -= 再 += 保证只挂一次。
        App.Content.Changed -= OnContentChanged;
        App.Content.Changed += OnContentChanged;

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
        // 缓存保留数量：同款下拉（1~10 与索引一一对应）
        CacheKeepCombo.SelectedIndex = Math.Clamp(s.CacheKeepCount, 1, 10) - 1;
        // 日志保留天数：3/7/15/30/永不（Tag 存天数，0 = 永不删除）；存档里出现别的值一律落回「7 天」
        LogKeepCombo.SelectedIndex = LogKeepIndex(s.LogKeepDays);
        UpdateCurrent.Text = $"当前版本：{ShellConfig.VersionPrefix}{ShellConfig.ShellVersion}· 更新源：{UpdateService.CreateDefault().Source.DisplayName}";

        // 教室喊话已独立成页（内置工具 → 教室喊话），设置页只剩跳转按钮（AnnouncePageJump_Click）。

        RefreshContentInfo();
            BuildAboutHeader();
            BuildQuickLinks();
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
        CacheExpander.IsExpanded = false;
        LogExpander.IsExpanded = false;
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

            // 折叠区在导航后 200ms（早已过布局）再压一次，防"首帧摊开"。
            HistoryExpander.IsExpanded = false;
            InstallerExpander.IsExpanded = false;
            LogExpander.IsExpanded = false;
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

    /// <summary>Hub AI 二级设置页（2026-10-18 上游新增；2026-10-22 移植）。和日志查看同一套走法：直跳，返回靠导航栏。</summary>
    private void OpenHubAiSettings_Click(object? sender, RoutedEventArgs e)
    {
        App.MainWindow?.Shell.NavigateToHubAiSettings();
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

        // ⚠️ async void 里抛出去会直接带走进程；而且按钮会永远停在「正在提交」且不可点。
        //    异常必须在本地收口，复位交给 finally。
        bool ok;
        string message;
        try
        {
            (ok, message) = await Services.EchoCaveService.SubmitAsync(text);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("echo", ex, "回声洞投稿失败");
            ok = false;
            message = "提交失败：" + ex.Message;
        }
        finally
        {
            EchoSubmitButton.IsEnabled = true;
            EchoSubmitButton.Content = "提交";
        }

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

    public override void OnNavigatedFrom()
    {
        App.Content.Changed -= OnContentChanged;
        base.OnNavigatedFrom();
    }

    /// <summary>内容变了 → 刷新这一组摘要（手动同步进行中就别抢它刚写上的状态行）。</summary>
    private void OnContentChanged()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(OnContentChanged);
            return;
        }
        if (_contentBusy) return;
        RefreshContentInfo();
    }

    private void RefreshContentInfo()
    {
        var c = App.Content;
        ContentSummary.Text = c.HasData
            ? $"{c.Apps.Count} 个软件 · {c.Categories.Count} 个分类" +
              (c.ContentVersion.Length > 0 ? $" · 内容版本 {c.ContentVersion}" : "")
            : (c.IsSyncing ? "正在获取软件清单…" : "还没有获取到内容 —— 需要联网。");

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

        ContentSource.Text = "正在从网络获取…";
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
                // 记下"有新版本"：首页那颗「立即更新」靠它（2026-10-18 自上游 1.2.0 移植）
                Services.Updating.UpdateNotice.Set(release);

                // ⛔ 这里原来会把发版正文渲染进设置卡（UpdateNotes）。上游 2026-10-26 去掉：
                //    一屏 Markdown 撑满整页、还把配图链接显示成源码，用户直接说"排版该改一改"。
                //    正文改由下面的更新对话框展示（它只挑「前言」要点，已洗干净）。
                UpdateStatus.Text = $"发现新版本 {release.Tag}，可自行选择是否安装。";

                // 先问；选「稍后」就什么都不做
                // ⚠️ 原版首参 XamlRoot 是 WinUI 的弹窗宿主；移植版 UpdateFlow 改收 owner（TopLevel）。
                var choice = await UpdateFlow.AskAsync(release, TopLevel.GetTopLevel(this));
                if (choice == UpdateFlow.UpdateChoice.Later) return;

                // 「后台下载」：不弹进度窗，下完发系统通知，装不装等用户点。
                if (choice == UpdateFlow.UpdateChoice.Background)
                {
                    if (UpdateFlow.StartBackgroundDownload(_updater, release))
                        UpdateStatus.Text = $"已转入「任务进行」页下载 {release.Tag}，可在该页查看进度并安装。";
                    else
                        UpdateStatus.Text = "已有一个更新正在后台下载，可在「任务进行」页查看。";
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

    /// <summary>关于区的一行快捷入口（同首页；2026-10-18 对齐上游 1.2.0：图标改走染色 SVG）。</summary>
    private void BuildQuickLinks()
    {
        var links = Core.QuickLinks.Build(App.Content.Ui);
        SettingsQuickGrid.ItemsSource = links;
        ApplyQuickLinkIcons(links);

        ActualThemeVariantChanged -= SettingsPage_ThemeChanged;
        ActualThemeVariantChanged += SettingsPage_ThemeChanged;
    }

    private void SettingsPage_ThemeChanged(object? sender, EventArgs e)
    {
        if (SettingsQuickGrid.ItemsSource is IEnumerable<Core.QuickLink> links) ApplyQuickLinkIcons(links);
    }

    /// <summary>按"本页所在的树"的主题前景色把 SVG 染色（别从应用级资源取，见首页同名方法）。</summary>
    private void ApplyQuickLinkIcons(IEnumerable<Core.QuickLink> links)
    {
        var color = (Services.ThemeBrush.Get(this, "TextFillColorPrimaryBrush") as Avalonia.Media.ISolidColorBrush)?.Color
                    ?? Avalonia.Media.Colors.Black;
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.IconSvg)) continue;
            try { link.IconSource = Services.QuickLinkIconLoader.GetIcon(link.IconSvg!, color); }
            catch { /* 取不到就退回 FontIcon，不影响点击 */ }
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
        var records = Services.VersionHistory.Load();
        if (records.Count == 0)
        {
            LocalHistoryList.Children.Clear();
            LocalHistoryPager.IsVisible = false;
            LocalHistoryList.Children.Add(new TextBlock
            {
                Text = "本机还没有版本记录（这次启动就会记下第一条）。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        _localHistoryPage = RenderPagedList(LocalHistoryList, records, _localHistoryPage, LocalHistoryRow,
                                            LocalHistoryPager, LocalHistoryPageText,
                                            LocalHistoryPrevButton, LocalHistoryNextButton);
    }

    /// <summary>本机记录单行：版本号 + [通道] + 「当前版本」，下面一行首次／最近运行时间。</summary>
    private static StackPanel LocalHistoryRow(Services.VersionRecord record)
    {
        var current = Core.ShellConfig.ShellVersion;
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
        return row;
    }

    // ── 历史版本（回滚） ──────────────────────────────────────

    private void LocalHistoryPrev_Click(object? sender, RoutedEventArgs e)
    {
        _localHistoryPage--;
        LoadLocalHistory();
    }

    private void LocalHistoryNext_Click(object? sender, RoutedEventArgs e)
    {
        _localHistoryPage++;
        LoadLocalHistory();
    }

    private void HistoryPrev_Click(object? sender, RoutedEventArgs e)
    {
        _historyPage--;
        RenderHistoryPage();
    }

    private void HistoryNext_Click(object? sender, RoutedEventArgs e)
    {
        _historyPage++;
        RenderHistoryPage();
    }

    /// <summary>
    /// 画「从仓库装回旧版本」的**当前页**。翻页也走这里，用的是 <see cref="_historyReleases"/> 那份缓存，
    /// ⛔不会再打一次网络（GitHub 那步慢且易限流，点一下「下一页」就重拉一次体验很糟）。
    /// </summary>
    private void RenderHistoryPage()
    {
        var releases = _historyReleases;

        HistoryList.Children.Clear();

        if (releases is null || releases.Count == 0)
        {
            HistoryPager.IsVisible = false;
            HistoryList.Children.Add(new TextBlock
            {
                Text = _updater.Source.IsConfigured ? "没查到版本（仓库里还没发过 Release）。" : "还没配置更新仓库地址。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        _historyPage = RenderPagedList(HistoryList, releases, _historyPage, HistoryRow,
                                       HistoryPager, HistoryPageText, HistoryPrevButton, HistoryNextButton);

        // 尾注不属于数据行：正式版用户看不到预览版，说清楚免得以为列表缺斤少两。
        // ⚠️ 必须排在 RenderPagedList 之后 —— 它开头会 Clear 一次。
        if (_historyNote is { Length: > 0 })
        {
            HistoryList.Children.Add(new TextBlock
            {
                Text = _historyNote,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private async void LoadHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        LoadHistoryButton.IsEnabled = false;
        HistoryRing.IsVisible = true;
        HistoryRing.IsActive = true;
        HistoryList.Children.Clear();
        HistoryPager.IsVisible = false;

        try
        {
            var rollbackChannel = UpdateChannels.Parse(App.Settings.Current.UpdateChannel);

            _historyNote = rollbackChannel == UpdateChannel.Stable
                ? "当前是稳定通道，只列正式版；想看 Insider 版（Beta）就先在上面把更新通道切成 Insider 再加载。"
                : null;

            _historyReleases = await _updater.GetHistoryAsync(rollbackChannel, 20);
            _historyPage = 1;                     // 重新加载＝回第一页
            RenderHistoryPage();
        }
        catch (Exception ex)
        {
            _historyReleases = null;
            _historyNote = null;
            HistoryPager.IsVisible = false;
            HistoryList.Children.Clear();
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

    // ── 安装包自动清理（updates 目录） ─────────────────────────

    /// <summary>保留数量改动：存档 + 后台立刻清一轮（不用等下次启动），列表若摊开着就跟着刷新。</summary>
    private void KeepCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)    {
        if (_loading) return;
        if (KeepCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var keep)) return;

        App.Settings.Current.InstallerKeepCount = Math.Clamp(keep, 1, 10);
        App.Settings.Save();

        var target = App.Settings.Current.InstallerKeepCount;
        // 待安装的包不参与「保留 N 个」的淘汰（它在 UI 线程上读好再传，清理线程不再碰设置）
        var pending = App.Settings.Current.UpdatePendingPath ?? "";
        _ = Task.Run(() =>
        {
            try { InstallerCleanup.Clean(target, pending); }
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

    // ── 下载后动作（2026-10-18 自上游 1.2.0 移植） ───────────────────

    /// <summary>「安装或解压完成后自动删除安装包或压缩包」。</summary>
    private void AutoDeletePackage_IsCheckedChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AutoDeletePackage = AutoDeletePackageSwitch.IsChecked == true;
        App.Settings.Save();
    }

    /// <summary>「解压完成后自动打开所在文件夹」。</summary>
    private void AutoOpenExtractFolder_IsCheckedChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AutoOpenExtractFolder = AutoOpenExtractFolderSwitch.IsChecked == true;
        App.Settings.Save();
    }

    /// <summary>「本地安装包」展开时现读目录（平时折叠，不占加载时间）。</summary>
    private void RefreshInstallerList()
    {
        InstallerDirText.Text = $"存放位置：{InstallerCleanup.InstallerDirectory}";

        var files = InstallerCleanup.Scan();
        if (files.Count == 0)
        {
            InstallerList.Children.Clear();
            InstallerPager.IsVisible = false;
            InstallerList.Children.Add(new TextBlock
            {
                Text = "本地暂无安装包。更新完成后会自动存放于此。",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        _installerPage = RenderPagedList(InstallerList, files, _installerPage, BuildInstallerRow,
                                         InstallerPager, InstallerPageText,
                                         InstallerPrevButton, InstallerNextButton);
    }

    private void InstallerPrev_Click(object? sender, RoutedEventArgs e)
    {
        _installerPage--;
        RefreshInstallerList();
    }

    private void InstallerNext_Click(object? sender, RoutedEventArgs e)
    {
        _installerPage++;
        RefreshInstallerList();
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

    /// <summary>
    /// 把 <paramref name="items"/> 的第 <paramref name="page"/> 页填进 <paramref name="host"/>，并同步分页栏。
    /// 「本地缓存」「本地日志」「本地安装包」「本机版本记录」「从仓库装回旧版本」结构一模一样，
    /// 就这一份逻辑共用。
    ///
    /// ⚠️ 返回值是**夹紧后**的页码，调用方必须写回自己的字段：删掉最后一页最后一条时页码会超界，
    ///    不写回的话下一轮刷新会从空页算起。
    /// ⚠️ 条数不超一页时整条分页栏收起 —— 只有两三条也顶一排翻页按钮太闹。
    /// </summary>
    private static int RenderPagedList<T>(
        StackPanel host,
        IReadOnlyList<T> items,
        int page,
        Func<T, Control> buildRow,
        StackPanel pager,
        TextBlock pageText,
        Button prev,
        Button next)
    {
        var pageCount = Math.Max(1, (items.Count + ListPageSize - 1) / ListPageSize);
        page = Math.Clamp(page, 1, pageCount);

        host.Children.Clear();
        var from = (page - 1) * ListPageSize;
        var to = Math.Min(items.Count, from + ListPageSize);
        for (var i = from; i < to; i++) host.Children.Add(buildRow(items[i]));

        pager.IsVisible = items.Count > ListPageSize;
        pageText.Text = $"第 {page} / {pageCount} 页　·　共 {items.Count} 条";
        prev.IsEnabled = page > 1;
        next.IsEnabled = page < pageCount;
        return page;
    }

    // ── 会话日志（logs 目录，按启动时间分目录，见 LogHome） ─────────────────────────

    /// <summary>「日志保留天数」下拉的索引：3/7/15/30 天 + 永不删除（0）。</summary>
    private static int LogKeepIndex(int days) => days switch
    {
        3 => 0,
        15 => 2,
        30 => 3,
        0 => 4,     // 永不删除
        _ => 1,     // 7 天（默认；存档里的怪值也落这里）
    };

    /// <summary>保留天数改动：存档 + 后台立刻清一轮（不用等下次启动），列表摊开着就跟着刷新。</summary>
    private void LogKeepCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (LogKeepCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var days)) return;

        App.Settings.Current.LogKeepDays = days <= 0 ? 0 : days;
        App.Settings.Save();

        var target = App.Settings.Current.LogKeepDays;
        _ = Task.Run(() =>
        {
            try { Services.LogHome.Clean(target); }
            catch { /* 清理失败不打扰界面，下次启动会再试 */ }
        }).ContinueWith(_ =>
        {
            // ⚠️ 移植：原版 DispatcherQueue.TryEnqueue → Avalonia Dispatcher.UIThread.Post。
            Dispatcher.UIThread.Post(() =>
            {
                if (LogExpander.IsExpanded) RefreshLogList();
            });
        });
    }

    private void LogPrev_Click(object? sender, RoutedEventArgs e)
    {
        _logPage--;
        RefreshLogList();
    }

    private void LogNext_Click(object? sender, RoutedEventArgs e)
    {
        _logPage++;
        RefreshLogList();
    }

    private void OpenLogDir_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.LogHome.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.LogHome.Root}\"") { UseShellExecute = true });
        }
        catch { /* 打不开就算了，本来只是给个方便 */ }
    }

    private void RefreshLogList()
    {
        var items = Services.LogHome.Scan();
        var (bytes, files) = Services.LogHome.Measure();

        LogDirText.Text = $"存放位置：{Services.LogHome.Root}";
        LogSummaryText.Text = items.Count == 0
            ? "还没有日志目录（应用启动时会自动建立）。"
            : $"共 {items.Count} 次运行 · {FormatSize(bytes)} · {files} 个文件";

        _logPage = RenderPagedList(LogList, items, _logPage, BuildLogRow,
                                   LogPager, LogPageText, LogPrevButton, LogNextButton);
    }

    /// <summary>单行：左边会话名 + 占用/时间，右边「打开」「删除」（当前正在运行的那次不给删）。</summary>
    private StackPanel BuildLogRow(Services.LogSessionInfo s)
    {
        var title = s.IsLegacy
            ? "升级前的历史日志"
            : $"{s.StartedLocal:yyyy-MM-dd HH:mm:ss} 那次运行";

        var meta = $"{FormatSize(s.Bytes)}　·　{s.FileCount} 个文件"
                   + (s.IsLegacy ? "　·　旧版按模块分的日志，已归档" : "")
                   + (s.IsCurrent ? "　·　当前正在运行" : "");

        return BuildStorageRow(title, meta, s.FullPath, s.IsCurrent,
                               "在文件资源管理器中打开这次运行的日志目录。",
                               "当前正在运行的那份日志，不能删除。",
                               "删掉这次运行留下的全部日志。",
                               () => DeleteLogSessionAsync(s));
    }

    private async Task DeleteLogSessionAsync(Services.LogSessionInfo s)
    {
        var confirm = new ContentDialog
        {
            Title = "删除日志",
            Content = $"删除「{s.StartedLocal:yyyy-MM-dd HH:mm:ss}」这次运行的日志？\n\n"
                      + $"{FormatSize(s.Bytes)}、{s.FileCount} 个文件，删掉就找不回来了。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        // ⚠️ 原版带 XamlRoot；FA 的 ContentDialog 收 TopLevel（与本页其它确认框同一写法）。
        var top = TopLevel.GetTopLevel(this);
        var result = top is null ? await confirm.ShowAsync() : await confirm.ShowAsync(top);
        if (result != ContentDialogResult.Primary) return;

        Services.LogHome.TryDelete(s.Name);
        RefreshLogList();
    }

    // ── 素材缓存（cache 目录，按版本分目录，见 CacheHome） ─────────────────────────

    /// <summary>保留数量改动：存档 + 后台立刻清一轮（不用等下次启动），列表摊开着就跟着刷新。</summary>
    private void CacheKeepCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CacheKeepCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var keep)) return;

        App.Settings.Current.CacheKeepCount = Math.Clamp(keep, 1, 10);
        App.Settings.Save();

        var target = App.Settings.Current.CacheKeepCount;
        _ = Task.Run(() =>
        {
            try { Services.CacheHome.Clean(target); }
            catch { /* 清理失败不打扰界面，下次启动会再试 */ }
        }).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (CacheExpander.IsExpanded) RefreshCacheList();
            });
        });
    }

    private void CachePrev_Click(object? sender, RoutedEventArgs e)
    {
        _cachePage--;
        RefreshCacheList();
    }

    private void CacheNext_Click(object? sender, RoutedEventArgs e)
    {
        _cachePage++;
        RefreshCacheList();
    }

    private void OpenCacheDir_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.CacheHome.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.CacheHome.Root}\"") { UseShellExecute = true });
        }
        catch { /* 打不开就算了，本来只是给个方便 */ }
    }

    private void RefreshCacheList()
    {
        var items = Services.CacheHome.Scan();
        var (bytes, files) = Services.CacheHome.Measure();

        CacheDirText.Text = $"存放位置：{Services.CacheHome.Root}";
        CacheSummaryText.Text = items.Count == 0
            ? "还没有版本缓存目录（用到素材时会自动建立）。"
            : $"共 {items.Count} 个版本目录 · {FormatSize(bytes)} · {files} 个文件";

        _cachePage = RenderPagedList(CacheList, items, _cachePage, BuildCacheRow,
                                     CachePager, CachePageText, CachePrevButton, CacheNextButton);
    }

    /// <summary>单行：左边目录名 + 占用/时间，右边「打开」「删除」（当前正在用的那个不给删）。</summary>
    private StackPanel BuildCacheRow(Services.CacheInstanceInfo inst)
    {
        var meta = $"{FormatSize(inst.Bytes)}　·　{inst.FileCount} 个文件　·　建立于 {inst.CreatedLocal:yyyy-MM-dd HH:mm}"
                   + (inst.IsCurrent ? "　·　当前正在使用" : "");

        return BuildStorageRow(inst.Name, meta, inst.FullPath, inst.IsCurrent,
                               "在文件资源管理器中打开这个缓存目录。",
                               "当前正在使用的版本目录，不能删除。",
                               "删掉这个版本的素材缓存；素材在下次用到时会自动重新解包。",
                               () => DeleteCacheInstanceAsync(inst));
    }

    private async Task DeleteCacheInstanceAsync(Services.CacheInstanceInfo inst)
    {
        var confirm = new ContentDialog
        {
            Title = "删除缓存目录",
            Content = $"删除 {inst.Name}？\n\n里面的素材在下次用到时会自动重新解包，不影响设置与任何数据。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        var top = TopLevel.GetTopLevel(this);
        var result = top is null ? await confirm.ShowAsync() : await confirm.ShowAsync(top);
        if (result != ContentDialogResult.Primary) return;

        Services.CacheHome.TryDelete(inst.Name);
        RefreshCacheList();
    }

    /// <summary>
    /// 「本地缓存」与「本地日志」的行长得一模一样（左边两行字，右边打开/删除），差别只有文案与
    /// 删除回调 ⇒ 抽成一份，别写两遍。
    /// ⚠️ 用 Grid 排「左伸缩 + 右动作」：Avalonia 的 Grid **没有 ColumnSpacing**，
    ///    右列的 12 用左列的右 Margin 模拟（与 HistoryRow / BuildInstallerRow 同一手法）。
    /// </summary>
    private static StackPanel BuildStorageRow(
        string title, string meta, string fullPath, bool isCurrent,
        string openTip, string currentDeleteTip, string deleteTip,
        Func<Task> delete)
    {
        var name = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var sub = new TextBlock { Text = meta, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };

        var open = new Button { Content = "打开" };
        ToolTip.SetTip(open, openTip);
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true }); }
            catch { /* 打不开就算了 */ }
        };

        var del = new Button { Content = "删除" };
        if (isCurrent)
        {
            del.IsEnabled = false;
            ToolTip.SetTip(del, currentDeleteTip);
        }
        else
        {
            ToolTip.SetTip(del, deleteTip);
            del.Click += async (_, _) => await delete();
        }

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        actions.Children.Add(open);
        actions.Children.Add(del);

        var left = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(name);
        left.Children.Add(sub);

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(left);
        grid.Children.Add(actions);

        var host = new StackPanel { Spacing = 0 };
        host.Children.Add(grid);
        return host;
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

    // ── 教室喊话（2026-10-13 已独立成「内置工具 → 教室喊话」页，这里只留跳转）──────

    private void AnnouncePageJump_Click(object? sender, RoutedEventArgs e)
    {
        App.MainWindow?.Shell.NavigateTo("announce");
    }

    // ── 法律条款（关于 → 用户协议 / 隐私政策 / 免责声明） ──────────

    private void OpenLegal_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not SettingsCard card || card.Tag is not string key) return;
        App.MainWindow?.Shell.NavigateToLegal(key);
    }
}
