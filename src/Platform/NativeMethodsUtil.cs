using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <see cref="NativeMethods"/> 及各伴生类的**又一批补充**：views-b2 移植需要的零散 user32 / kernel32 导出
/// （迷你计时器响铃 Beep、贴纸窗 / 虚拟键盘窗的 DPI 与窗口句柄查询）。
///
/// ⚠️ 为什么另开文件：<see cref="NativeMethods"/> 不是 partial 且移植期要求不修改已就绪的
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

    /// <summary>最低 Win2000。窗口所在显示器的 DPI（贴纸窗 1:1 物理像素换算用）。</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>最低 Win2000。SetWindowPos 的「不许动 Z 序」（贴纸窗改大小/挪位时别把自己压到底下去）。</summary>
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>SetWindowPos 的 hWndInsertAfter：把窗口放到最上层（Z 序，不影响激活）。</summary>
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
}
