using System;
using System.Collections.Generic;
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
        ToolGrid.ItemsSource = ToolCatalog.All;

        // 这里的开关（「点关闭时收进托盘」和设置页里是同一个值）；侧边栏/置顶那些挪到「设置 → 常用工具」了
        _loading = true;
        TraySwitch.IsChecked = App.Settings.Current.CloseToTray;
        _loading = false;
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

    /// <summary>
    /// 卡片单击 → 进对应工具页。
    /// ⚠️ 原版 <c>Frame.Navigate(def.Page)</c>（WinUI Page.Frame）；Avalonia 页面没有 Frame 属性，
    ///    沿可视树向上找宿主 <see cref="Frame"/>。
    /// ⚠️ 上游 dv1.1.0 改走导航栏那一套（<c>App.MainWindow?.Shell.NavigateTo(def.Id)</c>，
    ///    让左侧「内置工具」对应子项跟着高亮）—— 那是上游 ShellPage 按 <c>ToolCatalog.All</c>
    ///    动态生成导航子项、并按 Id 反查后才成立的；本移植版的 ShellPage 暂未接入 ToolCatalog，
    ///    故这里用本仓已有的等价入口 <see cref="ShellPage.NavigateToTool(Type)"/>：
    ///    它同样会高亮「内置工具」并把工具列表铺进返回栈，效果一致。
    /// </summary>
    private void ToolGrid_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is ToolDef def && def.Page is not null)
            App.MainWindow?.Shell.NavigateToTool(def.Page);
    }

    // 卡片自己的悬停反馈由 XAML 里 Class="card" 的 :pointerover 样式给出。
}
