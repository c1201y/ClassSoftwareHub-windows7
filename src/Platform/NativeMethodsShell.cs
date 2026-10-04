using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <see cref="NativeMethods"/>/<see cref="NativeMethodsEx"/> 的**第二批补充**：托盘图标、全局低级钩子、
/// 以及教学动作需要的那几个零散 user32 导出。
///
/// ⚠️ 为什么另开文件：<see cref="NativeMethods"/> 不是 partial 且移植期要求不修改已就绪的 Platform 文件，
///    没法就地扩展，所以按伴生类的惯例新开一个同命名空间的类。调用方一律走 <c>Platform.NativeMethods*</c>。
///
/// 每个导出仍按 <see cref="NativeMethods"/> 的惯例，在注释里标**最低可用系统**。
/// </summary>
internal static class NativeMethodsShell
{
    // ══════════════════════════════════════════════════════════════════
    //  kernel32 —— 模块句柄（建窗口类 / 装钩子都要它）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 零散补充（教学动作 / 托盘共用）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。投递消息（不改窗口过程里当前正在处理的消息）—— 关窗口靠它发 WM_CLOSE。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>最低 Win2000。往上找祖先窗口（点在某控件上时找整窗）。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    internal const uint GA_ROOTOWNER = 3;

    /// <summary>最低 Win2000。SW_MINIMIZE = 6（<see cref="NativeMethods"/> 里只备了 SW_RESTORE）。</summary>
    internal const int SW_MINIMIZE = 6;

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 窗口类 / 消息窗口（托盘图标拿它做「消息窗口」）
    // ══════════════════════════════════════════════════════════════════

    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    /// <summary>最低 Win2000。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>最低 Win2000。注册一条"广播消息"编号（托盘用 TaskbarCreated 判断资源管理器重启）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string lpString);

    // ══════════════════════════════════════════════════════════════════
    //  shell32 —— 系统托盘（Win7 完整可用；气泡通知在 Win7 由系统决定时长）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 最低 Win2000。挂/改/删托盘图标 + 弹气泡。
    /// ⚠️ Win7 上气泡就是**托盘气泡**（<c>NOTIFYICONDATA</c> 的 <c>uTimeout</c> 字段，实际时长由系统定）；
    ///    Win10+ 才会被转成 Action Center 的 toast。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    /// <summary>
    /// 现代版（V3）布局。⚠️ 偏移处那个 <c>uVersion</c> 与 V2 的 <c>uTimeout</c> 是**同一块联合体**：
    ///   调 NIM_SETVERSION 时它是版本号，弹气泡（NIM_MODIFY + NIF_INFO）时它是超时（毫秒）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        /// <summary>V3 = uVersion，V2 = uTimeout（同一偏移）。</summary>
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    internal const uint NIM_ADD = 0;
    internal const uint NIM_MODIFY = 1;
    internal const uint NIM_DELETE = 2;

    internal const uint NIF_MESSAGE = 0x01;
    internal const uint NIF_ICON = 0x02;
    internal const uint NIF_TIP = 0x04;
    internal const uint NIF_INFO = 0x10;

    internal const uint NIIF_INFO = 0x1;

    /// <summary>用户点了气泡通知本体（回给消息窗口的 lParam）。</summary>
    internal const uint NIN_BALLOONUSERCLICK = 0x0405;

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 原生弹出菜单（托盘右键菜单）
    // ══════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);

    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_NONOTIFY = 0x0080;

    internal const uint MF_STRING = 0x0000;
    internal const uint MF_SEPARATOR = 0x0800;
    internal const uint MF_DEFAULT = 0x1000;

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 全局低级鼠标钩子（触摸监听）
    //  最低 Win2000，Win7 完整可用。⚠️ 32/64 位必须同位数才能注入，本项目只出 x64。
    // ══════════════════════════════════════════════════════════════════

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    internal const int WH_MOUSE_LL = 14;

    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_QUIT = 0x0012;

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 键注入补充常量（<see cref="NativeMethods"/> 里有 KEYUP/UNICODE/SCANCODE）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。方向键 / Del 这些要带它，否则被当成小键盘上的同键码。</summary>
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Point;
    }

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG msg);

    /// <summary>最低 Win2000。给某条线程的消息队列投 WM_QUIT，叫停它的消息循环。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
}
