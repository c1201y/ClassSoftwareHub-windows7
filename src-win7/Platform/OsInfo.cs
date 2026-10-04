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

    /// <summary>Windows 10 / 11。</summary>
    public static bool IsWindows10OrLater => !SimulateWin7 && Raw.Major >= 10;

    /// <summary>Windows 11（内核仍报 10.0，靠 build 号区分：22000 起）。</summary>
    public static bool IsWindows11 => !SimulateWin7 && Raw.Major >= 10 && Raw.Build >= 22000;

    /// <summary>Win10 1803（17134）起才有 <c>SetWindowCompositionAttribute</c> 的亚克力。</summary>
    public static bool SupportsAcrylicBlur => !SimulateWin7 && Raw.Major >= 10 && Raw.Build >= 17134;

    /// <summary>Win11 22000 起才有 Mica。</summary>
    public static bool SupportsMica => IsWindows11;

    /// <summary>Win11 22000 起才有窗口圆角（DwmSetWindowAttribute 的 CORNER_PREFERENCE）。</summary>
    public static bool SupportsWindowRounding => IsWindows11;

    /// <summary>Win10 1809（17763）起才认「深色标题栏」这个 DWM 属性。</summary>
    public static bool SupportsDarkTitleBar => !SimulateWin7 && Raw.Major >= 10 && Raw.Build >= 17763;

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
