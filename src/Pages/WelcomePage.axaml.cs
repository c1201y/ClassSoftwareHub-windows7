using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Updating;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 首页：大标题 ClassSoftwareHub → 首页大标题（站点大标题）→ 版本图 + 版本号 → 快速开始卡片。
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
        // ⚠️ 与 Populate 分开调：横幅要在**每次进首页**时都复核一遍（Frame 缓存了页面实例，
        //    回到首页只是重新挂上可视树 → Avalonia 会再次触发 Loaded）。
        Loaded += (_, _) => { Populate(); RefreshUpdateReadyBar(); };
    }

    // ── 更新待装横幅 ────────────────────────────────────────────────

    /// <summary>
    /// 后台下载完成 → 存档里有待装标记 → 顶部 InfoBar 提醒（用户选了「稍后安装」或没点通知都算）。
    /// 装上新版本再进来时，待装 tag 与当前版本一致 → 自检清档；安装包被清理了也顺手清，不摆死横幅。
    ///
    /// ⚠️ 幂等，可以从多处调：本页 <c>Loaded</c>（原版就是这么接的）、ShellPage 的
    ///    <c>ContentFrame.Navigated</c>、以及后台下完更新时的 <c>MainWindow.NotifyUpdateReady</c>。
    /// </summary>
    internal void RefreshUpdateReadyBar()
    {
        var s = App.Settings.Current;
        var path = s.UpdatePendingPath ?? "";

        if (path.Length == 0 || !System.IO.File.Exists(path))
        {
            // 标记悬空（包被清了 / 档是手抄的）：清掉别让它永久挂着
            if (path.Length > 0)
            {
                s.UpdatePendingPath = "";
                s.UpdatePendingTag = "";
                App.Settings.Save();
            }
            UpdateReadyBar.IsOpen = false;
            return;
        }

        // 待装 tag == 当前版本（VersionPrefix + ShellVersion）= 已经装上了，待装周期结束
        if (s.UpdatePendingTag == ShellConfig.VersionPrefix + ShellConfig.ShellVersion)
        {
            s.UpdatePendingPath = "";
            s.UpdatePendingTag = "";
            App.Settings.Save();
            UpdateReadyBar.IsOpen = false;
            return;
        }

        UpdateReadyBar.Title = $"{(s.UpdatePendingTag.Length > 0 ? s.UpdatePendingTag : "新版本")} 已下载就绪";
        UpdateReadyBar.IsOpen = true;
    }

    private async void InstallPending_Click(object? sender, RoutedEventArgs e)
    {
        // 防连点：安装会退出应用，多点只会并发起安装器
        if (sender is Button b) b.IsEnabled = false;
        await UpdateFlow.InstallPendingNowAsync();
    }

    private void Populate()
    {
        var ui = App.Content.Ui;

        var title = ui.AppTitle.Length > 0 ? ui.AppTitle : "电教委员常用软件下载站";
        HomeTitleText.Text = title + " • 桌面版";
        // 只显示软件自己的版本（前缀 VersionPrefix + ShellVersion，如正式版 dv1.0.0 或预览版 dv1.0.0-insider1.1）。
        // ⚠️ 别再往这里挂"网站版本"——软件是独立发布物，不摆成网站版本的附属品（2026-09-26 删）。
        ShellVersionText.Text = ShellConfig.VersionPrefix + ShellConfig.ShellVersion;

        LoadBanner();
        BuildQuickInfo();
        BuildQuickLinks();
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
