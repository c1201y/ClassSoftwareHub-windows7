using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <see cref="NativeMethods"/> 及各伴生类的**又一批补充**：views-b2 移植需要的零散 user32 / kernel32 导出
/// （迷你计时器响铃 Beep、贴纸窗 / 虚拟键盘窗的 DPI 与窗口句柄查询）。
///
/// 为什么另开文件：<see cref="NativeMethods"/> 不是 partial 且移植期要求不修改已就绪的
///    Platform 文件，所以按伴生类惯例新开一个同命名空间的类。调用方一律走 <c>Platform.NativeMethods*</c>。
///
/// 每个导出仍按 <see cref="NativeMethods"/> 的惯例，在注释里标**最低可用系统**。
/// </summary>
internal static class NativeMethodsUtil
{
    // ══════════════════════════════════════════════════════════════════
    //  kernel32 —— 蜂鸣（课堂计时器到点响铃）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。控制台蜂鸣器（没接蜂鸣器时由声卡模拟）。Win7 完整可用。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Beep(uint dwFreq, uint dwDuration);

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 窗口查询 / 摆位的零散补充
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。句柄还活着吗（虚拟键盘「还前台」前先验一下，别把死句柄喂给 SetForegroundWindow）。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    /// <summary>最低 Win2000。SW_SHOWNOACTIVATE = 4（贴纸窗 / 虚拟键盘「显示但不抢前台」专用）。</summary>
    internal const int SW_SHOWNOACTIVATE = 4;

    /// <summary>最低 Win2000。让窗口立即重画一遍（贴纸窗 NOACTIVATE 显示后调它）。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateWindow(IntPtr hWnd);

    /// <summary>
    /// 窗口所在显示器的 DPI（贴纸窗 1:1 物理像素换算用）。
    /// ⚠️ 最低 **Windows 10 1607**，Win7/8/8.1 的 user32.dll 没有这个导出，直接调用会抛
    ///    <see cref="EntryPointNotFoundException"/>。调用方必须先按 OsInfo 判定系统版本，
    ///    并保留 try/catch 兜底，回落到 RenderScaling 或 GetDeviceCaps(LOGPIXELSX)。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>最低 Win2000。SetWindowPos 的「不许动 Z 序」（贴纸窗改大小/挪位时别把自己压到底下去）。</summary>
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>SetWindowPos 的 hWndInsertAfter：把窗口放到最上层（Z 序，不影响激活）。</summary>
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>GetDpiForWindow 导出探测结果：0=未探测，1=可用，-1=该导出不存在。</summary>
    private static int _dpiForWindowProbe;

    /// <summary>
    /// 取 <c>WM_NCHITTEST</c> 命中判定用的缩放（dip → 物理像素），供自绘标题栏与扩展客户区
    /// 两处共用，把标题栏高度、缩放带宽按**物理像素**换算。
    ///
    /// <para>
    /// ⚠️ 不能用 <c>Window.RenderScaling</c>：它是 Avalonia 按 TopLevel 算的，跨屏拖动
    ///    （150% ↔ 100%）时滞后于 <c>WM_DPICHANGED</c>，会算错边缘与标题栏命中。
    ///    Win10 1607+ 用 <see cref="GetDpiForWindow"/> 按窗口所在屏取 DPI；Win7 / 老系统
    ///    退回 <c>GetDeviceCaps(LOGPIXELSX)</c> 的系统级 DPI（Win7 单系统 DPI，行为不变）。
    /// </para>
    /// <para>
    /// ⛔ <see cref="GetDpiForWindow"/> 是 Win10 1607+ 导出：这里先按
    ///    <see cref="OsInfo.IsWindows10OrLater"/> 做版本判定，再用探测缓存 + try/catch 兜底，
    ///    绝不在 Win7 上抛 <c>EntryPointNotFoundException</c>。
    /// </para>
    /// </summary>
    internal static double GetScaleForHitTest(IntPtr hwnd, double renderScaling)
    {
        var fallback = renderScaling > 0 ? renderScaling : 1.0;

        if (OsInfo.IsWindows10OrLater && _dpiForWindowProbe >= 0)
        {
            try
            {
                var dpi = GetDpiForWindow(hwnd);
                _dpiForWindowProbe = 1;
                if (dpi > 0) return dpi / 96.0;
            }
            catch (DllNotFoundException) { _dpiForWindowProbe = -1; }
            catch (EntryPointNotFoundException) { _dpiForWindowProbe = -1; }
            catch { /* 单次失败不缓存结论，下次再试 */ }
        }

        // Win7 / 兜底：GetDC + GetDeviceCaps(LOGPIXELSX) —— Win2000 起可用，系统级 DPI。
        var hdc = IntPtr.Zero;
        try
        {
            hdc = NativeMethods.GetDC(hwnd);
            if (hdc == IntPtr.Zero) return fallback;
            var dpi = NativeMethodsEx.GetDeviceCaps(hdc, NativeMethodsEx.LOGPIXELSX);
            return dpi > 0 ? dpi / 96.0 : fallback;
        }
        catch
        {
            return fallback;
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
