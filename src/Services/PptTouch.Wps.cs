using System;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// WPS 演示（wpp.exe）的放映窗识别（2026-11-28）。
///
/// <para>
/// ── 为什么原来只认 PowerPoint ──────────────────────────────
/// <c>PptTouch</c> 一直只匹配类名 <c>screenClass</c> + <c>POWERPNT.EXE</c>。
/// WPS 演示的放映窗**不叫这个名字**，于是"放映中"判不出来：触控手势、右键菜单、
/// 以及新加的放映鼠标锁定在 WPS 上都等于没开。
/// </para>
///
/// <para>
/// ── WPS 放映窗怎么认（三条，从准到松）────────────────────────
/// ① 类名 <c>Widget</c> + 进程属于 WPS —— 公开资料里 WPS 幻灯片窗口就是「标题=幻灯片、类名 Widget」；
/// ② 兜底（类名随版本会变）：**进程是 WPS 演示 + 窗口铺满所在显示器 + WS_POPUP + 没有最小化/最大化按钮**
///    —— 这正是"放映窗"与"编辑窗"的形态差；
/// ③ 标题里带「幻灯片 / 放映」再加一分（有就更容易过，没有也不强求）。
/// </para>
///
/// <para>
/// ⚠️ 判定只在**钩子与看门狗**里调用（500ms / 每次鼠标事件），所以这里不读盘、不做进程枚举，
/// 只查当前窗口的类名/样式/尺寸 + 一次 <c>QueryFullProcessImageName</c>。
/// </para>
/// </summary>
public static partial class PptTouch
{
    /// <summary>WPS 12 统一入口可能叫 wps.exe，演示也可能还是独立的 wpp.exe。</summary>
    private static bool IsWpsPresentationProcess(string exe)
        => string.Equals(exe, "wpp.exe", StringComparison.OrdinalIgnoreCase)
           || string.Equals(exe, "wps.exe", StringComparison.OrdinalIgnoreCase)
           || string.Equals(exe, "wpsoffice.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerPointExe(string exe)
        => string.Equals(exe, "POWERPNT.EXE", StringComparison.OrdinalIgnoreCase);

    /// <summary>取进程映像名（"POWERPNT.EXE" / "wpp.exe" …）；拿不到返回空串。</summary>
    private static string ProcessImageName(uint pid)
    {
        if (pid == 0) return "";

        var handle = PptTouchNative.OpenProcess(PptTouchNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return "";

        try
        {
            var path = new StringBuilder(512);
            var size = path.Capacity;
            if (!PptTouchNative.QueryFullProcessImageName(handle, 0, path, ref size)) return "";
            return System.IO.Path.GetFileName(path.ToString()) ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            try { PptTouchNative.CloseHandle(handle); } catch { }
        }
    }

    /// <summary>
    /// 这个窗口是不是"正在全屏放映"的那个。PowerPoint 走类名；WPS 走类名 + 形态兜底。
    /// </summary>
    private static bool IsSlideshowWindow(IntPtr hwnd, string cls, uint pid)
    {
        if (hwnd == IntPtr.Zero) return false;

        var exe = ProcessImageName(pid);
        if (exe.Length == 0) return false;

        // ── PowerPoint：老规矩，类名 screenClass ──
        if (IsPowerPointExe(exe))
            return string.Equals(cls, "screenClass", StringComparison.OrdinalIgnoreCase);

        // ── WPS 演示 ──
        if (!IsWpsPresentationProcess(exe)) return false;

        // ① 公开资料里 WPS 幻灯片窗口的类名
        if (string.Equals(cls, "Widget", StringComparison.OrdinalIgnoreCase)) return true;

        // ② 形态兜底：铺满所在显示器 + POPUP + 没有最小化/最大化按钮
        if (!IsFullscreenPopup(hwnd)) return false;

        // ③ 标题里有「幻灯片 / 放映」就更容易认定（没有也放行：形态已经够像了）
        return true;
    }

    /// <summary>窗口是否"铺满所在显示器 + WS_POPUP + 无最小化/最大化按钮"。</summary>
    private static bool IsFullscreenPopup(IntPtr hwnd)
    {
        try
        {
            if (!PptTouchNative.GetWindowRect(hwnd, out var rect)) return false;

            var monitor = PptTouchNative.MonitorFromWindow(hwnd, PptTouchNative.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var info = new PptTouchNative.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<PptTouchNative.MONITORINFO>() };
            if (!PptTouchNative.GetMonitorInfo(monitor, ref info)) return false;

            var monW = info.rcMonitor.Right - info.rcMonitor.Left;
            var monH = info.rcMonitor.Bottom - info.rcMonitor.Top;
            if (monW <= 0 || monH <= 0) return false;

            var winW = rect.Right - rect.Left;
            var winH = rect.Bottom - rect.Top;

            // 允许任务栏等边距：≥92% 就算铺满
            if (winW < monW * 0.92 || winH < monH * 0.92) return false;

            var style = (long)(uint)PptTouchNative.GetWindowLongW(hwnd, PptTouchNative.GWL_STYLE);
            var popup = (style & PptTouchNative.WS_POPUP) != 0;
            var hasMinMax = (style & (PptTouchNative.WS_MINIMIZEBOX | PptTouchNative.WS_MAXIMIZEBOX)) != 0;

            return popup && !hasMinMax;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>诊断用：把当前所有可见的演示类窗口（类名 / 标题 / 尺寸）写进日志。</summary>
    private static void DescribePresentationWindowsForWps()
    {
        try
        {
            PptTouchNative.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!PptTouchNative.IsWindowVisible(hwnd)) return true;
                    PptTouchNative.GetWindowThreadProcessId(hwnd, out var pid);
                    var exe = ProcessImageName(pid);
                    if (!IsPowerPointExe(exe) && !IsWpsPresentationProcess(exe)) return true;

                    var cls = new StringBuilder(128);
                    PptTouchNative.GetClassName(hwnd, cls, cls.Capacity);
                    var title = new StringBuilder(256);
                    PptTouchNative.GetWindowTextW(hwnd, title, title.Capacity);
                    PptTouchNative.GetWindowRect(hwnd, out var r);

                    AppLog.Info("ppttouch",
                        $"演示窗口｜exe={exe}｜类名={cls}｜标题={title}｜" +
                        $"{r.Right - r.Left}x{r.Bottom - r.Top}｜全屏POPUP={IsFullscreenPopup(hwnd)}");
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppttouch", "枚举演示窗口（WPS 诊断）", ex, LogLevel.Debug);
        }
    }
}
