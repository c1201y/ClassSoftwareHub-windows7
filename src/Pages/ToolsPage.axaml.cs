using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>内置工具索引页：卡片清单来自 <see cref="ToolCatalog.All"/>（与导航子项同一份）。</summary>
public sealed partial class ToolsPage : PageBase
{
    public ToolsPage()
    {
        InitializeComponent();

        // 卡片列表改到 OnNavigatedTo 里填 —— 需要先知道当前是哪一组（2026-10-22 自上游移植）。

        // 这里的开关（「点关闭时收进托盘」和设置页里是同一个值）；侧边栏/置顶那些挪到「设置 → 常用工具」了
        _loading = true;
        TraySwitch.IsChecked = App.Settings.Current.CloseToTray;
        _loading = false;
    }

    /// <summary>
    /// 按导航传进来的参数决定显示哪一组（<c>classroom</c> / <c>system</c>）。
    /// ⚠️ 两个导航父项共用这一个页面：板式、卡片、交互全部一样，只是数据集与标题不同。
    /// 不拆两个页面是为了避免一份板式两处维护。
    /// </summary>
    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);

        var group = string.Equals(parameter as string, "system", StringComparison.OrdinalIgnoreCase)
            ? ToolGroup.System
            : ToolGroup.Classroom;

        var items = ToolCatalog.All.Where(t => t.Group == group).ToList();

        // 两列板式（见 .axaml：照搬首页「快速开始」）：偶数序号进左列、奇数进右列。
        CardColumnLeft.ItemsSource = items.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = items.Where((_, i) => i % 2 == 1).ToList();

        TitleText.Text = group == ToolGroup.System ? "系统工具" : "课堂工具";

        // 「常用工具窗口」那一块讲的是抽号 / 计时 / 时钟，只属于课堂组
        PaletteBlock.IsVisible = group != ToolGroup.System;
    }

    private bool _loading;

    private void OpenPalette_Click(object? sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool();

    private void TraySwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // ⚠️ 原版 ToggleSwitch.IsOn → Avalonia 的 ToggleSwitch.IsChecked（bool?）。
        App.MainWindow?.SetCloseToTray(TraySwitch.IsChecked == true);
    }

    // 卡片自己的悬停反馈（首页同款：卡片整张可点，反馈由 Opacity 给）
    private void Card_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }

    /// <summary>
    /// 卡片整张可点 → 进对应工具页。
    /// ⚠️ 上游 1.2.0 走的是 <c>App.MainWindow?.Shell.NavigateTo(def.Id)</c>（导航栏那套，
    ///    左侧「内置工具」子项会跟着高亮）—— 那要求 ShellPage 按 ToolCatalog 动态生成导航子项；
    ///    本移植版的 ShellPage 暂未接入 ToolCatalog，故这里沿用本仓库已有的等价入口
    ///    <see cref="ShellPage.NavigateToTool(Type)"/>：同样会高亮「内置工具」并把工具列表铺进返回栈。
    /// </summary>
    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as StyledElement)?.DataContext is ToolDef def && def.Page is not null)
            App.MainWindow?.Shell.NavigateToTool(def.Page);
    }
}
