using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 弹出层（下拉菜单 / 飞出菜单 / 提示气泡）在**深色模式下边框发白**的修复。
///
/// <para>
/// ── 现象 ──────────────────────────────────────────────────────────
/// 设置页那个「跟随系统 / 浅色 / 深色」下拉框展开后，深色面板外面围了一圈**白边**，
/// 面板的圆角处还有一个白色的四分之一圆。浅色模式下看不出来（白底白边）。
/// </para>
///
/// <para>
/// ── 根因 ──────────────────────────────────────────────────────────
/// Avalonia 的 <c>Popup</c> 在 Windows 上是**一个独立的顶层窗口**（<c>PopupRoot</c> /
/// <c>PopupImpl</c>，样式 <c>WS_POPUP</c>）。它的模板里靠
/// <c>TransparencyLevelHint = Transparent</c> 来让四角真正透明 ——
/// 而**逐像素透明度是 DWM 合成才有的**。教室机常见「关闭 Aero / 用基本主题」，
/// 此时弹窗窗口是不透明的：面板的 <c>CornerRadius</c> 之外那圈像素就直接露出窗口自己的底色。
/// 那底色恰好是白的，于是深色模式下一眼就能看见。
/// </para>
///
/// <para>
/// ── 对策（两层，互为兜底）────────────────────────────────────────
/// <list type="number">
///   <item>
///     <b>裁剪</b>：把弹窗窗口的形状按 <c>OverlayCornerRadius</c> 裁成同半径的圆角矩形。
///     区域之外的像素压根不属于这个窗口 —— 露出来的是下面的主窗口内容，不是白。
///     半径直接取 FluentAvalonia 模板用的同一个资源键，所以和内容的圆角**必然对齐**。
///   </item>
///   <item>
///     <b>同色打底</b>：再把 <c>PopupRoot.Background</c> 设成跟弹层同色的**不透明**纯色。
///     万一裁剪没生效（比如半径算不出、或者弹窗比内容大一圈），露出来的也是同色，
///     而不是白。（参考实现就是只靠这一层解决的，这里把它留作兜底。）
///   </item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 只在「弹窗做不了逐像素透明」的环境下动手（见 <see cref="PopupIsOpaque"/>）。
/// Win11 / 开了 Aero 的机器上弹窗本来就是真透明，什么都不用做 —— 保持原样最好。
/// </para>
/// </summary>
internal static class PopupSurfaceFix
{
    private const double FallbackCornerRadiusDip = 8;

    /// <summary>按顺序尝试这几个资源键来取「弹层底色」（要的是不透明纯色）。</summary>
    private static readonly string[] SurfaceColorKeys =
    {
        "ComboBoxDropDownBackground",
        "SolidBackgroundFillColorBaseBrush",
        "CardBackgroundFillColorDefaultBrush",
    };

    /// <summary>深色模式下的兜底底色 —— 跟 FluentAvalonia 的弹层色接近。</summary>
    private static readonly Color DarkFallback = Color.FromRgb(0x24, 0x24, 0x24);
    private static readonly Color LightFallback = Color.FromRgb(0xF9, 0xF9, 0xF9);

    private static bool _installed;

    /// <summary>每个弹窗上次裁到多大（dip），尺寸没变就不重复裁。</summary>
    private static readonly ConditionalWeakTable<PopupRoot, SizeCache> LastBounds = new();

    private sealed class SizeCache
    {
        public double W = -1;
        public double H = -1;
    }

    /// <summary>
    /// 这个环境里弹窗能不能做逐像素透明。<c>true</c> 表示"不能" —— 也就是会露白眼的那种。
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>SimulateWin7</c> 时强制按"不能"处理：教室机是否关掉了 Aero 我们在开发机上
    ///    判断不了，而这条兜底路径正是要在开发机上验证的东西。模拟模式下让它走一边，
    ///    就能用 <c>--simulate-win7</c> 实测（见 PORTING 说明里的排障开关约定）。
    /// </remarks>
    private static bool PopupIsOpaque => OsInfo.SimulateWin7 || !OsInfo.IsDwmCompositionEnabled;

    /// <summary>在 <c>App.OnFrameworkInitializationCompleted</c> 里调一次。</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            // 弹窗一显示就处理一次
            Visual.IsVisibleProperty.Changed.AddClassHandler<PopupRoot, bool>((root, e) =>
            {
                if (e.GetNewValue<bool>()) Apply(root);
            });

            // 下拉菜单每次高度都可能不同 → 尺寸一变要按新尺寸重裁
            Layoutable.BoundsProperty.Changed.AddClassHandler<PopupRoot>((root, _) => Apply(root));
        }
        catch (Exception ex)
        {
            PortLog.Step("弹出层白边修复挂载失败（不影响功能）: " + ex.Message);
        }
    }

    private static void Apply(PopupRoot? root)
    {
        if (root is null || !PopupIsOpaque) return;

        try
        {
            // 尺寸没变就别重复动手 —— Bounds 在一次布局里会变好几次，
            // 每次 SetWindowRgn 都会让窗口重画一遍边界，白干且会闪。
            if (!SizeChanged(root)) return;

            var hwnd = GetHwnd(root);
            if (hwnd == IntPtr.Zero) return;

            var scale = root.RenderScaling > 0 ? root.RenderScaling : 1.0;

            // 圆角半径先取：下面要一起写进日志
            var radiusDip = ResolveCornerRadius(root);

            // ① 同色打底：把"露白"变成"露出同色"。必须是不透明纯色，半透明的顶不住。
            var surface = ResolveSurfaceColor(root);
            if (surface is { } c)
                root.Background = new SolidColorBrush(c);

            // ② 裁剪：按内容同样的圆角半径把窗口形状裁掉四角
            _ = RoundedCorners.Apply(hwnd, radiusDip, scale);

            // 只记第一条：真机上"白边到底还在不在"就看这行 —— 底色/圆角/窗口都在这
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                PortLog.Step($"弹出层: 已同色打底 + 裁剪  底色={surface?.ToString() ?? "(没取到)"}  "
                             + $"圆角={radiusDip:0.#}dip → {Math.Round(radiusDip * scale):0}px  缩放={scale:0.##}");
            }
        }
        catch
        {
            // 弹层外观是"锦上添花"：任何一步失败都保持原样即可，绝不冒泡
        }
    }

    /// <summary>已经记过一条弹层诊断日志了（避免每个下拉框都刷一条）。</summary>
    private static bool _loggedFirst;

    private static bool SizeChanged(PopupRoot root)
    {
        var cache = LastBounds.GetOrCreateValue(root);
        var b = root.Bounds;
        if (Math.Abs(b.Width - cache.W) < 0.5 && Math.Abs(b.Height - cache.H) < 0.5) return false;
        cache.W = b.Width;
        cache.H = b.Height;
        return true;
    }

    private static IntPtr GetHwnd(PopupRoot root)
    {
        try { return root.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// 取「弹层底色」：优先抄 FluentAvalonia 自己在用的那个键，抄不到才用兜底色。
    ///
    /// <para>
    /// ⚠️⚠️ <b>必须把 <c>ActualThemeVariant</c> 传给 <c>TryFindResource</c></b>（2026-10-02 实测定论）。
    ///    不带变体的那个重载走的是 <c>ThemeVariant.Default</c>，**不会去查 ThemeDictionaries** ——
    ///    于是深色主题下它会取回**浅色那套**（实测：`ComboBoxDropDownBackground` → <c>#FFF9F9F9</c>，
    ///    近白），然后被当成"弹层底色"刷到弹窗窗口上。
    ///    本机 200% 缩放下面板正好盖满弹窗，看不出来；换个缩放/字体，弹窗比面板大出那一两个像素时，
    ///    四周就会露出一圈**近白**的边 —— 这正是用户报的"深色下拉菜单还有白边"。
    /// </para>
    /// </summary>
    private static Color? ResolveSurfaceColor(PopupRoot root)
    {
        ThemeVariant variant;
        try { variant = root.ActualThemeVariant; }
        catch { variant = ThemeVariant.Dark; }

        foreach (var key in SurfaceColorKeys)
        {
            try
            {
                if (!root.TryFindResource(key, variant, out var value)) continue;
                if (value is ISolidColorBrush brush && brush.Color.A == 0xFF) return brush.Color;
            }
            catch { }
        }

        // 实在取不到就按当前主题给一个不透明的兜底色
        try
        {
            return variant == ThemeVariant.Dark ? DarkFallback : LightFallback;
        }
        catch
        {
            return DarkFallback;
        }
    }

    /// <summary>
    /// 圆角半径：直接读 FluentAvalonia 模板里用的 <c>OverlayCornerRadius</c>。
    /// ⚠️ 必须跟内容的半径**完全相同** —— 比它小会留一圈白，比它大会啃掉内容。
    /// </summary>
    private static double ResolveCornerRadius(PopupRoot root)
    {
        try
        {
            if (root.TryFindResource("OverlayCornerRadius", out var value))
            {
                if (value is CornerRadius cr)
                    return Math.Max(Math.Max(cr.TopLeft, cr.TopRight), Math.Max(cr.BottomLeft, cr.BottomRight));
                if (value is double d) return d;
            }
        }
        catch { }

        return FallbackCornerRadiusDip;
    }

    /// <summary>排障用：把当前环境判定 + 弹层两个配色键的实际取值写进日志。</summary>
    /// <remarks>
    /// ⚠️ 为什么要连**取值**一起记：真机反馈「深色下菜单四周还有白边」时，能一眼分清是
    ///    「我们设的键没生效（取到的是 FluentAvalonia 的默认中灰）」还是「真的是窗口底色漏出来」。
    ///    这两个键在 <c>App.axaml</c> 的 ThemeDictionaries 里，深色应分别是 #242424 / #3A3A3A。
    /// </remarks>
    public static void LogDecision()
    {
        var kind = PopupIsOpaque ? "不透明（按 Win7 兜底处理：同色打底 + 圆角裁剪）" : "支持逐像素透明（不动它）";
        PortLog.Step("弹出层: 弹窗" + kind);

        try
        {
            var app = Application.Current;
            if (app is null) return;

            PortLog.Step("弹出层配色: 底色=" + Describe(app, "ComboBoxDropDownBackground", app.ActualThemeVariant)
                         + "  边框=" + Describe(app, "ComboBoxDropDownBorderBrush", app.ActualThemeVariant)
                         + "  圆角=" + Describe(app, "OverlayCornerRadius", app.ActualThemeVariant)
                         + $"（当前主题 {app.ActualThemeVariant}）");
        }
        catch
        {
            // 查不到资源不影响功能，只是少一条线索
        }
    }

    /// <summary>
    /// ⚠️ 必须把 <paramref name="variant"/> 传进去：不带变体的那个重载用的是
    /// <c>ThemeVariant.Default</c>，**不会**去查 <c>ThemeDictionaries</c>，
    /// 于是深色主题下也会把浅色那套取回来 —— 用它做判断会得出完全相反的结论（踩过）。
    /// </summary>
    private static string Describe(IResourceHost host, string key, ThemeVariant variant)
    {
        try
        {
            if (!host.TryFindResource(key, variant, out var value) || value is null)
                return $"（{key} 取不到，会用 FluentAvalonia 默认值）";

            return value switch
            {
                ISolidColorBrush b => b.Color.ToString(),
                CornerRadius cr => cr.TopLeft.ToString("0.#"),
                double d => d.ToString("0.#"),
                _ => value.ToString() ?? "?",
            };
        }
        catch
        {
            return "?";
        }
    }
}
