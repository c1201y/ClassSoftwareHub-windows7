using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 实验性功能总览页：把「还不成熟、先放着试」的功能列一遍。
/// 版式跟内置工具页一致（内容列 960、两列卡片、页脚说明卡），Nick 2026-09-26 要求。
///
/// ⚠️ 往这儿加功能要动三处：① 下面 <see cref="All"/> 加一条；② <c>ShellPage.axaml</c> 的
///    「实验性功能」分组里加一个子项（带同一个 tag）；③ <c>ShellPage.NavigateTagCore</c> 加一个 case。
///    少一处就会出现「卡片点不动」或「点进去没高亮」。
///
/// ⚠️ 移植说明：原版 GridView 的 ItemClick（e.ClickedItem）→ Avalonia 用 Tapped 冒泡 +
///    从 DataContext 取 ExperimentalFeature（与 WelcomePage.QuickGrid_Tapped 同一套写法）；
///    卡片悬停反馈改用 XAML 里的 :pointerover 伪类（见 .axaml 的移植说明），事件删掉。
/// </summary>
public sealed partial class ExperimentalPage : PageBase
{
    private static readonly List<ExperimentalFeature> All = new()
    {
        new ExperimentalFeature
        {
            Id = "machinecheck", Name = "本机核实",
            Desc = "对照 Windows 的已安装程序记录，看清单里的软件本机装没装",
            Glyph = "\uE7F4", Tag = "machinecheck"
        },
        new ExperimentalFeature
        {
            Id = "easiguard", Name = "白板专杀",
            Desc = "到达设定时间点后，结束希沃白板5 后台滞留的残留进程",
            Glyph = "\uEA99", Tag = "easiguard"
        },
        new ExperimentalFeature
        {
            Id = "procguard", Name = "程序专杀",
            Desc = "为指定程序单独设定结束时间点，到点结束其后驻留进程",
            Glyph = "\uE7E8", Tag = "procguard"
        },
        new ExperimentalFeature
        {
            Id = "virtualkeyboard", Name = "虚拟键盘",
            Desc = "自绘触屏键盘：手指点在输入框上自动弹出，外观全部使用系统控件与主题色",
            Glyph = "\uE765", Tag = "virtualkeyboard"
        },
    };

    public ExperimentalPage()
    {
        InitializeComponent();
        FeatureGrid.ItemsSource = All;
    }

    private void FeatureGrid_Tapped(object? sender, TappedEventArgs e)
    {
        // 走导航栏那一套（不是 Frame.Navigate）：左侧「实验性功能」才会跟着高亮
        if ((e.Source as StyledElement)?.DataContext is ExperimentalFeature f && f.Tag.Length > 0)
            App.MainWindow?.Shell.NavigateTo(f.Tag);
    }
}
