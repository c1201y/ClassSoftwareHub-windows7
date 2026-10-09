using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 浮窗「贴着边滑」的按帧动画（窗口级 Move，跟侧边栏滑入同一套手感）。
///
/// 位置动画只能这样一帧一帧地 Move（16ms 一帧 ≈ 60fps）——窗口位置不归合成器管，
/// 想让它"动得丝滑"就只有把每一帧的位置算准：缓动 + 整数像素，别抖。
///
/// 每个浮窗持有一个自己的实例，互相不干扰；Stop 会让在跑的那一波立刻作废（连同它的回调）。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：原版第一参数是 <c>AppWindow</c>；Avalonia 没有对应物，
///    改收目标窗口的 HWND，每帧用 Win32 SetWindowPos 挪（无边框窗口外框 = 客户区，
///    物理像素语义与原版一致；P/Invoke 走 <c>Platform.NativeMethods</c>）。
/// </summary>
public sealed class FlyoutSlider
{
    /// <summary>滑入用的时长（ms）：稍长一点、ease-out，看着像"甩进来停住"。</summary>
    public const double SlideInMs = 220;

    /// <summary>滑出用的时长（ms）：比滑入快，收起来要利索。</summary>
    public const double SlideOutMs = 150;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private DispatcherTimer? _timer;
    private DispatcherTimer? _watchdog;
    private int _epoch;

    /// <summary>正在滑（滑入或滑出）。调用方据此避免在动画中途改窗口位置。</summary>
    public bool IsRunning { get; private set; }

    public void Stop()
    {
        _epoch++;
        _timer?.Stop();
        _timer = null;
        _watchdog?.Stop();
        _watchdog = null;
        IsRunning = false;
    }

    /// <param name="hwnd">目标窗口句柄（每帧 SetWindowPos 挪它）。</param>
    /// <param name="easeIn">true = 起步慢、越走越快（滑出用）；false = 起步快、末段收着（滑入用）。</param>
    public void Run(IntPtr hwnd, PixelPoint from, PixelPoint to, double ms, Action? done = null, bool easeIn = false)
    {
        Stop();
        IsRunning = true;
        var epoch = ++_epoch;
        var sw = Stopwatch.StartNew();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (epoch != _epoch) { timer.Stop(); return; }

            var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
            var e = easeIn ? t * t * t : 1 - Math.Pow(1 - t, 3); // ease-in / ease-out cubic
            if (hwnd != IntPtr.Zero)
                _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    (int)Math.Round(from.X + (to.X - from.X) * e),
                    (int)Math.Round(from.Y + (to.Y - from.Y) * e),
                    0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

            if (t < 1) return;
            timer.Stop();
            _timer = null;
            IsRunning = false;
            if (hwnd != IntPtr.Zero)
                _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);      // 最后一帧对到准确位置
            done?.Invoke();
        };

        _timer = timer;
        timer.Start();

        // 兜底：按帧计时器万一停摆（消息循环被卡住 / 窗口被藏），动画不能卡死不收尾。
        // （原版 WinUI 靠 CompositionTarget.Rendering 每 vsync 一帧 + 同样的 watchdog 兜底；
        //  Avalonia 侧按本仓库既有约定仍走 16ms DispatcherTimer，见类顶部说明。）
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms + 400) };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            if (epoch != _epoch) return;
            Stop();
            if (hwnd != IntPtr.Zero)
                _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            done?.Invoke();
        };
        _watchdog = watchdog;
        watchdog.Start();
    }
}
