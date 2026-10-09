using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 全屏倒计时窗口：铺满屏幕显示源计时器的剩余时间，按 Esc / 双击屏幕 / 右上角那颗按钮退出。
///
/// ⚠️ 它是**只读投影**：剩余/总时长由 <see cref="Show"/> 传进来的取值函数提供，窗口自己不含计时状态，
/// 到点响铃、暂停这些仍然归源控件管 —— 浮窗收起/再开，全屏这边跟着走，不会出现两份时间。
///
/// ⚠️ **同一时刻只允许存在一个**（跟 <see cref="StopwatchFullscreenWindow"/> 同一条铁律）：
/// 已经开着就只把它拉到前台，不叠新的 —— 触屏上连点几下就叠出十几个全屏窗口。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · <c>DispatcherQueueTimer</c> → <see cref="DispatcherTimer"/>（重复触发天生，判断在跑用 IsEnabled）。
///   · <c>AppWindow.SetPresenter(FullScreen)</c> → <see cref="Window.WindowState"/> = FullScreen。
///   · <c>WindowNative.GetWindowHandle</c> → <see cref="Backdrop.TryGetHwnd"/>。
///   · 事件类型同 <see cref="StopwatchFullscreenWindow"/>：KeyRoutedEventArgs→KeyEventArgs、
///     Tapped/DoubleTappedRoutedEventArgs→TappedEventArgs、WindowEventArgs→EventArgs。
/// </summary>
public sealed partial class TimerFullscreenWindow : Window
{
    /// <summary>当前开着的这一个（没有则为 null）。</summary>
    private static TimerFullscreenWindow? _current;

    /// <summary>取当前剩余毫秒（由调用方提供）。</summary>
    private readonly Func<long> _remain;

    /// <summary>取本轮总时长毫秒（算进度条用）。</summary>
    private readonly Func<long> _total;

    /// <summary>源计时器是否正在跑。</summary>
    private readonly Func<bool> _running;

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _exitTimer;

    /// <summary>退出入口的淡出倒计时（毫秒）。</summary>
    private int _exitRemainMs;

    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    /// <summary>唯一入口：开一个全屏倒计时。已经开着就复用（拉到最前），**不会**再多出一个窗口。</summary>
    public static void Show(Func<long> remain, Func<long> total, Func<bool> running)
    {
        if (_current is not null)
        {
            try
            {
                _current.Activate();
                _current.RevealExit();
                return;
            }
            catch
            {
                // 窗口已销毁但字段没清 —— 丢掉引用重新开
                _current = null;
            }
        }

        var window = new TimerFullscreenWindow(remain, total, running);
        _current = window;
        window.Start();
    }

    public TimerFullscreenWindow(Func<long> remain, Func<long> total, Func<bool> running)
    {
        _remain = remain;
        _total = total;
        _running = running;
        InitializeComponent();
        Title = "全屏倒计时";

        // 原版 DispatcherQueue.CreateTimer() + IsRepeating=true → Avalonia DispatcherTimer
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();

        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _exitTimer.Tick += (_, _) => FadeExit();

        // 鼠标一动 / 手指一碰 → 退出入口重新亮起来
        // （原版即在此显式挂线；StopwatchFullscreenWindow 那边没挂，属原版差异，照搬不修）
        Root.PointerMoved += Root_PointerMoved;
        Root.Tapped += Root_Tapped;

        // 「使用全屏时钟背景设置」开着 → 把时钟那套背景（图 / 蒙版 / 底色 / 字色）铺上；
        // 关着就保持上面那个固定的深底。见 Views/ClockBackdrop.cs。
        if (App.Settings.Current.TimerUseClockBackground)
            ClockBackdrop.Apply(Root, BgImage, BgVeil, Bar, TimeText, StateText);

        Closed += OnClosed;
    }

    public void Start()
    {
        // 原版 Activate() 之前窗口尚未 Show；Avalonia 必须先 Show 再置全屏/抢焦点。
        Show();

        try
        {
            // 铺满整个物理屏幕（差几个像素就会看起来"没盖住"）。
            WindowState = WindowState.FullScreen;

            // Win11 全屏窗口默认带圆角 + 一圈细边框 → 关掉，免得四角露桌面。
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd != IntPtr.Zero) WindowChrome.RemoveBorder(hwnd, rounded: false, dark: true);
        }
        catch { /* 全屏失败也能当普通窗口用 */ }

        Tick();
        _timer.Start();
        RevealExit();

        Dispatcher.UIThread.Post(() => Root.Focus());
    }

    private void Tick()
    {
        try
        {
            var ms = Math.Max(0, _remain());
            var hours = ms / 3_600_000;
            var minutes = ms % 3_600_000 / 60_000;
            var seconds = ms % 60_000 / 1000;

            TimeText.Text = hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";

            var total = _total();
            Bar.Value = total > 0 ? Math.Clamp(ms / (double)total, 0, 1) * 100 : 0;

            StateText.Text = _running() ? "计时中"
                : ms <= 0 && total > 0 ? "到点了"
                : ms > 0 ? "已暂停" : "未开始";
        }
        catch
        {
            // 取时的那一端已经被关掉了 —— 停表，别让空窗一直挂在屏上
            _timer.Stop();
            Close();
        }
    }

    // ══════════ 退出入口 ══════════

    private void RevealExit()
    {
        _exitRemainMs = ExitHoldMs;
        ExitBar.Opacity = 1;
        ExitBar.IsHitTestVisible = true;
        // 原版 _exitTimer.IsRunning → Avalonia DispatcherTimer.IsEnabled
        if (!_exitTimer.IsEnabled) _exitTimer.Start();
    }

    private void FadeExit()
    {
        _exitRemainMs -= 60;
        if (_exitRemainMs > 0)
        {
            ExitBar.Opacity = _exitRemainMs >= ExitFadeMs ? 1 : _exitRemainMs / (double)ExitFadeMs;
            return;
        }

        _exitTimer.Stop();
        ExitBar.Opacity = 0;
        ExitBar.IsHitTestVisible = false;
    }

    private void Root_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_exitRemainMs < ExitHoldMs - 300 || ExitBar.Opacity <= 0) RevealExit();
    }

    private void Root_Tapped(object? sender, TappedEventArgs e) => RevealExit();

    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();

    private void Root_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Root_DoubleTapped(object? sender, TappedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs args)
    {
        _timer.Stop();
        _exitTimer.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
