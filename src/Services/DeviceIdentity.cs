using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClassSoftwareHub.Desktop.Platform;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 硬件锚定的设备标识：给服务端做信誉判定与封禁用，请求头 <c>X-CSH-Did</c>。
///
/// <para>
/// 取值优先级：
/// <list type="number">
///   <item><b>Windows 10 1607+</b>：<c>Windows.System.Profile.SystemIdentification.GetSystemIdForPublisher()</c>
///         —— 系统自己给的发布者标识（有 TPM 时锚在 TPM，有 UEFI 时锚在 UEFI，
///         都没有才退注册表）。这是官方口径，重装系统只要硬件不变，TPM/UEFI 来源的值通常也不变。</item>
///   <item><b>Windows 7 / 8 / 8.1，或上面那条取不到</b>：<c>MachineGuid</c> +
///         系统卷序列号自己算。<b>必须能跑</b> —— Win7 是本移植版的主目标系统。</item>
/// </list>
/// 两条路最后都过同一个哈希（<see cref="Salt"/> + 原始材料 → SHA-256 → 取前 16 位十六进制），
/// 所以对外永远是一个 16 字符的小写串，服务端不需要区分它是哪条路来的。
/// </para>
///
/// <para>
/// 为什么 Win7 走不了 <c>GetSystemIdForPublisher</c>：这是 WinRT 静态类，最低 Windows 10 1607，
/// Win7 上连 <c>combase.dll</c> 的 <c>RoGetActivationFactory</c> 都没有。这不是「调用姿势不对」，
/// 而是 API 与整个 WinRT 运行时都不存在 —— 所以只能另取等效信号。
/// </para>
///
/// <para>
/// 与 <see cref="DeviceFingerprint"/> 的分工：后者是早期上线的设备码（随机 ID 落盘 + 机器名 + 用户名），
/// 已被授权令牌绑定、<c>/api/oss-sign</c> 登记表引用，<b>不能改值</b>；本类是并行的第二个标识，
/// 服务端优先用它判信誉，缺失时才退回旧设备码。
/// </para>
///
/// <para>
/// 稳定性：改机器名、改用户名、换网卡、换 IP、换硬盘挂载顺序都不变。
/// 变化点只有「装系统 / 格式系统盘 / 换主板（TPM 或 UEFI 变量随之变化）」。
/// 全部信号都取不到时退回 <see cref="DeviceFingerprint.Value"/>，保证仍有一个跨启动稳定的值。
/// </para>
/// </summary>
public static class DeviceIdentity
{
    /// <summary>请求头名。与服务端 <c>functions/api/[[default]].js</c> 的 <c>devIdOf()</c> 对应。</summary>
    public const string Header = "X-CSH-Did";

    /// <summary>哈希前缀。改这个字符串等于把全网设备标识全部换新，非必要不要动。</summary>
    private const string Salt = "csh-device-v1";

    private static readonly object Gate = new();

    private static string? _value;
    private static string _source = "pending";
    private static string _detail = "";

    /// <summary>16 位十六进制的小写设备标识（进程内只算一次）。</summary>
    public static string Value
    {
        get
        {
            lock (Gate)
            {
                if (_value is null) Resolve();
                return _value!;
            }
        }
    }

    /// <summary>这个值是用哪条路径算出来的，供关于页 / 日志排障。</summary>
    public static string Source
    {
        get
        {
            lock (Gate)
            {
                if (_value is null) Resolve();
                return _source;
            }
        }
    }

    /// <summary>取值过程的补充说明（系统标识那条路的成败原因），日志用。</summary>
    public static string Detail
    {
        get
        {
            lock (Gate)
            {
                if (_value is null) Resolve();
                return _detail;
            }
        }
    }

    /// <summary>日志用的一行描述，例如 <c>3b1f9c0d7a2e4f81（系统标识 + 注册表）</c>。</summary>
    public static string Describe() =>
        $"{Value}（{Source}{(string.IsNullOrEmpty(Detail) ? "" : "，" + Detail)}）";

    /// <summary>
    /// 启动时预热一次：激活 WinRT 工厂是毫秒级但需要一次 DLL 加载，
    /// 放在主窗口构造里会顶到首帧。由启动流程在后台线程调一次即可；
    /// 不调也不影响功能（首次用时惰性计算）。
    /// </summary>
    public static void WarmUp()
    {
        try { _ = Value; } catch { }
    }

    private static void Resolve()
    {
        // ── ① Windows 10 1607+：官方口径 ──────────────────────────────
        if (SystemIdentification.TryGetPublisherId(out var publisherId, out var origin, out var detail))
        {
            _value = Hash("sid:" + Convert.ToHexString(publisherId));
            _source = "系统标识 + " + OriginText(origin);
            _detail = detail;
            try { Core.AppLog.Info("device", $"设备标识｜值={_value}｜来源={_source}｜依据={detail}"); } catch { }
            return;
        }

        // ── ② Win7 / 8 / 8.1 或上面取不到：本地等效指纹 ────────────────
        string? machineGuid = null;
        string? volumeSerial = null;

        try { machineGuid = ReadMachineGuid(); } catch { }
        try { volumeSerial = ReadSystemVolumeSerial(); } catch { }

        if (machineGuid is null && volumeSerial is null)
        {
            // 三级信号都读不到（极罕见的受限环境）：退回旧设备码，至少跨启动稳定
            _value = DeviceFingerprint.Value;
            _source = "退回旧设备码";
            _detail = detail;
            return;
        }

        _value = Hash(string.Join('|',
            "mg:" + (machineGuid ?? "-"),
            "vs:" + (volumeSerial ?? "-")));

        _source = machineGuid is not null
            ? volumeSerial is not null ? "安装码 + 卷序列号" : "安装码"
            : "卷序列号";
        _detail = detail;

        try { Core.AppLog.Info("device", $"设备标识｜值={_value}｜来源={_source}｜系统标识=不可用｜依据={detail}"); } catch { }
    }

    private static string Hash(string material)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + "|" + material));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string OriginText(SystemIdentification.Origin origin) => origin switch
    {
        SystemIdentification.Origin.Tpm => "TPM",
        SystemIdentification.Origin.Uefi => "UEFI",
        SystemIdentification.Origin.Registry => "注册表",
        _ => "无",
    };

    /// <summary>
    /// <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> —— 安装 Windows 时生成，
    /// 重装系统才变。显式按 64 位视图读，避免 32 位进程被 WOW64 重定向到别处。
    /// </summary>
    private static string? ReadMachineGuid()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var guid = key?.GetValue("MachineGuid") as string;
                if (!string.IsNullOrWhiteSpace(guid) && guid.Trim().Length >= 16) return guid.Trim();
            }
            catch { /* 换下一个视图 */ }
        }

        return null;
    }

    /// <summary>系统盘卷序列号 —— 与 MachineGuid 互补：重装系统会变（若同时格式化），换硬件不变。</summary>
    private static string? ReadSystemVolumeSerial()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(root)) return null;

        if (!GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0)) return null;
        return serial == 0 ? null : serial.ToString("X8");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}
