using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <see cref="NativeMethods"/> 的**补充**：SystemInfo / ScreenCapture / WindowChrome 需要的
/// Win32 导出，按移植契约「所有 P/Invoke 都必须集中在 Platform」的规矩收在这里。
///
/// ⚠️ 为什么另开一个类而不是并进 <see cref="NativeMethods"/>：
///    那个文件**不是 partial**（且移植期明确要求不修改已就绪的 Platform 文件），
///    没法就地扩展，所以新开一个同命名空间的伴生类。语义上二者是一回事，
///    调用方一律走 <c>Platform.NativeMethods*</c>。
///
/// 每个导出仍按 <see cref="NativeMethods"/> 的惯例，在注释里标**最低可用系统**。
/// </summary>
internal static class NativeMethodsEx
{
    // ══════════════════════════════════════════════════════════════════
    //  kernel32 —— 硬件 / 系统信息
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 WinXP SP3（Win7 完整可用）。数物理核（数 Relationship == 0 的条目）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CACHE_DESCRIPTOR
    {
        public byte Level;
        public byte Associativity;
        public ushort LineSize;
        public uint CacheSize;
        public int Type;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct SLPI_UNION
    {
        [FieldOffset(0)] public CACHE_DESCRIPTOR Cache;
        [FieldOffset(0)] public uint NodeNumber;
        [FieldOffset(0)] public ulong Reserved0;
        [FieldOffset(8)] public ulong Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SLPI_ENTRY
    {
        public UIntPtr ProcessorMask;
        public int Relationship;
        public SLPI_UNION U;
    }

    /// <summary>最低 WinVista。读固件表（取 SMBIOS 内存条信息）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetSystemFirmwareTable(uint signature, uint tableId, IntPtr buffer, uint size);

    /// <summary>最低 Win2000。物理内存总量（SMBIOS 解不出来时的兜底）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>
    /// 最低 WinXP。CPU 特性查询（PF_VIRT_FIRMWARE_ENABLED = 21）。
    /// ⚠️ 这个位会被 Hyper-V / VBS 挡住，见 SystemInfo.VirtualizationText 的说明。
    /// </summary>
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessorFeaturePresent(uint feature);

    // ── 剪贴板写位图（Win7 上唯一可靠的「复制图片」路径）─────────────
    // ⚠️ Avalonia 的 IClipboard 只认文本 / 文件，没有位图格式，
    //    所以截图的「复制到剪贴板」直接走 Win32 的 CF_DIB。

    /// <summary>最低 Win2000。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    internal const uint GMEM_MOVEABLE = 0x0002;

    // ══════════════════════════════════════════════════════════════════
    //  psapi —— 性能信息（虚拟内存 / 分页文件）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。</summary>
    [DllImport("psapi.dll", SetLastError = true)]
    internal static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable;
        public UIntPtr SystemCache, KernelTotal, KernelPaged, KernelNonPaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 显示设备（显示器型号 / 分辨率）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public uint dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    /// <summary>⚠️ 必须用 W 版导出名：64 位 user32 **没有** 裸露的 <c>GetWindowLongPtr</c>，
    /// 只有 <c>GetWindowLongPtrW</c> / <c>...A</c>。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>32 位进程下的对应导出。</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    /// <summary>最低 Win2000。取窗口的 owner / 上一窗口（z 序遍历用）。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    internal const uint GW_OWNER = 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>最低 Win2000。最大触控点数（WM_ 之外的系统指标）。</summary>
    internal const int SM_MAXIMUMTOUCHES = 95;

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 剪贴板（写 CF_DIB）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。剪贴板随时可能被别的程序占着，失败要重试。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    internal const uint CF_DIB = 8;

    // ══════════════════════════════════════════════════════════════════
    //  gdi32 —— 抓屏用的 DIB 段（Win7 没有 Windows.Graphics.Capture，只能 BitBlt）
    // ══════════════════════════════════════════════════════════════════

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    internal const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        // 本该跟一张调色板，32bpp 用不着，只声明头
    }

    // ══════════════════════════════════════════════════════════════════
    //  dwmapi —— 取窗口「真实可视边界」（阴影/可缩放边框不算进去）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 WinVista。取值为 <see cref="NativeMethods.RECT"/>（不是 int）。</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out NativeMethods.RECT lpRect, int cbAttribute);

    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // ══════════════════════════════════════════════════════════════════
    //  user32 —— 自绘窗口框架（Core/SelfDrawnFrame 用）
    //
    //  思路：**一个字节的窗口样式都不改**（WS_CAPTION / WS_THICKFRAME 原样留着），
    //  只在消息层把非客户区的绘制吃掉、把边缘命中自己报回去。
    //  系统的缩放、吸附、Win+方向键、任务栏右键菜单、双击最大化因此全部照常工作 ——
    //  这正是 WindowChrome.MakeBorderless（WS_POPUP 路线）做不到的事。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。系统问"这个窗口的非客户区有多宽"。</summary>
    internal const uint WM_NCCALCSIZE = 0x0083;

    /// <summary>最低 Win2000。系统问"鼠标这一点算什么"（屏幕坐标）。</summary>
    internal const uint WM_NCHITTEST = 0x0084;

    /// <summary>属于客户区（内容自己处理）。</summary>
    internal const int HTCLIENT = 1;

    // ⚠️ HTCAPTION(2) 已在 NativeMethods 里定义，不重复；下面这几档共 8 个方向。
    internal const int HTLEFT = 10;
    internal const int HTRIGHT = 11;
    internal const int HTTOP = 12;
    internal const int HTTOPLEFT = 13;
    internal const int HTTOPRIGHT = 14;
    internal const int HTBOTTOM = 15;
    internal const int HTBOTTOMLEFT = 16;
    internal const int HTBOTTOMRIGHT = 17;

    /// <summary>最低 Win2000。取窗口所在显示器（最大化时要拿它的工作区）。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>最低 Win2000。取显示器信息（要用 rcWork）。</summary>
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public NativeMethods.RECT rcMonitor;
        public NativeMethods.RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// <c>WM_NCCALCSIZE</c> 在 wParam=TRUE 时的参数。<b>只用到 rgrc[0]</b>，
    /// 三个 RECT 顺序排布，摊平成三个字段直接读写即可（省掉 ByValArray 的封送）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NCCALCSIZE_PARAMS
    {
        /// <summary>窗口矩形（进）→ 客户区矩形（出）。</summary>
        public NativeMethods.RECT rgrc0;
        public NativeMethods.RECT rgrc1;
        public NativeMethods.RECT rgrc2;
        public IntPtr lppos;
    }

    // ══════════════════════════════════════════════════════════════════
    //  非客户区绘制的抑制（2026-10-02）
    //
    //  「拖拽调整大小时又冒出原生边框」的根治。光靠 WM_NCCALCSIZE 返回 0 在
    //  Win10/11 的 DWM 合成下就够了，但**关了 DWM 合成的 Win7 经典主题**下，
    //  系统仍会在缩放途中用主题引擎补画那圈边框 —— 必须把这几个消息也吃掉。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>系统要求重画非客户区。非客户区已经是 0 像素，直接回 0 别让它画。</summary>
    internal const uint WM_NCPAINT = 0x0085;

    /// <summary>窗口激活/失活。返回 TRUE = "非客户区我自己负责"，阻止系统重画边框。</summary>
    internal const uint WM_NCACTIVATE = 0x0086;

    /// <summary>
    /// 主题引擎（uxtheme）要求画标题栏 / 窗口框架。<b>未公开消息</b>，
    /// 但这是「经典主题下边框总也去不干净」的关键一条 —— 回 0 就不会画。
    /// </summary>
    internal const uint WM_NCUAHDRAWCAPTION = 0x00AE;

    /// <inheritdoc cref="WM_NCUAHDRAWCAPTION" />
    internal const uint WM_NCUAHDRAWFRAME = 0x00AF;

    /// <summary>需要擦背景。自绘框架用它只填「新暴露的那条带」，避免整窗擦除导致闪白/闪黑。</summary>
    internal const uint WM_ERASEBKGND = 0x0014;

    /// <summary>
    /// 进入系统的「改大小 / 移动」模态循环（拖边缘、拖标题栏）。
    ///
    /// <para>
    /// 从收到它到 <see cref="WM_EXITSIZEMOVE"/> 之间，系统自己跑一个消息循环，
    /// 期间鼠标每动一像素就改一次窗口尺寸 → <c>Resized</c> / <c>PositionChanged</c> 会密集触发。
    /// 这段窗口期内**只做必须跟着走的最小工作**（重排页面、重画），
    /// 把"要不要发网页脚本、要不要重算浮层落点"这类重活推到 <see cref="WM_EXITSIZEMOVE"/>，
    /// 否则拖拽就会一顿一顿的（用户报的"拖拽调整大小卡卡的"）。
    /// </para>
    /// </summary>
    internal const uint WM_ENTERSIZEMOVE = 0x0231;

    /// <summary>退出「改大小 / 移动」模态循环 —— 拖拽结束，可以把攒下的重活一次做完。</summary>
    internal const uint WM_EXITSIZEMOVE = 0x0232;

    // ══════════════════════════════════════════════════════════════════
    //  DWM 非客户区渲染策略（2026-10-02，修「出现两个关闭按钮」）
    // ══════════════════════════════════════════════════════════════════

    /// <summary><c>DWMWA_NCRENDERING_POLICY</c>：由 DWM 决定非客户区（标题栏/边框）画不画。</summary>
    internal const int DWMWA_NCRENDERING_POLICY = 2;

    /// <summary>
    /// <c>DWMNCRP_DISABLED</c>：**不让 DWM 画非客户区**。
    ///
    /// <para>
    /// 为什么必须有这一条：Win7 / Vista 开了 Aero（DWM 合成）时，
    /// 窗口的玻璃边框与标题栏按钮是 **DWM 自己合成上去的**，
    /// 和 <c>WM_NCPAINT</c>（GDI 那一路）根本不是同一个通道 ——
    /// 所以我们把 <c>WM_NCPAINT / WM_NCUAHDRAW*</c> 全回绝了，标题栏按钮照样显示，
    /// 于是应用内自绘的 × 和 DWM 画的 × 叠在一起，看起来就是「出现两个关闭按钮」。
    /// 这一条把 DWM 那一路也关掉，两边就都不画了。
    /// </para>
    /// </summary>
    internal const int DWMNCRP_DISABLED = 1;

    /// <summary>取/设窗口类样式（<c>GCL_STYLE</c>）。⚠️ 改的是**整个类**，不是单个窗口。</summary>
    internal const int GCL_STYLE = -26;

    /// <summary>窗口类的背景刷（只读，排障用）。</summary>
    internal const int GCLP_HBRBACKGROUND = -10;

    internal const int CS_VREDRAW = 0x0001;
    internal const int CS_HREDRAW = 0x0002;

    // ⚠️ GetClassLongPtrW / SetClassLongPtrW 只在 64 位 user32 上导出；
    //    32 位系统上它们是头文件宏，实际解析成 GetClassLongW / SetClassLongW。
    //    跟 WindowChrome.MakeBorderless 里那对是同一个坑，这里也按指针宽度路由。
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr GetClassLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
    private static extern uint GetClassLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
    private static extern uint SetClassLongW(IntPtr hWnd, int nIndex, uint dwNewLong);

    /// <summary>读窗口类样式（32/64 位安全）。</summary>
    internal static uint GetClassStyle(IntPtr hWnd)
        => IntPtr.Size == 8
            ? unchecked((uint)(long)GetClassLongPtrW(hWnd, GCL_STYLE))
            : GetClassLongW(hWnd, GCL_STYLE);

    /// <summary>写窗口类样式（32/64 位安全）。</summary>
    internal static void SetClassStyle(IntPtr hWnd, uint value)
    {
        if (IntPtr.Size == 8)
            _ = SetClassLongPtrW(hWnd, GCL_STYLE, new IntPtr(unchecked((int)value)));
        else
            _ = SetClassLongW(hWnd, GCL_STYLE, value);
    }

    // ── 光标（GripDragFrame：浮窗把手上显式画"能拖"的四向移动光标）──
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr LoadCursorW(IntPtr hInstance, uint lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetCursorW(IntPtr hCursor);

    // ══════════════════════════════════════════════════════════════════
    //  窗口区域裁剪 —— Win7 / Win10 的圆角（Win11 走 DWM 的 CORNER_PREFERENCE）
    //
    //  Win7 上 DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE) 是不认的，
    //  必须用 SetWindowRgn 把窗口形状裁成圆角矩形。
    //  ⚠️ 只能用在**不可缩放**的窗口上：区域之外的像素不参与命中测试，
    //     主窗口那圈缩放边会被裁掉，点不中。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 建一个圆角矩形区域。
    /// ⚠️ <paramref name="right"/> / <paramref name="bottom"/> 是**坐标**不是宽高，
    ///    且是**开区间**：想要 w×h 的整块就传 (0, 0, w, h)，传 w+1 会多出 1 像素。
    ///    <paramref name="ellipseWidth"/> / <paramref name="ellipseHeight"/> 是圆角椭圆的**直径**。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
                                                    int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    /// <summary>区域合并（RGN_OR / RGN_DIFF…）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    internal const int RGN_OR = 2;

    /// <summary>
    /// 释放 GDI 对象。
    /// ⚠️ <c>SetWindowRgn</c> <b>成功</b>之后区域归系统所有，那时<b>绝不能</b>调它 ——
    /// 只有设置失败（区域还在我们手上）时才要自己释放。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 设窗口形状。成功后区域<b>归系统所有</b>，不能再 DeleteObject；
    /// 传 IntPtr.Zero 表示恢复成普通矩形。返回 0 表示失败。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // ══════════════════════════════════════════════════════════════════
    //  局部擦背景（缩放时只填新暴露的条带）
    // ══════════════════════════════════════════════════════════════════

    [DllImport("user32.dll")]
    internal static extern bool GetUpdateRect(IntPtr hWnd, out NativeMethods.RECT lpRect, bool bErase);

    /// <summary>客户区矩形（左上角恒为 0,0）。擦整窗时的兜底范围。</summary>
    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    /// <summary>
    /// 标脏一块区域（null 矩形 = 整个客户区）。
    /// <c>bErase=false</c> 时不产生 <c>WM_ERASEBKGND</c> —— 只让应用重画一遍，不闪。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    /// <summary>窗口尺寸变化（<c>wParam</c> 是 SIZE_* 类型）。</summary>
    internal const uint WM_SIZE = 0x0005;

    /// <summary>系统主题（配色方案）变了 —— 主题引擎会借机重画非客户区。</summary>
    internal const uint WM_THEMECHANGED = 0x031A;

    /// <summary>DWM 合成开关状态变了 —— DWM 会重读非客户区渲染策略。</summary>
    internal const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("user32.dll")]
    internal static extern int FillRect(IntPtr hDC, ref NativeMethods.RECT lprc, IntPtr hbr);

    /// <summary>把 <c>Color</c> 折成 GDI 的 <c>COLORREF</c>（0x00BBGGRR）。</summary>
    internal static uint ToColorRef(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ══════════════════════════════════════════════════════════════════
    //  系统 DPI（Win7 没有 GetDpiForWindow，圆角半径得按系统 DPI 换算）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。<c>GetDeviceCaps</c> 的索引：水平分辨率（DPI）。</summary>
    internal const int LOGPIXELSX = 88;

    /// <summary>最低 Win2000。取设备能力（这里只用 LOGPIXELSX 拿系统 DPI）。</summary>
    [DllImport("gdi32.dll")]
    internal static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
}
