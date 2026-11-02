using System;
using System.Runtime.Versioning;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 操作系统版本探测。**本移植版所有「Win10 才有 / Win7 没有」的判定都必须走这里。**
///
/// ⛔ 为什么不用 <c>Environment.OSVersion</c>：.NET 读了 exe 清单里的 <c>supportedOS</c> 之后，
///    会把版本统一「兼容性上报」成 6.2（Win8），在 Win7 上会直接误判。必须走 RtlGetVersion。
/// </summary>
public static class OsInfo
{
    private static readonly Version Raw = Read();

    /// <summary>
    /// 排障开关：强制按 Windows 7 的分支运行。
    ///
    /// 为什么需要它：开发机是 Win10/11，而「主界面出不来 / 界面点不动 / 托盘没图标」这类问题
    /// **只在 Win7 的代码路径上出现** —— 扩展客户区、Aero 毛玻璃、深色标题栏、窗口圆角……
    /// 这些判定全都是按系统版本分叉的。没有这个开关，就只能一次次请用户在教室机上试；
    /// 有了它，开发机上就能把 Win7 那条分支完整走一遍。
    ///
    /// 由 Program.Main 从 <c>--simulate-win7</c> 或环境变量 <c>CSH_SIMULATE_WIN7=1</c> 打开。
    /// ⚠️ 只是**分支模拟**：机器真实内核仍是 Win10/11，个别 API 的实际行为会有差异；
    ///    但「走哪条代码路径」是准的，而这恰好是排查时要钉死的东西。
    /// </summary>
    public static bool SimulateWin7 { get; internal set; }

    /// <summary>真实内核版本，例如 Win7 SP1 = 6.1.7601。</summary>
    public static Version Version => Raw;

    /// <summary>Windows 7（含 SP1）。本移植版的**主目标系统**。</summary>
    public static bool IsWindows7 => SimulateWin7 || (Raw.Major == 6 && Raw.Minor == 1);

    /// <summary>Windows 8 / 8.1。</summary>
    public static bool IsWindows8 => !SimulateWin7 && Raw.Major == 6 && (Raw.Minor == 2 || Raw.Minor == 3);

    /// <summary>Windows 8 及以上（含 8.1 / 10 / 11）。用于「Win8 才引入的功能」判定，
    /// 例如 <c>chkdsk /scan</c>、DISM 的 <c>/ScanHealth</c> / <c>/RestoreHealth</c> / <c>/StartComponentCleanup</c>。</summary>
    public static bool IsWindows8OrLater => !SimulateWin7 && (Raw.Major > 6 || (Raw.Major == 6 && Raw.Minor >= 2));

    /// <summary>Windows 10 / 11。</summary>
    public static bool IsWindows10OrLater => !SimulateWin7 && Raw.Major >= 10;

    /// <summary>Windows 11（内核仍报 10.0，靠 build 号区分：22000 起）。</summary>
    public static bool IsWindows11 => !SimulateWin7 && Raw.Major >= 10 && Raw.Build >= 22000;

    /// <summary>Win10 1803（17134）起才有 <c>SetWindowCompositionAttribute</c> 的亚克力。</summary>
    public static bool SupportsAcrylicBlur => !SimulateWin7 && Raw.Major >= 10 && Raw.Build >= 17134;

    /// <summary>Win11 22000 起才有 Mica。</summary>
    public static bool SupportsMica => IsWindows11;

    private static bool? _dwmMaterials;

    /// <summary>
    /// 系统背景材质（Mica / 亚克力）在当前图形环境里**会不会真的被 DWM 画出来**。
    ///
    /// ⛔ 为什么必须有这个判定（2026-11-01 本机实测，Win11 26200 + QXL 云桌面）：
    ///    DWM 的材质要靠 GPU 合成。虚拟/间接显卡（QXL、virtio、VirtualBox、IDD 虚拟显示器）
    ///    和远程会话里 DWM 直接关掉材质，只给窗口刷自己的**兜底设计色**
    ///    （深 #202020 / 浅 #F3F3F3）：
    ///      · <c>DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE=4)</c> 返回成功，
    ///        窗口却是一块死色；把壁纸换成洋红/绿两半，窗口像素纹丝不动；
    ///      · Avalonia 的合成亚克力（host backdrop brush）更糟，整窗渲染成**黑色**；
    ///      · 记事本（WinUI3 Mica）、开始菜单的材质同样失效 —— 这是系统级现象，
    ///        不是本程序没调对 API。
    ///    这类机器上「系统材质」这条路是死路，<see cref="Backdrop"/> 会改走半透明底色。
    /// </summary>
    public static bool DwmMaterialsAvailable
    {
        get
        {
            if (!_dwmMaterials.HasValue) _dwmMaterials = ProbeDwmMaterials();
            return _dwmMaterials.Value;
        }
    }

    /// <summary>判定失败的常见虚拟显卡 PCI 厂商号：Red Hat QXL、QEMU、virtio、VirtualBox、
    /// VMware、Microsoft（Hyper-V / Basic Render）、Parallels。</summary>
    private static readonly string[] VirtualVendors = { "1B36", "1234", "1AF4", "80EE", "15AD", "1414", "1AB8" };

    private static bool ProbeDwmMaterials()
    {
        try
        {
            if (NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0) return false;

            var dd = new NativeMethods.DISPLAY_DEVICE
            {
                cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>()
            };
            for (uint i = 0; i < 16; i++)
            {
                if (!NativeMethods.EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & NativeMethods.DISPLAY_DEVICE_PRIMARY_DEVICE) == 0) continue;
                return !IsSoftwareDisplay(dd);
            }
        }
        catch
        {
            // 拿不到显示信息（极端情况）→ 按「可用」处理，维持原来的 DWM 级联
        }
        return true;
    }

    private static bool IsSoftwareDisplay(in NativeMethods.DISPLAY_DEVICE dd)
    {
        var id = dd.DeviceID ?? "";
        var name = dd.DeviceString ?? "";

        // 根枚举的是间接显示器驱动（IDD 虚拟屏）
        if (id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var ven in VirtualVendors)
            if (id.IndexOf("VEN_" + ven, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        return name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Remote", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Win11 22000 起才有窗口圆角（DwmSetWindowAttribute 的 CORNER_PREFERENCE）。</summary>
    public static bool SupportsWindowRounding => IsWindows11;

    /// <summary>Win10 1809（17763）起才认「深色标题栏」这个 DWM 属性。</summary>
    public static bool SupportsDarkTitleBar => !SimulateWin7 && Raw.Major >= 10 && Raw.Build >= 17763;

    /// <summary>剪贴板历史（Win+V）是 Win10 1809（17763）才有的功能，Win7/8 按下无任何反应。</summary>
    public static bool SupportsClipboardHistory => !SimulateWin7 && Raw.Major >= 10 && Raw.Build >= 17763;

    /// <summary>
    /// DWM 合成是否开着 —— Win7 上「毛玻璃」能不能用全看它。
    /// Aero 被关掉（经典/基本主题）时返回 false，此时必须退回纯色底，不能留透明。
    /// </summary>
    public static bool IsDwmCompositionEnabled
    {
        get
        {
            try
            {
                if (NativeMethods.DwmIsCompositionEnabled(out var enabled) != 0) return false;
                return enabled;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    /// <summary>给日志/关于页用的一行描述，例如「Windows 7 (6.1.7601)」。</summary>
    public static string Describe()
    {
        var name = Raw.Major switch
        {
            >= 10 when Raw.Build >= 22000 => "Windows 11",
            >= 10 => "Windows 10",
            6 when Raw.Minor == 3 => "Windows 8.1",
            6 when Raw.Minor == 2 => "Windows 8",
            6 when Raw.Minor == 1 => "Windows 7",
            6 when Raw.Minor == 0 => "Windows Vista",
            5 => "Windows XP",
            _ => "Windows"
        };

        // 模拟模式下必须写明 —— 否则日志会误导后来人（让人以为真在 Win7 上跑过）。
        return SimulateWin7
            ? $"Windows 7（★ 模拟分支，真实内核 {Raw}）"
            : $"{name} ({Raw})";
    }

    private static Version Read()
    {
        try
        {
            var osvi = new NativeMethods.OSVERSIONINFOEX
            {
                dwOSVersionInfoSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.OSVERSIONINFOEX>()
            };
            if (NativeMethods.RtlGetVersion(ref osvi) != 0)
                return Environment.OSVersion.Version;

            return new Version((int)osvi.dwMajorVersion, (int)osvi.dwMinorVersion, (int)osvi.dwBuildNumber);
        }
        catch
        {
            // ntdll 取不到（理论上不会）→ 退回托管 API，至少不致崩
            return Environment.OSVersion.Version;
        }
    }
}
