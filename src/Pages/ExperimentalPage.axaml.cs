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
        // ⚠️ 上游 dv1.1.3 把「虚拟键盘」这张卡换成了「自动关机」（键盘整体下线）——
        //    本移植版**保留虚拟键盘**（Win7 教室机的既定取舍），所以是**新增**而不是替换。
        new ExperimentalFeature
        {
            Id = "autoshutdown", Name = "自动关机",
            // ⚠️ 图标用日历（E787）而不是电源（E7E8）—— 电源那个已经被「程序专杀」占了，
            //    同一页两张卡撞脸。日历正好对上"按星期排时间表"这件事。
            Desc = "按星期设定关机时间点，到点自动关机（可直接关，也可用系统的滑动关机）",
            Glyph = "\uE787", Tag = "autoshutdown"
        },
        // 日志查看已不算实验功能（Nick 2026-10-02）：入口挪到 设置 → 诊断。

        // ── 上游 1.1.3-insider1.3 ~ 1.2.0-insider1.1 新增（2026-10-18 移植） ──
        // 这四个都是"会动本机状态"的实用功能，上游放在实验性功能分组下，本移植版照齐。
        new ExperimentalFeature
        {
            Id = "uninstall", Name = "应用卸载",
            Desc = "按分类列出本机已安装的软件，调用其自带卸载程序；卸载后可扫描并清理残留（进隔离区，可还原）",
            Glyph = "\uE74D", Tag = "uninstall"
        },
        new ExperimentalFeature
        {
            Id = "appdata", Name = "AppData 管理",
            Desc = "体检 Local / Roaming 下的一级目录体积排行；只对白名单里的缓存目录提供清理（永久删除）",
            Glyph = "\uE8B7", Tag = "appdata"
        },
        new ExperimentalFeature
        {
            Id = "startup", Name = "开机启动项",
            Desc = "列出开机自启项（注册表 Run + 启动文件夹），逐个启用 / 禁用；启用状态与任务管理器同源",
            Glyph = "\uE945", Tag = "startup"
        },
        new ExperimentalFeature
        {
            Id = "systemrepair", Name = "系统修复",
            Desc = "sfc / DISM / chkdsk / netsh 等预设修复任务，执行前二次确认并原样列出命令，运行中可中止",
            Glyph = "\uE9F3", Tag = "systemrepair"
        },
    };

    public ExperimentalPage()
    {
        InitializeComponent();

        // 两列板式（见 .axaml：照搬首页「快速开始」）：偶数序号进左列、奇数进右列。
        // （2026-10-18 自上游 1.2.0 移植）
        var list = All;
        CardColumnLeft.ItemsSource = System.Linq.Enumerable.ToList(
            System.Linq.Enumerable.Where(list, (_, i) => i % 2 == 0));
        CardColumnRight.ItemsSource = System.Linq.Enumerable.ToList(
            System.Linq.Enumerable.Where(list, (_, i) => i % 2 == 1));
    }

    // 卡片自己的悬停反馈（首页同款：卡片整张可点，反馈由 Opacity 给）
    private void Card_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Avalonia.Controls.Border b) b.Opacity = 0.88;
    }

    private void Card_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Avalonia.Controls.Border b) b.Opacity = 1.0;
    }

    private void Card_Tapped(object? sender, TappedEventArgs e)
    {
        // 走导航栏那一套（不是 Frame.Navigate）：左侧「实验性功能」才会跟着高亮
        if ((sender as StyledElement)?.DataContext is ExperimentalFeature f && f.Tag.Length > 0)
            App.MainWindow?.Shell.NavigateTo(f.Tag);
    }
}
