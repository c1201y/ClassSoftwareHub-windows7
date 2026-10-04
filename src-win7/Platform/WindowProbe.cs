using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 跨进程探测「本机到底有没有一个看得见的窗口」。
///
/// 为什么专门做这个东西（这是本移植版吃过的最大的一个亏）：
///   单实例判定以前问的是「互斥体还在不在」—— 那只证明**进程还活着**，
///   完全不证明**界面看得见**。目标机（Win7）上真出现过这样的实例：
///   进程从早上 11:40 一直活到晚上，常驻内存只剩 4 MB，主窗口从头到尾没有露过面；
///   而用户点 × 只是把它收进了托盘，托盘图标又不显示 —— 于是用户被**永久锁在门外**：
///   双击多少次都被这个僵尸吃掉，每次都静默退出 3 秒，看起来就是"程序坏了"。
///
/// 唯一能回答"界面到底有没有出来"的，只有去问系统：EnumWindows 数一数。
///
/// ⚠️ 全部走 P/Invoke，不依赖 Avalonia —— 单实例判定发生在 Avalonia 初始化**之前**，
///    那个时候没有任何托管 UI API 可用。
/// </summary>
internal static class WindowProbe
{
    /// <summary>一个顶层窗口的快照。</summary>
    internal readonly struct Info
    {
        public int Pid { get; init; }
        public IntPtr Hwnd { get; init; }
        public string Title { get; init; }
        public string ClassName { get; init; }
        public bool Visible { get; init; }

        /// <summary>有 owner 的是对话框 / 浮窗（侧边栏就属于这类），不能当主窗口。</summary>
        public bool Owned { get; init; }

        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>
        /// 像不像「主窗口」——可见 + 无 owner + 有正常尺寸。
        ///
        /// 为什么要卡尺寸：主窗口和工具侧边栏是**同一个进程**的两个顶层窗口，
        /// 侧边栏只有 40~184 像素宽。如果不卡尺寸，僵尸实例只要把侧边栏显示出来
        /// 就会被误判成"界面是好的"，那就又回到了原点上（实测侧边栏 184x1088）。
        /// </summary>
        public bool LooksLikeMainWindow => Visible && !Owned && Width >= 300 && Height >= 200;

        public string Describe()
            => $"{(Visible ? "可见" : "隐藏")} {Width}x{Height} @({X},{Y})  标题=\"{Title}\"  类={ClassName}";
    }

    /// <summary>列出指定进程拥有的全部顶层窗口。</summary>
    internal static List<Info> ForPids(ICollection<int> pids)
    {
        var list = new List<Info>();
        if (pids is null || pids.Count == 0) return list;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains((int)pid)) return true;

                var r = default(RECT);
                GetWindowRect(hwnd, ref r);

                list.Add(new Info
                {
                    Pid = (int)pid,
                    Hwnd = hwnd,
                    Title = TitleOf(hwnd),
                    ClassName = ClassOf(hwnd),
                    Visible = IsWindowVisible(hwnd),
                    Owned = GetWindow(hwnd, GW_OWNER) != IntPtr.Zero,
                    X = r.Left,
                    Y = r.Top,
                    Width = Math.Max(0, r.Right - r.Left),
                    Height = Math.Max(0, r.Bottom - r.Top),
                });
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败（极端环境）→ 返回空表。调用方按"看不见窗口"处理，是安全的一侧。
        }

        return list;
    }

    /// <summary>这些进程里，有没有哪一个露出了一个像样的主窗口？</summary>
    internal static bool HasVisibleMainWindow(ICollection<int> pids)
    {
        foreach (var w in ForPids(pids))
            if (w.LooksLikeMainWindow) return true;
        return false;
    }

    /// <summary>按进程名找出同名的其它进程（不含自己）。</summary>
    internal static List<int> SiblingPids(string processName)
    {
        var result = new List<int>();
        try
        {
            var me = Environment.ProcessId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                try { if (p.Id != me) result.Add(p.Id); }
                finally { p.Dispose(); }
            }
        }
        catch
        {
            // 权限不足 → 当作没有兄弟进程
        }
        return result;
    }

    /// <summary>同名进程里，有没有谁才刚启动（老机器启动慢，别急着当僵尸杀掉）。</summary>
    internal static bool SiblingStartedWithin(string processName, TimeSpan window)
    {
        try
        {
            var me = Environment.ProcessId;
            var now = DateTime.Now;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
            {
                try
                {
                    if (p.Id != me && now - p.StartTime < window) return true;
                }
                catch { /* 取不到启动时间就当它不是新进程 */ }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 这个窗口在系统侧现在是不是可见的？
    ///
    /// ⚠️ 不能拿 Avalonia 的 <c>Window.IsVisible</c> 代替 —— 外部直接
    /// <c>ShowWindow(SW_HIDE)</c> 时 Avalonia 根本不知道，它的 IsVisible 仍然是 true。
    /// 只有问 Win32 才是权威答案。
    ///
    /// 判断不了时返回 <c>true</c>（"当它没问题"）—— 看门狗不该因为探测失败去乱动窗口。
    /// </summary>
    internal static bool IsShown(IntPtr hwnd)
    {
        try
        {
            return hwnd == IntPtr.Zero || IsWindowVisible(hwnd);
        }
        catch { return true; }
    }

    internal static string TitleOf(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    internal static string ClassOf(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    // ══════════════════════════════════════════════════════════════════
    //  P/Invoke
    // ══════════════════════════════════════════════════════════════════

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
