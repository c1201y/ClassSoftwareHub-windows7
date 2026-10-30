using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 动画期间把系统计时器分辨率提到 1ms，离开作用域自动还原。
///
/// <para>
/// Windows 默认计时器粒度约 <b>15.6ms</b>：<c>DispatcherTimer(16ms)</c> 的实际 tick
/// 会抖到 20~31ms、忙时还会合并，窗口位移动画看着就是一跳一跳的。
/// 只在动画这 200~400ms 内抬高分辨率（<c>timeBeginPeriod(1)</c>），结束立刻
/// <c>timeEndPeriod(1)</c> —— 不长期占着系统计时器，不影响待机功耗。
/// </para>
///
/// <para>
/// ⚠️ 别用 <c>TopLevel.RequestAnimationFrame</c> 替代它：Avalonia 11.2.8 的帧回调
/// 跟着“这一次渲染”走，窗口只是被 SetWindowPos 挪动、内容没有脏区时合成器不一定刷帧，
/// 实测 220ms 的滑动只落了 6 帧（总时长被拖到 350ms）。按时间驱动的计时器才稳。
/// </para>
/// </summary>
internal readonly struct TimerResolutionScope : IDisposable
{
    private readonly bool _raised;

    private TimerResolutionScope(bool raised) => _raised = raised;

    /// <summary>抬高计时器分辨率；不支持的平台上返回空作用域（动画照旧，只是精度差些）。</summary>
    public static TimerResolutionScope Raise()
    {
        try { return new TimerResolutionScope(timeBeginPeriod(1) == 0); }
        catch { return default; }
    }

    public void Dispose()
    {
        if (!_raised) return;
        try { timeEndPeriod(1); } catch { }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint period);
}
