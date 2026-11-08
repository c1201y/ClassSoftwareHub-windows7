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

    /// <summary>当前显示的是哪一组（导航回高亮时据此区分「课堂工具 / 系统工具」两个父项）。</summary>
    public ToolGroup Group { get; private set; } = ToolGroup.Classroom;

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
        Group = group;

        // ⚠️ 卡片清单与导航子项同源（都走 NavLayout.SecondLevel）：2026-11-04（自上游 1.10 移植）起
        //    用户可以把「查阅单词 / 文档库」收进分组、也可以把常用工具拎到一级，
        //    这里显示的就是"当前确实留在本组二级里"的那些。
        var items = NavLayout.SecondLevel(group).ToList();

        // 两列板式（见 .axaml：照搬首页「快速开始」）：偶数序号进左列、奇数进右列。
        CardColumnLeft.ItemsSource = items.Where((_, i) => i % 2 == 0).ToList();
        CardColumnRight.ItemsSource = items.Where((_, i) => i % 2 == 1).ToList();

        TitleText.Text = group == ToolGroup.System ? "系统工具" : "课堂工具";

        // 全部提到一级之后就剩下两条空列 —— 给一句说明，别让人以为页面坏了
        EmptyHint.IsVisible = items.Count == 0;

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
    /// 2026-11-04（自上游 1.10 移植）：卡片数据源从 <see cref="ToolDef"/> 换成 <see cref="NavPlaceable"/>，
    /// 于是回到上游那条路 —— 走 <c>Shell.NavigateTo(tag)</c>（导航栏那一套，左侧子项跟着高亮）。
    /// ⚠️ NavPlaceable 只有 tag、没有页面类型，载体选择（网页版 / 原生页）在 NavigateTagCore 里统一做，
    ///    这里别再自己查 ToolCatalog，否则两条路的判定会分叉。
    /// </summary>
    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as StyledElement)?.DataContext is NavPlaceable placeable && placeable.Tag.Length > 0)
            App.MainWindow?.Shell.NavigateTo(placeable.Tag);
    }
}
