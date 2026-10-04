using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <see cref="NativeMethods"/> 的**第三批补充**：显示器亮度（外接显示器的 DDC/CI 路径）。
///
/// ⚠️ 只服务于 <c>Services.BrightnessService</c> 的「外接显示器」兜底：
///    · <c>MonitorFromPoint</c> 走 user32，Win2000 起就有；
///    · <c>dxva2.dll</c> 的四个导出最低 WinVista，**Win7 完整可用**。
/// ⛔ 内建显示屏**不支持 DDC/CI**（这条路去问内屏只会一路失败，错误码是 0xC02xxxxx 那类图形错误），
///    内屏一律走 WMI，见 <c>BrightnessService</c> 的类注释。
/// </summary>
internal static class NativeMethodsDisplay
{
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>最低 Win2000。按坐标拿 HMONITOR（不是物理显示器句柄）。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>最低 WinVista。</summary>
    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    /// <summary>最低 WinVista。</summary>
    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] arr);

    /// <summary>最低 WinVista。DDC/CI（MCCS 0x10）读亮度。</summary>
    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint min, out uint current, out uint max);

    /// <summary>最低 WinVista。DDC/CI（MCCS 0x10）写亮度。</summary>
    [DllImport("dxva2.dll", SetLastError = true)]
    internal static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    // ══════════════════════════════════════════════════════════════════
    //  gdi32 —— 软件亮度（gamma ramp）
    //
    //  ⚠️ 第三条路，服务于「WMI 与 DDC/CI 都不通」的机器（外接屏不带 DDC/CI、
    //     虚拟机 / 远程桌面的虚拟显示器）。原理：直接改显卡输出的 gamma 查找表，
    //     把 0..255 的每个输入值整体压小 —— 屏幕上就是变暗了。
    //     所有"护眼软件"的亮度调节都是这个做法。
    //
    //     它不是真的调背光：亮部会一起压下去、对比度略降，但**确实看得见效果**，
    //     而且不需要显示器配合。全部 Win32 GDI 接口，Win2000 起就有，Win7 完整可用。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>拿显示设备上下文（GDI 的 DC，用完必须 <see cref="DeleteDC"/>）。</summary>
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    /// <summary>释放 <see cref="CreateDC"/> 拿到的 DC。</summary>
    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    /// <summary>读当前 gamma 表（3×256 个 ushort：R/G/B 各一段）。</summary>
    [DllImport("gdi32.dll")]
    internal static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] lpRamp);

    /// <summary>写 gamma 表。数组长度必须正好 3×256。</summary>
    [DllImport("gdi32.dll")]
    internal static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);
}
