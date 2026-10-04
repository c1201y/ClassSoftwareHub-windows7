using System;
using System.Collections.Generic;
using Avalonia.Controls;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 窗口四角圆角 —— 走 **<c>SetWindowRgn</c> 窗口区域裁剪**，这是 Win7 / Win8 / Win10 上唯一可行的路线。
///
/// <para>
/// ── 为什么不能只靠 <see cref="WindowChrome.SetRounded"/> ───────────────────────
/// 那条路是 <c>DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)</c>，**只有 Win11 认**。
/// Win7/8/10 上调它只会返回 <c>E_INVALIDARG</c>，四角纹丝不动 ——
/// 这正是"浮窗展开没有圆角、贴边没有圆角"的根因：代码一直在调，只是那台机器根本不听。
/// </para>
///
/// <para>
/// ── 裁剪是怎么做的 ───────────────────────────────────────────────────────────
/// <c>CreateRoundRectRgn(0, 0, w, h, 2r, 2r)</c> 造一个圆角矩形区域，再 <c>SetWindowRgn</c>
/// 把窗口形状换成它。**区域之外的像素既不绘制、也不参与命中测试** ——
/// 所以四角天然露出底下的桌面，不需要任何透明或合成支持（DWM 关掉也照样成立）。
/// </para>
///
/// <para>
/// ⚠️ 三个必须记牢的坑（都写进代码了，改前先看清楚）：
/// <list type="number">
///   <item><b>right / bottom 是坐标不是宽高</b>，想要 w×h 就传 <c>(0, 0, w, h)</c>；</item>
///   <item><b>ellipseWidth / ellipseHeight 是圆角椭圆的直径</b>，也就是 <c>2 × 半径</c>；</item>
///   <item><b>SetWindowRgn 成功后区域归系统所有</b>，绝不能 DeleteObject
///         （只有设置失败时区域还在我们手上，那时才要自己释放）。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ <b>为什么 Win11 上要躲开</b>（<see cref="Enabled"/>）：
/// Win11 的 DWM 圆角带抗锯齿，并且和系统阴影、Aero 吸附是配套的；
/// 到了 Win11 还去抢着设区域，等于把系统那套换成硬边，是**倒退**。
/// </para>
///
/// <para>
/// ⚠️ 两种用法（都给了）：
/// <list type="bullet">
///   <item><b>一次性</b>：<see cref="Apply"/> / <see cref="Clear"/> —— 调用方自己知道什么时候尺寸变了；</item>
///   <item><b>托管</b>：<see cref="Attach"/> 拿一个实例挂到窗口上，<c>Resized</c> 自动重算
///         （宽高随内容变的浮窗用它，见 <c>Views.FlyoutChrome</c>）。</item>
/// </list>
/// 两种入口共用同一份「上次生效形状」缓存，尺寸/半径没变就直接返回 ——
/// 所以动画期间那种每帧回调不会产生多余的 GDI 对象，也不会闪。
/// </para>
/// </summary>
public sealed class RoundedCorners
{
    /// <summary>默认圆角半径（dip）。8 是 Win11 原生窗口的圆角，也是原版 WinUI 的 <c>OverlayCornerRadius</c>。</summary>
    public const double DefaultCornerRadiusDip = 8;

    /// <inheritdoc cref="DefaultCornerRadiusDip" />
    public const double DefaultRadiusDip = DefaultCornerRadiusDip;

    /// <summary>LOGPIXELSX：取设备每英寸逻辑像素数（96 = 100% 缩放）。</summary>
    private const int LogPixelsX = 88;

    /// <summary>
    /// 这门手艺只在**没有 DWM 原生圆角**的系统上开：Win7 / Win8 / Win10。
    /// Win11 走它自己的 DWM 圆角，我们不插手。
    /// <c>--simulate-win7</c> 会把 <see cref="OsInfo.SupportsWindowRounding"/> 压成 false，
    /// 所以本机也能走这条分支做验证。
    /// </summary>
    public static bool Enabled => !OsInfo.SupportsWindowRounding;

    /// <summary>每个窗口上次生效的（半径, 宽, 高）。用来挡住重复设置 —— 重复 SetWindowRgn 会闪。</summary>
    private static readonly Dictionary<IntPtr, Entry> Applied = new();

    /// <summary>
    /// 已经记过日志的窗口。真机上排查时最想知道"到底裁上没裁上"，
    /// 但拖拽缩放期间尺寸每帧都在变，逐次记会把 port.log 刷爆 → 每个窗口只记第一条。
    /// </summary>
    private static readonly HashSet<IntPtr> Logged = new();

    private readonly Window _window;
    private readonly double _radiusDip;
    private bool _attached;

    private readonly struct Entry
    {
        public readonly int Radius;
        public readonly int Width;
        public readonly int Height;

        public Entry(int radius, int width, int height)
        {
            Radius = radius;
            Width = width;
            Height = height;
        }

        public bool Matches(int radius, int width, int height)
            => Radius == radius && Width == width && Height == height;
    }

    private RoundedCorners(Window window, double radiusDip)
    {
        _window = window;
        _radiusDip = radiusDip;
    }

    // ══════════════════════════════════════════════════════════════════
    //  托管用法：挂到窗口上，尺寸一变自动重算
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把圆角托管给这个窗口：立刻裁一次，之后窗口 <c>Resized</c> 会自动重算。
    /// <b>必须在窗口 Show() 之后调用</b>（HWND 还不存在时无从下手）。
    /// </summary>
    public static RoundedCorners Attach(Window window, double radiusDip = DefaultCornerRadiusDip)
    {
        var corners = new RoundedCorners(window, radiusDip);
        if (window is not null)
        {
            window.Resized += corners.OnResized;
            corners._attached = true;
        }
        corners.Refresh();
        return corners;
    }

    /// <summary>摘掉自动重算（窗口要销毁、或者改走别的方案时用）。</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        try { _window.Resized -= OnResized; } catch { }
    }

    /// <summary>按窗口**当前**的实测尺寸重裁一次。尺寸没变就是空转。</summary>
    public void Refresh()
    {
        try
        {
            var hwnd = Backdrop.TryGetHwnd(_window);
            if (hwnd == IntPtr.Zero) return;

            var scale = _window.RenderScaling > 0 ? _window.RenderScaling : 0;
            Apply(hwnd, _radiusDip, scale);
        }
        catch
        {
            // 拿不到句柄 / 尺寸：等下一次 Resized 或下一次显式 Refresh
        }
    }

    /// <summary>恢复成矩形窗口。</summary>
    public void ClearRegion()
    {
        try
        {
            var hwnd = Backdrop.TryGetHwnd(_window);
            if (hwnd != IntPtr.Zero) Clear(hwnd);
        }
        catch { }
    }

    private void OnResized(object? sender, EventArgs e) => Refresh();

    // ══════════════════════════════════════════════════════════════════
    //  一次性用法
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把窗口裁成圆角矩形。已经裁成同样的形状时什么都不做。
    /// </summary>
    /// <param name="hwnd">目标窗口。<see cref="IntPtr.Zero"/> 直接忽略。</param>
    /// <param name="radiusDip">圆角半径（dip）。</param>
    /// <param name="scale">
    /// 显示器缩放（dip → 物理像素）。传 <c>0</c>（默认）时自己问系统 DPI
    /// —— 调用方手里有 <c>Window.RenderScaling</c> 时应尽量传进来，多显示器混合 DPI 下更准。
    /// </param>
    /// <returns>真的设置成功了才回 <c>true</c>（含"本来就已经是这个形状"）。</returns>
    public static bool Apply(IntPtr hwnd, double radiusDip = DefaultCornerRadiusDip, double scale = 0)
    {
        if (!Enabled || hwnd == IntPtr.Zero) return false;

        try
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return false;

            // ⚠️ 无边框窗口的外框 == 客户区，所以整窗矩形拿来当区域矩形正是我们要的
            var width = wr.Width;
            var height = wr.Height;
            if (width <= 0 || height <= 0) return false;

            var s = scale > 0 ? scale : ScaleFrom(hwnd);
            var radius = (int)Math.Round(radiusDip * s);
            if (radius < 1) radius = 1;

            // 半径超过短边一半就不是圆角了（CreateRoundRectRgn 会画成"胶囊"）
            var max = Math.Min(width, height) / 2;
            if (radius > max) radius = max;

            if (Applied.TryGetValue(hwnd, out var last) && last.Matches(radius, width, height))
                return true;                        // 一模一样 —— 别白折腾

            var rgn = NativeMethodsEx.CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2);
            if (rgn == IntPtr.Zero) return false;

            if (NativeMethodsEx.SetWindowRgn(hwnd, rgn, true) == 0)
            {
                // 失败 → 区域没被系统接管，得自己释放，否则就是一注 GDI 泄漏
                NativeMethodsEx.DeleteObject(rgn);
                return false;
            }

            // 成功 → **绝不能** DeleteObject（区域已经归系统了）
            Applied[hwnd] = new Entry(radius, width, height);

            if (Logged.Add(hwnd))
                PortLog.Step($"圆角裁剪: 半径={radius}px 尺寸={width}x{height}（SetWindowRgn 成功，四角已裁）");

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 取消裁剪，恢复成普通矩形窗口
    /// （最大化时必须清掉 —— 贴边的窗口不该削角，否则屏幕四角会露出底下的桌面）。
    /// </summary>
    public static void Clear(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        Applied.Remove(hwnd);
        if (!Enabled) return;

        try
        {
            _ = NativeMethodsEx.SetWindowRgn(hwnd, IntPtr.Zero, true);
            if (Logged.Remove(hwnd))
                PortLog.Step("圆角裁剪: 已清除（恢复矩形窗口）");
        }
        catch { }
    }

    /// <summary>按窗口句柄取句柄来清（只是省得调用方自己取）。</summary>
    public static void Clear(Window window)
    {
        if (window is null) return;
        try { Clear(Backdrop.TryGetHwnd(window)); } catch { }
    }

    /// <summary>
    /// 问系统 DPI。Win7 / Win8 上没有 <c>GetDpiForWindow</c>（Win10 1607+ 才有），
    /// <c>GetDeviceCaps(LOGPIXELSX)</c> 是这些系统上唯一拿得到缩放的办法。
    /// </summary>
    private static double ScaleFrom(IntPtr hwnd)
    {
        var hdc = IntPtr.Zero;
        try
        {
            hdc = NativeMethods.GetDC(hwnd);
            if (hdc == IntPtr.Zero) return 1.0;

            var dpi = NativeMethodsEx.GetDeviceCaps(hdc, LogPixelsX);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
        finally
        {
            if (hdc != IntPtr.Zero)
            {
                try { _ = NativeMethods.ReleaseDC(hwnd, hdc); } catch { }
            }
        }
    }
}
