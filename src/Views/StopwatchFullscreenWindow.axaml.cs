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
/// 全屏秒表窗口：铺满屏幕显示秒表时间，按 Esc / 双击屏幕 / 右上角那颗按钮退出。
///
/// ⚠️ 它是**只读投影**：时间由 <see cref="Show"/> 传进来的取时函数提供，窗口自己不含任何计时状态。
/// 好处是浮窗版秒表和应用端秒表能共用同一个窗口，不会出现"两个时间对不上"。
///
/// ⚠️ **同一时刻只允许存在一个**（跟 <see cref="ClockFullscreenWindow"/> 同一条铁律）：
/// 已经开着就只把它拉到前台，不叠新的 —— 触屏上连点几下就叠出十几个全屏窗口，
/// 现场反馈原话就是「点了一下然后卡死了/关不掉」。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · 原版 <c>Microsoft.UI.Dispatching.DispatcherQueueTimer</c> + <c>IsRepeating=true</c>
///     → Avalonia <see cref="DispatcherTimer"/>（天生重复触发，无 IsRepeating / IsRunning，
///     判断是否在跑改用 <see cref="DispatcherTimer.IsEnabled"/>，见 <c>RevealExit</c>）。
///   · 原版 <c>AppWindow.SetPresenter(FullScreen)</c> + <c>MoveAndResize</c> 铺满物理屏
///     → Avalonia <see cref="Window.WindowState"/> = <see cref="WindowState.FullScreen"/>（语义一致）。
///   · 原版 <c>WindowNative.GetWindowHandle</c> → <see cref="Backdrop.TryGetHwnd"/>。
///   · <c>WindowChrome.RemoveBorder</c> 原样保留（内部已改成走 Platform.NativeMethods）。
///   · 事件类型：<c>KeyRoutedEventArgs</c> → <see cref="KeyEventArgs"/>，
///     <c>DoubleTappedRoutedEventArgs</c>/<c>TappedRoutedEventArgs</c> → <see cref="TappedEventArgs"/>，
///     <c>WindowEventArgs</c> → <see cref="EventArgs"/>。
/// </summary>
public sealed partial class StopwatchFullscreenWindow : Window
{
    /// <summary>当前开着的这一个（没有则为 null）。</summary>
    private static StopwatchFullscreenWindow? _current;

    /// <summary>取当前已计毫秒（由调用方提供）。</summary>
    private readonly Func<long> _elapsed;

    /// <summary>源秒表是否正在跑 —— 只用来显示"计时中/已暂停"。</summary>
    private readonly Func<bool> _running;

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _exitTimer;

    /// <summary>退出入口的淡出倒计时（毫秒）。</summary>
    private int _exitRemainMs;

    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    /// <summary>
    /// 唯一入口：开一个全屏秒表。已经开着就复用（拉到最前），**不会**再多出一个窗口。
    /// </summary>
    public static void Show(Func<long> elapsed, Func<bool> running)
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

        var window = new StopwatchFullscreenWindow(elapsed, running);
        _current = window;
        window.Start();
    }

    public StopwatchFullscreenWindow(Func<long> elapsed, Func<bool> running)
    {
        _elapsed = elapsed;
        _running = running;
        InitializeComponent();
        Title = "全屏秒表";

        // 原版 DispatcherQueue.CreateTimer() + Interval=50ms + IsRepeating=true
        // → Avalonia DispatcherTimer（重复触发是天生行为，没有 IsRepeating 属性）
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();

        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _exitTimer.Tick += (_, _) => FadeExit();

        // 「使用全屏时钟背景设置」开着 → 铺时钟那套背景；关着保持固定深底。见 Views/ClockBackdrop.cs。
        if (App.Settings.Current.TimerUseClockBackground)
            ClockBackdrop.Apply(Root, BgImage, BgVeil, null, TimeText, CsText, StateText);

        Closed += OnClosed;
    }

    public void Start()
    {
        // 原版 Activate() 之前窗口尚未 Show（WinUI 里 AppWindow 已存在）；
        // Avalonia 必须先 Show 再置全屏/抢焦点，否则 WindowState 与 Focus 不生效。
        Show();

        try
        {
            // 铺满整个物理屏幕（差几个像素就会看起来"没盖住"）。
            // Avalonia 的 FullScreen 状态走无边框全屏，任务栏也被盖住 —— 与原版 AppWindow 全屏语义一致。
            WindowState = WindowState.FullScreen;

            // ⚠️ Win11 上全屏窗口默认还带圆角 + 一圈细边框 → 屏幕四角/边上会露出底下的桌面，
            // 看着像"屏幕外面套了一圈"。这里把圆角关掉、边框设成无色。
            // （Win10 / Win7 不认识这些属性，调用失败无所谓。）
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd != IntPtr.Zero) WindowChrome.RemoveBorder(hwnd, rounded: false, dark: true);
        }
        catch { /* 全屏失败也能当普通窗口用 */ }

        Tick();
        _timer.Start();
        RevealExit();

        // 显示一帧后再落焦点（窗口还没起来时 Focus 不生效）
        Dispatcher.UIThread.Post(() => Root.Focus());
    }

    private void Tick()
    {
        try
        {
            var ms = _elapsed();
            var hours = ms / 3_600_000;
            var minutes = ms % 3_600_000 / 60_000;
            var seconds = ms % 60_000 / 1000;
            var hundredths = ms % 1000 / 10;

            TimeText.Text = hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
            CsText.Text = $".{hundredths:00}";
            StateText.Text = _running() ? "计时中" : (ms > 0 ? "已暂停" : "未开始");
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

    // ⚠️ 忠实照搬原版：这两个方法在原版 Stopwatch 里就**没有**接到 XAML / 构造函数上
    //    （对比 TimerFullscreenWindow 是在构造函数里显式挂的）。这里保留原状，不做"顺手修好"。
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
