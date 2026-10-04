using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Pages.Tools;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>内置工具索引页：只放已经在桌面版做完的工具（其余先不上）。</summary>
public sealed partial class ToolsPage : PageBase
{
    private static readonly List<ToolDef> All = new()
    {
        new ToolDef
        {
            Id = "image-color", Name = "图片取色",
            Desc = "导入图片后自动提取一组柔和的配色（种子色 + 7 档明暗变体），单击色块即可复制。",
            Glyph = "\uE790", Page = typeof(ImageColorToolPage)
        },
        new ToolDef
        {
            Id = "pick-number", Name = "随机抽号",
            Desc = "按学号范围或班级名单随机抽取，可一次抽多个、抽过不重复，也能一键随机分组。",
            Glyph = "\uE716", Page = typeof(PickNumberToolPage)
        },
        new ToolDef
        {
            Id = "timer", Name = "课堂计时器",
            Desc = "倒计时 / 秒表，大字号便于投影，到达设定时间响铃。",
            Glyph = "\uE916", Page = typeof(TimerToolPage)
        },
        new ToolDef
        {
            Id = "clock", Name = "全屏时钟",
            Desc = "将屏幕变为大型时钟：可设置背景图与蒙版，全屏显示查看时间。",
            Glyph = "\uE740", Page = typeof(ClockToolPage)
        },
        new ToolDef
        {
            Id = "encoding", Name = "编码 / 哈希工具",
            Desc = "校验文件：拖入即得 MD5 / SHA-1 / SHA-256 / SHA-512，可粘贴官方值自动核对；也可做 Base64、URL 编解码。",
            Glyph = "\uE943", Page = typeof(EncodingToolPage)
        },
        new ToolDef
        {
            Id = "mirror-download", Name = "系统镜像下载",
            Desc = "Windows 等系统镜像的官方 / 可信第三方入口，单击任一行即跳转（本站不存储镜像）。",
            Glyph = "\uE896", Page = typeof(MirrorToolPage)
        },
    };

    public ToolsPage()
    {
        InitializeComponent();
        ToolGrid.ItemsSource = All;

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
    /// </summary>
    private void ToolGrid_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is ToolDef def && def.Page is not null)
            this.FindAncestorOfType<Frame>()?.Navigate(def.Page);
    }

    // 卡片自己的悬停反馈由 XAML 里 Class="card" 的 :pointerover 样式给出。
}
