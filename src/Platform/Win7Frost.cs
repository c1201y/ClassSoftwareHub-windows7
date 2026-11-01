using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// Win7 的「亚克力」仿真 —— **静态磨砂**：不抓屏、不开透明窗口、零运行时开销。
///
/// <para>
/// Win7 没有 Win10/11 的 DWM 材质，Avalonia 11 在 Win7 上也拿不到逐像素透明
/// （根因见 <see cref="Backdrop"/> 头部），所以"透出桌面并实时模糊"这条路不存在。
/// 用户要的分层其实只有一句：**导航栏、标题栏是玻璃，内容区不是**。这里用不依赖任何
/// 系统能力的办法把这个分层做出来：
/// </para>
///
/// <list type="number">
///   <item>窗口底铺一层很淡的对角渐变（"材质"），内容区照旧由页面自己的不透明层盖住；</item>
///   <item>标题栏（背景本来就是 null）和导航栏盖**同一张**半透明玻璃罩 ——
///         两处都透出底下那层渐变，看起来是连在一起的一块玻璃，与 Win10 亚克力的分层一致。</item>
/// </list>
///
/// <para>
/// ⚠️ 它不是真的"模糊"：桌面和别的窗口永远不会出现在这块玻璃里（那需要抓屏）。
///    观感上限就是"一层磨砂渐变"，比纯色更像材质，但别当成实时亚克力。
/// </para>
///
/// <para>
/// ⚠️ 只给**主窗口壳层**用（判定依据 = 找得到 <c>TitleBarArea</c>）。工具窗/浮窗没有导航栏，
///    继续走 <see cref="Backdrop"/> 的纯色兜底，免得全局换掉它们的底色。
/// </para>
/// </summary>
internal static class Win7Frost
{
    private const string PaneBackgroundKey = "NavigationViewExpandedPaneBackground";

    private static bool _logged;

    /// <summary>
    /// 给主窗口上静态磨砂。返回 false = 没找到壳层（不是主窗口），调用方继续原来的纯色兜底。
    /// </summary>
    public static bool TryApply(Window win, Panel? root)
    {
        // 先找挂点：标题栏只在 ShellPage 里（主窗口壳层）；找不到就不用改这个窗口。
        var titleBar = FindNamed<Panel>(win, "TitleBarArea");
        if (titleBar is null) return false;

        var dark = win.ActualThemeVariant == ThemeVariant.Dark;

        // ① 窗口底 = "材质"。内容区不透明，所以只有标题栏 / 导航栏 / 页面没铺满的边缘看得到它。
        win.Background = BuildMaterial(dark);
        win.TransparencyBackgroundFallback = new SolidColorBrush(dark
            ? Color.FromRgb(0x23, 0x23, 0x27)
            : Color.FromRgb(0xF1, 0xF2, 0xF5));
        if (root is not null) root.Background = null;

        // ② 标题栏 + 导航栏 = 同一张玻璃罩（半透明白），两处连成一块。
        var veil = BuildVeil(dark);
        titleBar.Background = veil;

        // FA 的展开面板默认取 NavigationViewExpandedPaneBackground；
        // 模板是按 key 查表（ThemeResource），控件级资源会先命中，所以放在 Nav.Resources 里即可。
        // ⛔ 只改 Expanded：Compact / Minimal 是浮在页面内容之上的，做成半透明会让页面从面板里透出来。
        var nav = FindNamed<Control>(win, "Nav");
        if (nav is not null) nav.Resources[PaneBackgroundKey] = veil;

        if (!_logged)
        {
            _logged = true;
            PortLog.Step(
                "材质: Win7 静态磨砂（渐变底 + 标题栏/导航栏玻璃罩，不抓屏）" +
                (nav is not null ? "" : "；⚠️ 未找到 Nav，导航栏保持默认"));
        }

        return true;
    }

    /// <summary>退回纯色前把磨砂留下的东西清掉（否则切到「纯色」后标题栏还比底白一块）。</summary>
    public static void Reset(Window win)
    {
        var titleBar = FindNamed<Panel>(win, "TitleBarArea");
        if (titleBar is not null) titleBar.Background = null;

        var nav = FindNamed<Control>(win, "Nav");
        nav?.Resources.Remove(PaneBackgroundKey);
    }

    /// <summary>窗口材质：很淡的对角渐变，无 alpha（Win7 上窗口本身不透明）。</summary>
    private static LinearGradientBrush BuildMaterial(bool dark)
    {
        var brush = Diagonal();
        if (dark)
        {
            brush.GradientStops.Add(new GradientStop(Color.Parse("#2C2C31"), 0));
            brush.GradientStops.Add(new GradientStop(Color.Parse("#242428"), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.Parse("#1C1C20"), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.Parse("#FAFBFC"), 0));
            brush.GradientStops.Add(new GradientStop(Color.Parse("#F1F3F6"), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.Parse("#E8EBF1"), 1));
        }
        return brush;
    }

    /// <summary>玻璃罩：半透明白，跟材质同一方向渐变，避免两处拼出明显的缝。</summary>
    private static LinearGradientBrush BuildVeil(bool dark)
    {
        var brush = Diagonal();
        if (dark)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x0F, 255, 255, 255), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, 255, 255, 255), 1));
        }
        return brush;
    }

    private static LinearGradientBrush Diagonal() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
    };

    /// <summary>按名字在逻辑树里找控件（ShellPage 内的 x:Name 不在窗口的 NameScope 里，不能用 FindControl）。</summary>
    private static T? FindNamed<T>(ILogical root, string name) where T : Control
    {
        foreach (var item in root.GetLogicalDescendants())
        {
            if (item is T control && control.Name == name) return control;
        }
        return null;
    }
}
