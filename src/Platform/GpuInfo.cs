using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 显卡能力的直接探测 —— 用来决定 Avalonia 该从哪条渲染后端开始试。
///
/// 背景（实机日志 2026-10-01 目标 Win7 机器）：
///   ANGLE 后端是**架在 D3D11 上**的。这台机器没有 D3D11 适配器
///   （D3D11CreateDevice 返回 0x887A0004 = DXGI_ERROR_UNSUPPORTED），
///   WGL(OpenGL) 也起不来（只有微软的 GDI 通用 opengl32，OpenGL 1.1，缺入口点）。
///   于是每次启动都要先在 ANGLE 上撞一次墙、再在 WGL 上撞一次墙，白等两秒多，
///   还在控制台刷出一大段 `No adapters found` / `Unknown requested PlatformApi` 的英文报错 ——
///   对排障的人反而是噪音。
///
///   ANGLE 和 D3D11 是绑死的，所以**没有 D3D11 就没有必要试 ANGLE**，
///   直接从 WGL 试起、落到软件渲染。这是纯收益，不改变任何能跑通的结果。
/// </summary>
internal static class GpuInfo
{
    private static bool _probed;
    private static bool _available;
    private static int _hr;

    /// <summary>本机硬件 D3D11 设备能不能建出来。探测失败时保守返回 true（照常走完整降级链）。</summary>
    public static bool IsD3D11Available
    {
        get
        {
            Probe();
            return _available;
        }
    }

    /// <summary>人话版结论，给诊断横幅用。</summary>
    public static string Describe()
    {
        Probe();
        return _available
            ? "可用"
            : $"⚠ 不可用（HRESULT=0x{_hr:X8}）—— 这台机器的显卡/驱动不支持 D3D11，"
              + "已跳过 ANGLE 后端，直接走 OpenGL → 软件渲染";
    }

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;

        try
        {
            // DriverType: 1 = D3D_DRIVER_TYPE_HARDWARE，SDKVersion: 7 = D3D11_SDK_VERSION
            _hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0,
                IntPtr.Zero, 0, 7, out var device, out _, out var context);

            _available = _hr == 0;

            // 只是探测，用完立刻还回去，别把 D3D 设备对象挂在进程里
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
        catch (DllNotFoundException)
        {
            // 系统里连 d3d11.dll 都没有（Win7 未装平台更新）→ 明确不可用
            _available = false;
            _hr = unchecked((int)0x8007007E);   // ERROR_MOD_NOT_FOUND
        }
        catch
        {
            // 其它异常（权限、沙箱等）→ 保守当作可用，让 Avalonia 自己按降级链试
            _available = true;
            _hr = 0;
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);
}
