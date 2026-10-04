using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 全程序 P/Invoke 集中处。
///
/// ⚠️ 规矩：**所有** Win32 调用都从这里走，不要在业务代码里散写 DllImport。
///    理由有二：① Win7 上有一半「Win10 才有」的导出，散着写很容易漏掉版本判定；
///    ② 集中一处才好在移植期做「这个 API 在 Win7 上到底有没有」的统一核查。
///
/// 每个导出都在注释里标了**最低可用系统**，调用前必须用 <see cref="OsInfo"/> 判定。
/// </summary>
internal static class NativeMethods
{
    // ══════════════════════════════════════════════════════════════════
    //  kernel32 —— 版本信息
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。⚠️ 不能用 Environment.OSVersion 判断版本：.NET 会在清单里
    /// 把 supportedOS 声明过的系统统一报成 6.2，Win7 会被误判成 Win8。</summary>
    [DllImport("ntdll.dll", SetLastError = true)]
    internal static extern int RtlGetVersion(ref OSVERSIONINFOEX lpVersionInfo);

    [StructLayout(LayoutKind.Sequential)]
    internal struct OSVERSIONINFOEX
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 窗口
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>最低 Win2000。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>最低 Win2000。SW_RESTORE = 9</summary>
    internal const int SW_RESTORE = 9;

    /// <summary>最低 Win2000。给非前台进程抢焦点的通行证，配合 SetForegroundWindow 用。</summary>
    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    /// <summary>最低 Win2000。拖拽窗口用（WM_NCLBUTTONDOWN + HTCAPTION）。</summary>
    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal const uint WM_NCLBUTTONDOWN = 0x00A1;
    internal const int HTCAPTION = 2;

    /// <summary>最低 Win2000。窗口置顶。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    /// <summary>最低 WinVista。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>最低 Win2000。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadImage(IntPtr hinst, IntPtr name, uint type, int cx, int cy, uint load);

    /// <summary>
    /// 最低 Win2000。取系统内置图标（<c>IDI_APPLICATION</c> 等）—— 托盘图标最后的兜底。
    /// ⚠️ 拿回来的是**共享图标**，不要 DestroyIcon。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    /// <summary>系统默认的"应用程序"图标（<c>MAKEINTRESOURCE(32512)</c>，Win2000 起）。</summary>
    internal static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);

    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x0010;
    internal const uint LR_DEFAULTSIZE = 0x0040;
    internal const uint LR_SHARED = 0x8000;

    /// <summary>最低 Win2000。设置窗口图标小图/大图。</summary>
    internal const uint WM_SETICON = 0x0080;
    internal static readonly IntPtr ICON_SMALL = IntPtr.Zero;
    internal static readonly IntPtr ICON_BIG = new(1);

    /// <summary>最低 Win2000。读写窗口样式/扩展样式（无边框、工具窗等）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_APPWINDOW = 0x00040000;

    /// <summary>最低 Win2000。整屏/虚拟屏尺寸。</summary>
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>最低 Win2000。枚举顶层窗口 / 判断可见性（虚拟键盘与截屏用）。</summary>
    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>最低 Win2000。查询/设置窗口矩形。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// 最低 Win2000。**屏幕像素取样** —— 取色器 / 截屏取点用。
    /// ⚠️ Win7 没有 Windows.Graphics.Capture，抓屏一律走 GDI 的 BitBlt（见 Services/ScreenCapture）。
    /// </summary>
    [DllImport("gdi32.dll")]
    internal static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // ══════════════════════════════════════════════════════════════════
    //  gdi32 —— 抓屏（BitBlt 路线，Win7 唯一可行方案）
    // ══════════════════════════════════════════════════════════════════

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteDC(IntPtr hdc);

    /// <summary>最低 Win2000。抓屏核心。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    internal const uint SRCCOPY = 0x00CC0020;
    internal const uint CAPTUREBLT = 0x40000000;

    // ══════════════════════════════════════════════════════════════════
    //  dwmapi —— 窗口材质 / 标题栏
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 最低 **WinVista**（Aero 开启时生效）。这是 Win7 上做「毛玻璃」的唯一途径 ——
    /// Win7 没有 Mica / Acrylic，但 DWM 的 blur-behind 视觉效果与亚克力同源。
    /// ⚠️ Aero 被关掉（经典主题 / 基本主题）时调用会静默无效，必须能退回纯色。
    /// </summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
    }

    internal const uint DWM_BB_ENABLE = 0x00000001;
    internal const uint DWM_BB_BLURREGION = 0x00000002;

    /// <summary>最低 WinVista。玻璃边框延伸到客户区（Win7 上配合 blur-behind 用）。</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
    }

    /// <summary>最低 WinVista。判断 DWM 合成是否开着（Aero 是否可用）。</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmIsCompositionEnabled(out bool pfEnabled);

    /// <summary>
    /// 最低 WinVista，但**属性编号随系统而异**：
    ///   · DWMWA_USE_IMMERSIVE_DARK_MODE(20) —— Win10 20H1+
    ///   · 旧编号(19) —— Win10 1809/1903
    ///   · DWMWA_WINDOW_CORNER_PREFERENCE(33) / DWMWA_SYSTEMBACKDROP_TYPE(38) —— Win11 22000+ / 22621+
    /// 调用前必须判版本；Win7 上调用未知属性只会返回 E_INVALIDARG，不会崩，但别白调。
    /// </summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    internal const int DWMWCP_DEFAULT = 0;
    internal const int DWMWCP_DONOTROUND = 1;
    internal const int DWMWCP_ROUND = 2;
    internal const int DWMWCP_ROUNDSMALL = 3;

    internal const int DWMSBT_AUTO = 0;
    internal const int DWMSBT_NONE = 1;
    internal const int DWMSBT_MAINWINDOW = 2;        // Mica
    internal const int DWMSBT_TRANSIENTWINDOW = 3;   // Acrylic
    internal const int DWMSBT_TABBEDWINDOW = 4;      // Mica Alt

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— Win10 1803+ 的 Acrylic（SetWindowCompositionAttribute）
    //  ⚠️ Win7/8/8.1 上没有这个导出，加载会失败 —— 必须用它前先判版本
    // ══════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ACCENTPOLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    internal const int WCA_ACCENT_POLICY = 19;
    internal const int ACCENT_DISABLED = 0;
    internal const int ACCENT_ENABLE_BLURBEHIND = 3;
    internal const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    // ══════════════════════════════════════════════════════════════════
    //  shell32 —— AppUserModelID（任务栏分组 / 通知归属）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win7。⚠️ Win7 上图标必须是磁盘上的 .ico 文件，不能是打包资源引用。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 全局键鼠注入（虚拟键盘 / 常用工具用）
    //  最低 Win2000，Win7 完整可用
    // ══════════════════════════════════════════════════════════════════

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint INPUT_MOUSE = 0;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
}
