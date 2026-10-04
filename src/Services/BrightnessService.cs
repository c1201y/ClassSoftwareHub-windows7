using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 屏幕亮度（**内建显示屏**专用）+ 自动亮度开关。
///
/// 跟音频那边一个原则：**读不到就老实说读不到**，绝不抛异常、绝不假装 0%。
///
/// ⚠️ 亮度有**两条完全不同的路**，用错路的现象就是"明明能调却说读不到"（2026-09-26 踩过）：
///   ① **WMI**（<c>root\wmi:WmiMonitorBrightness / WmiMonitorBrightnessMethods</c>）
///      —— 笔记本内屏走这条。**Windows 自己的亮度滑块也是走它**。✅ Win7 完整可用。
///   ② **dxva2.dll**（<c>Get/SetMonitorBrightness</c>）—— 那是 **DDC/CI（MCCS 0x10）**，
///      归**外接显示器**用。内屏**根本不支持 DDC/CI**，拿 HMONITOR 或 physical monitor 句柄去问
///      都是一路失败（错误码是 0xC02xxxxx 那类图形错误）。
///      一开始只写了这条 → 内屏上永远"读不到设备"，这就是那个 bug。
/// 所以现在是 **WMI 优先、dxva2 兜底**：内屏走 WMI，外接显示器（支持 DDC/CI 的）走 dxva2。
///
/// 自动亮度（跟随环境光）又是另一回事：得有**环境光传感器（ALS）**。
/// 没传感器的机器上 Windows 设置里那个开关也是摆设 —— 所以先探传感器，没有就把开关置灰。
///
/// ⚠️ Win7 移植说明：
///   · WMI 与 powercfg 两条路 Win7 都能用（<c>ADAPTBRIGHT</c> 的电源设置从 Vista 起就有）；
///   · dxva2（DDC/CI）Win7 也能用，但**外接显示器不支持 DDC/CI 时要能优雅失败** —— 本文件所有
///     dxva2 调用都包在 try/catch 里，且每次调用前都重新取句柄，失败一律返回 false，绝不外抛；
///   · 原版探 ALS 用的是 WinRT <c>Windows.Devices.Sensors.LightSensor</c>，**Win7 上没有 WinRT**，
///     该探测已降级（见 <see cref="AdaptiveSupported"/> 的 TODO）。
/// </summary>
public static class BrightnessService
{
    // ════════════════════════════════════════════════════════════════
    // 读
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 读当前亮度（0–100）。三条路任意一条通即可：
    /// ① WMI（内屏）→ ② dxva2/DDC-CI（外接屏）→ ③ **软件 gamma**（前两条都不通时的兜底）。
    /// 三条全不通才返回 false。
    /// </summary>
    public static bool TryGet(out int percent)
    {
        if (TryGetPhysical(out percent)) return true;
        if (GammaRead(out percent)) return true;
        percent = 0;
        return false;
    }

    /// <summary>物理亮度接口（WMI / DDC-CI）读得通吗。false = 这台机器只能靠软件 gamma 调。</summary>
    public static bool PhysicalAvailable
    {
        get
        {
            _physicalOk ??= TryGetPhysical(out _);
            return _physicalOk.Value;
        }
    }

    private static bool? _physicalOk;

    private static bool TryGetPhysical(out int percent)
    {
        percent = 0;
        return WmiRead(out percent) || Dxva2Read(out percent);
    }

    // ════════════════════════════════════════════════════════════════
    // 写
    // ════════════════════════════════════════════════════════════════

    private static readonly object Gate = new();
    private static int _want = -1;          // 攒着的最新目标值
    private static bool _armed;             // 合并窗口已经排上了
    private static int _applied = -1;       // 上次真设下去的值

    /// <summary>
    /// 设亮度（0–100）。
    /// ⚠️ 拖滑块一秒钟能出几十个值，而 **WMI 一次要十几到几十毫秒** → 逐个设会越拖越落后（手感变"糊"）。
    ///    所以这里做个 50ms 的**合并窗口**：窗口内连续调用只留最后一次，最多 20 次/秒，松手一定落在最终值上。
    /// </summary>
    public static void SetPercent(int percent)
    {
        var v = Math.Clamp(percent, 0, 100);

        lock (Gate)
        {
            _want = v;
            if (_armed) return;
            _armed = true;
        }

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(50).ConfigureAwait(false); } catch { }

            int target;
            lock (Gate)
            {
                target = _want;
                _want = -1;
                _armed = false;
            }

            if (target < 0 || target == _applied) return;
            if (WriteAny(target)) _applied = target;
        });
    }

    /// <summary>写下去这三条路的优先级缓存：-1 还没定，0/1/2 分别是 WMI / dxva2 / gamma，3 = 三条都不通。</summary>
    private static int _writePath = -1;

    /// <summary>
    /// 按 WMI → dxva2 → 软件 gamma 的顺序写，**谁先成谁被记住**：
    /// 定下来之后只试那一条，拖动滑块时不会每次都把三条路挨个撞一遍。
    /// </summary>
    private static bool WriteAny(int percent)
    {
        if (_writePath == 3) return false;                       // 已经确认三条都不通，别再白试
        if (_writePath == 0 && WmiWrite(percent)) return true;
        if (_writePath == 1 && Dxva2Write(percent)) return true;
        if (_writePath == 2 && GammaWrite(percent)) return true;

        _writePath = -1;                                         // 上次那条忽然不通了 / 还没定 → 重新走一遍

        if (WmiWrite(percent)) { _writePath = 0; return true; }
        if (Dxva2Write(percent)) { _writePath = 1; return true; }
        if (GammaWrite(percent)) { _writePath = 2; return true; }

        _writePath = 3;
        return false;
    }

    // ════════════════════════════════════════════════════════════════
    // ① WMI（内建显示屏）
    // ════════════════════════════════════════════════════════════════

    private static bool WmiRead(out int percent)
    {
        percent = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");

            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var v = mo["CurrentBrightness"];
                if (v is null) continue;

                percent = Math.Clamp(Convert.ToInt32(v), 0, 100);
                return true;
            }
        }
        catch (Exception ex)
        {
            LogOnce("WMI 读亮度失败: " + ex.Message);
        }
        return false;
    }

    private static bool WmiWrite(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");

            var any = false;
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                using var args = mo.GetMethodParameters("WmiSetBrightness");
                if (args is null) continue;

                args["Timeout"] = (uint)3;              // 秒
                args["Brightness"] = (byte)percent;     // 0–100

                using var result = mo.InvokeMethod("WmiSetBrightness", args, null);

                // ⚠️ 用 Properties[…] 而不是 result["ReturnValue"]：
                //    这个方法的 CIM 映射里**没有 ReturnValue 这一项**（实测：Invoke-CimMethod 返回的对象是空的），
                //    用索引器取会直接抛 ManagementException → 日志被刷屏、还误判成失败。
                var rc = result?.Properties["ReturnValue"]?.Value;
                if (rc is not null && Convert.ToInt32(rc) != 0) continue;   // 0 = 成功

                any = true;
            }
            return any;
        }
        catch (Exception ex)
        {
            LogOnce("WMI 设亮度失败: " + ex.Message);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // ② dxva2 / DDC-CI（外接显示器）
    // ════════════════════════════════════════════════════════════════

    private static bool Dxva2Read(out int percent)
    {
        percent = 0;
        try
        {
            if (!TryGetPhysicalMonitor(out var hMonitor, out var err))
            {
                LogOnce("dxva2 取显示器句柄失败: " + err);
                return false;
            }

            if (!NativeMethodsDisplay.GetMonitorBrightness(hMonitor, out var min, out var cur, out var max)) return false;

            var span = Math.Max(1u, max - min);
            percent = (int)Math.Round((cur - min) * 100.0 / span);
            percent = Math.Clamp(percent, 0, 100);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Dxva2Write(int percent)
    {
        try
        {
            if (!TryGetPhysicalMonitor(out var hMonitor, out _)) return false;
            if (!NativeMethodsDisplay.GetMonitorBrightness(hMonitor, out var min, out _, out var max)) return false;

            var target = min + (uint)Math.Round(Math.Clamp(percent, 0, 100) * (max - min) / 100.0);
            return NativeMethodsDisplay.SetMonitorBrightness(hMonitor, target);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 拿**物理显示器句柄** —— dxva2 这套是要这个，不是 HMONITOR（给 HMONITOR 会一直失败）。
    ///
    /// ⚠️ Win7 上外接显示器**不支持 DDC/CI** 时这一步会失败，属于预期：直接返回 false，
    ///    由调用方走 WMI 或如实报"读不到"，绝不外抛（见类注释的"优雅失败"要求）。
    /// </summary>
    private static bool TryGetPhysicalMonitor(out IntPtr hPhysicalMonitor, out string error)
    {
        hPhysicalMonitor = IntPtr.Zero;
        error = "";

        var h = NativeMethodsDisplay.MonitorFromPoint(new NativeMethodsDisplay.POINT(), NativeMethodsDisplay.MONITOR_DEFAULTTOPRIMARY);
        if (h == IntPtr.Zero) { error = "MonitorFromPoint 没给句柄"; return false; }

        if (!NativeMethodsDisplay.GetNumberOfPhysicalMonitorsFromHMONITOR(h, out var n) || n < 1)
        {
            error = "GetNumberOfPhysicalMonitorsFromHMONITOR 失败 err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        var arr = new NativeMethodsDisplay.PHYSICAL_MONITOR[n];
        if (!NativeMethodsDisplay.GetPhysicalMonitorsFromHMONITOR(h, n, arr))
        {
            error = "GetPhysicalMonitorsFromHMONITOR 失败 err=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        hPhysicalMonitor = arr[0].hPhysicalMonitor;      // 多显示器也先管主显示器
        return hPhysicalMonitor != IntPtr.Zero;
    }

    // ════════════════════════════════════════════════════════════════
    // ③ 软件亮度（gamma ramp）—— 前两条路都走不通时的兜底
    //
    // 为什么需要它（2026-10-02 实机日志）：
    //    目标机上 WMI 直接「不支持」（没有可调亮度内屏），DDC/CI 也拿不到物理显示器句柄
    //    （GetNumberOfPhysicalMonitorsFromHMONITOR 失败，err=0xC0262585 = 图形子系统错误）。
    //    这台机器（虚拟机 / 远程桌面）**物理上就没有可调亮度的显示器** ——
    //    前两条路再怎么修都是徒劳，界面只能永远显示「不可读」、滑块永远拖着没反应。
    //
    // 兜底办法：改显卡的 gamma 查找表。把 0..255 的每个输入整体压小，屏幕上就是变暗了。
    //    护眼软件、f.lux 这类全用这个原理，不需要显示器配合，Win7 完整可用。
    //
    // ⚠️ 说清楚它和真亮度的区别（界面上的 ToolTip 也这么写）：
    //    · 真的调背光：暗部细节还在，画面整体变暗；
    //    · gamma 模拟：**亮部一起压下去**，暗部会更糊一点，对比度略降。
    //    但在"显示器根本不听话"的机器上，这是唯一能让那颗滑块真的动起来的办法。
    //
    // ⚠️ 退出时一定要 <see cref="RestoreGamma"/>，否则用户的屏幕会一直暗着。
    // ════════════════════════════════════════════════════════════════

    /// <summary>gamma 调光的最低值 —— 别给到 0，全黑之后用户自己都找不回来。</summary>
    private const int GammaMinPercent = 10;

    private static readonly object GammaGate = new();
    private static ushort[]? _gammaOriginal;     // 第一次动手前存下来的原表（退出时恢复）
    private static int _gammaPercent = -1;        // 我们自己设过的值（-1 = 还没碰过 gamma）
    private static int _gammaProbe = -1;          // -1 没探过 / 0 不可用 / 1 可用

    /// <summary>这台机器能不能用软件 gamma 调亮度。</summary>
    public static bool SoftwareDimmable
    {
        get
        {
            if (_gammaProbe < 0)
            {
                _gammaProbe = ProbeGamma() ? 1 : 0;
                if (_gammaProbe == 0) LogOnce("软件调光（gamma）也不可用 —— 这台机器亮度完全无法调节");
            }
            return _gammaProbe == 1;
        }
    }

    /// <summary>
    /// 读：gamma 这条路**读不出真实值**（改的是显卡输出表，系统里没有"当前亮度"这个概念），
    /// 所以返回我们自己记着的那个数；没碰过就当成 100%（=还没压暗）。
    /// 这样界面上滑块是活的、数字有值，用户拖一下立刻见效，不会是一根拖不动的灰条。
    /// </summary>
    private static bool GammaRead(out int percent)
    {
        percent = 100;
        if (!SoftwareDimmable) return false;

        lock (GammaGate)
        {
            percent = _gammaPercent >= 0 ? _gammaPercent : 100;
            return true;
        }
    }

    /// <summary>写：算出 3×256 的 gamma 表压下去。第一次写之前先把原表存好。</summary>
    private static bool GammaWrite(int percent)
    {
        if (!SoftwareDimmable) return false;

        var hdc = IntPtr.Zero;
        try
        {
            hdc = NativeMethodsDisplay.CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;

            var v = Math.Clamp(percent, GammaMinPercent, 100);
            var ramp = new ushort[256 * 3];        // R / G / B 各 256 个

            lock (GammaGate)
            {
                if (_gammaOriginal is null)
                {
                    var cur = new ushort[256 * 3];
                    _gammaOriginal = NativeMethodsDisplay.GetDeviceGammaRamp(hdc, cur)
                        ? cur
                        : Array.Empty<ushort>();   // 读不到原表也记一笔，退出时至少知道"我们动过"
                }

                for (var i = 0; i < 256; i++)
                {
                    var level = (ushort)Math.Clamp((int)Math.Round(i * 257 * v / 100.0), 0, 65535);
                    ramp[i] = ramp[i + 256] = ramp[i + 512] = level;
                }

                if (!NativeMethodsDisplay.SetDeviceGammaRamp(hdc, ramp))
                {
                    LogOnce("软件调光失败：SetDeviceGammaRamp 被拒绝（该显卡驱动不支持软件 gamma）");
                    return false;
                }

                _gammaPercent = v;
                return true;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hdc != IntPtr.Zero) { try { NativeMethodsDisplay.DeleteDC(hdc); } catch { } }
        }
    }

    /// <summary>
    /// 探测能不能走 gamma 这条路：读一份现成的表再**原样写回**（等于什么都没改）。
    /// 读得到 + 写回得去，才算这条路通 —— 只有一半能力的情况（很多远程/虚拟显示驱动）不算。
    /// </summary>
    private static bool ProbeGamma()
    {
        var hdc = IntPtr.Zero;
        try
        {
            hdc = NativeMethodsDisplay.CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;

            var ramp = new ushort[256 * 3];
            if (!NativeMethodsDisplay.GetDeviceGammaRamp(hdc, ramp)) return false;
            return NativeMethodsDisplay.SetDeviceGammaRamp(hdc, ramp);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hdc != IntPtr.Zero) { try { NativeMethodsDisplay.DeleteDC(hdc); } catch { } }
        }
    }

    /// <summary>把 gamma 还原成原来的样子。**程序退出时必须调**，否则用户的屏幕会一直暗着。</summary>
    public static void RestoreGamma()
    {
        ushort[]? original;
        lock (GammaGate)
        {
            original = _gammaOriginal;
            _gammaOriginal = null;
            _gammaPercent = -1;
        }

        if (original is null || original.Length != 256 * 3) return;

        var hdc = IntPtr.Zero;
        try
        {
            hdc = NativeMethodsDisplay.CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdc != IntPtr.Zero) NativeMethodsDisplay.SetDeviceGammaRamp(hdc, original);
        }
        catch
        {
        }
        finally
        {
            if (hdc != IntPtr.Zero) { try { NativeMethodsDisplay.DeleteDC(hdc); } catch { } }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 自动亮度（环境光传感器）
    // ════════════════════════════════════════════════════════════════

    private static int _alsProbe = -1;      // -1 = 还没探过，0 = 没传感器，1 = 有

    /// <summary>这台机器有没有环境光传感器（= 自动亮度有没有意义）。探一次就记住。</summary>
    /// <remarks>
    /// ⚠️ Win7 移植降级：原版用 WinRT <c>Windows.Devices.Sensors.LightSensor.GetDefault()</c>，
    ///    **Win7 上没有任何 WinRT 运行时**，这条探测在本移植版里恒为"没有传感器"→ 自动亮度开关被置灰。
    ///    Win7 上等价的原生路径是 **COM 的 Sensor API**（<c>CLSID_SensorManager</c> +
    ///    <c>SENSOR_TYPE_AMBIENT_LIGHT</c>，最低支持 Windows 7，sensorsapi.h），
    ///    但它的 vtable 顺序与两个 GUID 必须按 SDK 头文件逐字核对 ——
    ///    移植期手头没有该头文件、无法核验，按契约「不许凭印象写 COM 布局」的纪律**刻意不实现**，
    ///    以免 vtable 错位导致随机崩溃。
    /// </remarks>
    public static bool AdaptiveSupported
    {
        get
        {
            if (_alsProbe < 0)
            {
                // TODO(win7): 如需在 Win7 笔记本上真正支持 ALS，按 sensorsapi.h 的 CLSID_SensorManager /
                //             SENSOR_TYPE_AMBIENT_LIGHT 与 ISensorManager::GetSensorsByType 实现探测
                //             （vtable 顺序、GUID 必须逐字核验后再动）。
                _alsProbe = 0;
            }
            return _alsProbe == 1;
        }
    }

    /// <summary>自动亮度现在开着吗（读电源设置里 ADAPTBRIGHT 的交流索引）。</summary>
    public static bool TryGetAdaptive(out bool on)
    {
        on = false;
        try
        {
            var text = Run("powercfg", $"/query SCHEME_CURRENT {SubVideo} {AdaptBright}");
            if (text is null) return false;

            // ⚠️ 别去匹配 powercfg 输出里的中文（"当前交流电源设置索引"这类字样是**跟系统语言走**的，
            //    英文系统上就全对不上了）。按位置取：输出里最后一对 0x 值 = 当前交流 / 当前直流。
            var hex = Regex.Matches(text, @"0x([0-9a-fA-F]{1,8})");
            if (hex.Count < 2) return false;

            on = Convert.ToInt32(hex[hex.Count - 2].Groups[1].Value, 16) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开关自动亮度（交流 + 直流都设，跟系统设置里那个开关等价）。成功返回 true。</summary>
    public static bool SetAdaptive(bool on)
    {
        try
        {
            var v = on ? "1" : "0";
            var a = Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {SubVideo} {AdaptBright} {v}");
            var b = Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {SubVideo} {AdaptBright} {v}");
            var c = Run("powercfg", "/setactive SCHEME_CURRENT");
            if (a is null || b is null || c is null) return false;

            // 复核一次：powercfg 偶尔"命令成功但没落到当前方案"，别让界面骗人
            return TryGetAdaptive(out var now) && now == on;
        }
        catch
        {
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // 底下这些小东西
    // ════════════════════════════════════════════════════════════════

    /// <summary>跑一条命令，成功（退出码 0）返回输出文本，否则 null。</summary>
    private static string? Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            // 中文字节会被按非 UTF8 读成乱码，无所谓 —— 我们只要里面的 0x 十六进制
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return p.HasExited && p.ExitCode == 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>亮度这条路的日志（万一又出"读不到"，看这个就知道卡在哪一步）。</summary>
    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Dir);
            File.AppendAllText(Path.Combine(SettingsStore.Dir, "brightness.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // 日志写不进去就算了
        }
    }

    /// <summary>已经报过的失败就别再报（见 <see cref="Log"/> 的调用点）。</summary>
    private static readonly HashSet<string> _loggedOnce = new();

    /// <summary>
    /// 同一条失败消息**只记一次**。
    ///
    /// ⚠️ 2026-10-02：浮窗开着的时候亮度是每 3 秒读一次，而这台机器两条物理路都注定失败 ——
    ///    实测 brightness.log 就成了每 3 秒两行的复读机（WMI + dxva2 各一行，一直刷到天黑）。
    ///    这种刷屏会把真正有用的信息顶出诊断控制台，也让日志文件白白长大。
    ///    "读不到"是这台机器的**恒定事实**，说一次就够。
    /// </summary>
    private static void LogOnce(string message)
    {
        lock (_loggedOnce)
        {
            if (!_loggedOnce.Add(message)) return;
        }
        Log(message);
    }

    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";      // SUB_VIDEO（显示）
    private const string AdaptBright = "fbd9aa66-9553-4097-ba44-ed6e9d65eab8";   // ADAPTBRIGHT（自适应亮度）
}
