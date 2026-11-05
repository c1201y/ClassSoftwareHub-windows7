using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <c>Windows.System.Profile.SystemIdentification.GetSystemIdForPublisher()</c> 的手写绑定。
///
/// <para>
/// 为什么手写：本工程是 <c>net6.0-windows7.0</c>，没有 CsWinRT 投影，也不能为了一个取值点
/// 把 <c>TargetFramework</c> 抬到需要 WinRT 支持的档位 —— Win7 是主目标系统。
/// 所以这里只做「一次静态方法调用」所需的最小 WinRT 互操作：
/// <c>RoGetActivationFactory</c> 拿工厂 → 虚表槽 6 调 <c>GetSystemIdForPublisher</c>
/// → 从返回的 <c>ISystemIdentificationInfo</c> 取 <c>Id</c> 与 <c>Source</c>
/// → 经 <c>IBufferByteAccess</c> 读字节。
/// </para>
///
/// <para>
/// IID 与槽位来源（两处独立核对过，改之前请重新核对，错一个 GUID 就是静默失败）：
/// <list type="bullet">
///   <item><c>ISystemIdentificationStatics</c> = <c>5581F42A-D3DF-4D93-A37D-C41A616C6D01</c>
///         （Windows SDK <c>windows.system.profile.h</c>，UniversalApiContract 3.0；
///          Wine 的 <c>RoGetActivationFactory</c> 日志里也是这个值）</item>
///   <item>虚表槽位：IUnknown 占 0–2，IInspectable 占 3–5，业务方法从 6 起 ——
///         与 VB/COM 社区那套 <c>IDX_GetSystemIdForPublisher = 6</c> 一致</item>
///   <item><c>IBufferByteAccess</c> = <c>905A0FEF-BC53-11DF-8C49-001E4FC686DA</c>（<c>robuffer.h</c>）</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 该 API 最低 Windows 10 1607（build 14393），而且 <b>Win7 上连 combase.dll 里的
/// RoGetActivationFactory 都没有</b>。调用方必须先按版本卡口，本类内部也再做一次兜底捕获，
/// 保证在 Win7 上只是「返回 false」，不会抛异常、不会拖慢启动。
/// </para>
/// </summary>
internal static class SystemIdentification
{
    private const string ClassId = "Windows.System.Profile.SystemIdentification";

    /// <summary>该 API 首次出现的 build（Windows 10 1607）。</summary>
    public const int MinimumBuild = 14393;

    private static readonly Guid IID_ISystemIdentificationStatics =
        new("5581F42A-D3DF-4D93-A37D-C41A616C6D01");

    private static readonly Guid IID_IBufferByteAccess =
        new("905A0FEF-BC53-11DF-8C49-001E4FC686DA");

    // ── 虚表槽位（IUnknown 0–2、IInspectable 3–5 之后才是业务方法）──
    private const int SlotGetSystemIdForPublisher = 6;
    private const int SlotInfoGetId = 6;
    private const int SlotInfoGetSource = 7;
    private const int SlotBufferGetLength = 7;
    private const int SlotBufferByteAccessBuffer = 3;

    /// <summary>标识来源，对应 <c>SystemIdentificationSource</c> 枚举。</summary>
    public enum Origin
    {
        None = 0,
        Tpm = 1,
        Uefi = 2,
        Registry = 3,
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSystemIdForPublisherFn(IntPtr self, out IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIdFn(IntPtr self, out IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSourceFn(IntPtr self, out int source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetLengthFn(IntPtr self, out uint length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferBytesFn(IntPtr self, out IntPtr bytes);

    // ⚠️ 必须 ExactSpelling：combase 里是 WindowsCreateString（不是 …W / …A），
    //    加了 CharSet 会自动补后缀，那就找不到入口点了。
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    /// <summary>
    /// 取发布者的系统标识。成功返回 true 并给出 32 字节 ID 与来源；
    /// 任何一步不成立（系统太老、没有 combase、没有 TPM/UEFI、COM 调用失败）都返回 false，不抛。
    /// </summary>
    public static bool TryGetPublisherId(out byte[] id, out Origin origin, out string detail)
    {
        id = Array.Empty<byte>();
        origin = Origin.None;
        detail = "";

        if (!IsSupportedByOs()) { detail = "系统低于 Windows 10 1607"; return false; }

        IntPtr hstring = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        IntPtr info = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        IntPtr byteAccess = IntPtr.Zero;

        try
        {
            var hr = WindowsCreateString(ClassId, ClassId.Length, out hstring);
            if (hr < 0 || hstring == IntPtr.Zero) { detail = Hr("WindowsCreateString", hr); return false; }

            var iid = IID_ISystemIdentificationStatics;
            hr = RoGetActivationFactory(hstring, ref iid, out factory);
            if (hr < 0 || factory == IntPtr.Zero) { detail = Hr("RoGetActivationFactory", hr); return false; }

            var getForPublisher = Bind<GetSystemIdForPublisherFn>(factory, SlotGetSystemIdForPublisher);
            hr = getForPublisher(factory, out info);
            if (hr < 0 || info == IntPtr.Zero) { detail = Hr("GetSystemIdForPublisher", hr); return false; }

            var getSource = Bind<GetSourceFn>(info, SlotInfoGetSource);
            if (getSource(info, out var source) >= 0) origin = (Origin)source;

            // Source=None 表示系统既没有 TPM 也没有 UEFI、且拿不到注册表兜底 —— 这个 ID 不可信
            if (origin == Origin.None) { detail = "系统没有可用的标识来源（Source=None）"; return false; }

            var getId = Bind<GetIdFn>(info, SlotInfoGetId);
            hr = getId(info, out buffer);
            if (hr < 0 || buffer == IntPtr.Zero) { detail = Hr("get_Id", hr); return false; }

            var getLength = Bind<GetLengthFn>(buffer, SlotBufferGetLength);
            hr = getLength(buffer, out var length);
            if (hr < 0 || length == 0 || length > 1024) { detail = $"缓冲区长度异常（{length}）"; return false; }

            var bufferIid = IID_IBufferByteAccess;
            hr = Marshal.QueryInterface(buffer, ref bufferIid, out byteAccess);
            if (hr < 0 || byteAccess == IntPtr.Zero) { detail = Hr("QI IBufferByteAccess", hr); return false; }

            var getBytes = Bind<GetBufferBytesFn>(byteAccess, SlotBufferByteAccessBuffer);
            hr = getBytes(byteAccess, out var raw);
            if (hr < 0 || raw == IntPtr.Zero) { detail = Hr("IBufferByteAccess::Buffer", hr); return false; }

            var bytes = new byte[length];
            Marshal.Copy(raw, bytes, 0, bytes.Length);

            id = bytes;
            detail = $"来源={origin}，{length} 字节";
            return true;
        }
        catch (DllNotFoundException ex) { detail = "combase.dll 不可用：" + ex.Message; return false; }
        catch (EntryPointNotFoundException ex) { detail = "combase 缺少 WinRT 入口：" + ex.Message; return false; }
        catch (Exception ex) { detail = ex.GetType().Name + "：" + ex.Message; return false; }
        finally
        {
            Release(byteAccess);
            Release(buffer);
            Release(info);
            Release(factory);
            if (hstring != IntPtr.Zero)
            {
                try { WindowsDeleteString(hstring); } catch { }
            }
        }
    }

    /// <summary>
    /// 版本卡口：<c>--simulate-win7</c> 时一律当作不支持（正好用来验证 Win7 兜底路径）。
    /// </summary>
    private static bool IsSupportedByOs()
    {
        try
        {
            return OsInfo.IsWindows10OrLater
                   && !OsInfo.SimulateWin7
                   && OsInfo.Version.Build >= MinimumBuild;
        }
        catch { return false; }
    }

    private static T Bind<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var entry = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(entry);
    }

    private static void Release(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        try { Marshal.Release(ptr); } catch { }
    }

    private static string Hr(string what, int hr) => $"{what} 失败（HRESULT 0x{hr:X8}）";
}
