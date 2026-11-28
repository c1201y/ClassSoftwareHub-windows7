using System;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 关掉 **ClassIsland 里那个重复的「课件续页」控制面板**（2026-11-28，用户反馈「俩课件续页重叠」）。
///
/// <para>
/// ── 为什么会有两个 ──────────────────────────────────────────
/// 同一个台账现在有两个入口：
/// ① CSH 主界面「课件库」页（内嵌 <c>ControlPanel</c>，4 页 Tab）；
/// ② ClassIsland 插件自己的 <c>ControlPanel</c> 窗口（标题「课件续页」，3 页 Tab，跑在 CI 进程里）。
/// 老师两边都开着时就是两块几乎一样的界面叠在屏幕上，还各自弹各自的操作。
/// </para>
///
/// <para>
/// ── 处理原则 ────────────────────────────────────────────────
/// 按之前定下的方向「两边都在时默认用 CSH 的」：CSH 一旦打开课件库，就把 CI 里那个面板
/// 用 <c>WM_CLOSE</c> 请它自己关（插件收到关闭会把引用清掉，下次点齿轮能重建，不会坏状态）。
/// 只认**进程名 ClassIsland + 标题以「课件续页」开头**的可见窗口 —— 不碰 PowerPoint、不碰我们自己。
/// </para>
///
/// <para>
/// ⚠️ 这里只做「关窗口」，不杀进程、不发自定义消息：CI 想怎么处理自己决定。
/// </para>
/// </summary>
internal static class CiPanelCloser
{
    private const string PanelTitlePrefix = "课件续页";
    private const string CiProcessName = "ClassIsland";

    /// <summary>把 CI 里开着的「课件续页」面板都请关闭，返回关掉几个。</summary>
    public static int CloseDuplicatePanels()
    {
        var closed = 0;
        try
        {
            PptTouchNative.EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!PptTouchNative.IsWindowVisible(hwnd)) return true;

                    var title = new StringBuilder(256);
                    if (PptTouchNative.GetWindowTextW(hwnd, title, title.Capacity) == 0) return true;
                    var text = title.ToString();
                    if (!text.StartsWith(PanelTitlePrefix, StringComparison.Ordinal)) return true;

                    PptTouchNative.GetWindowThreadProcessId(hwnd, out var pid);
                    if (!IsClassIslandProcess(pid)) return true;

                    if (PptTouchNative.PostMessage(hwnd, PptTouchNative.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                    {
                        closed++;
                        AppLog.Info("classisland",
                            "已请 ClassIsland 关闭重复的「课件续页」面板（改用 CSH 的课件库）：" + text);
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Catch("classisland", "关闭 ClassIsland 重复面板", ex, LogLevel.Debug);
        }

        return closed;
    }

    private static bool IsClassIslandProcess(uint pid)
    {
        if (pid == 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, CiProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
