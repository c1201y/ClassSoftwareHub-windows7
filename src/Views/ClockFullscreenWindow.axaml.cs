using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 全屏时钟窗口：铺满屏幕，按 Esc / 双击屏幕 / 右上角那颗按钮退出；外观完全跟随页面里的设置。
///
/// 同一时刻只允许存在一个：统一走 <see cref="Show"/>，已开着时就地更新设置并拉到前台，不再新建实例。
///
/// ⚠️ 移植说明：原版走 <c>AppWindow.SetPresenter(FullScreen)</c>；
///    Avalonia 里对应 <see cref="Window.WindowState"/> = <see cref="WindowState.FullScreen"/>，
///    铺满物理屏幕、盖住任务栏的语义一致（见 <c>Start</c>）。
/// </summary>
public sealed partial class ClockFullscreenWindow : Window
{
    /// <summary>当前开着的这一个（没有则为 null）。整个进程唯一的全屏时钟。</summary>
    private static ClockFullscreenWindow? _current;

    private ClockSettings _settings;
    private bool _dark;
    private readonly DispatcherTimer _timer;

    /// <summary>当前铺在窗口上的背景位图；替换或关窗时释放（Bitmap 持有非托管像素内存）。</summary>
    private Bitmap? _bgBitmap;

    /// <summary>退出入口的淡出倒计时（毫秒）。到 0 就藏起来，别一直压着时钟。</summary>
    private readonly DispatcherTimer _exitTimer;
    private int _exitRemainMs;

    /// <summary>退出按钮可见时给多久（毫秒）；末段用来做淡出。</summary>
    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    /// <summary>
    /// 唯一入口：开一个全屏时钟。已经开着就复用（拉到最前），**不会**再多出一个窗口。
    ///
    /// <paramref name="settings"/> 传 null = 仅将已开着的窗口调到前台，不更新外观
    /// （调用方没有可用的设置时使用，避免覆盖已调好的配置）。
    /// </summary>
    public static void Show(ClockSettings? settings, bool darkTheme)
    {
        if (_current is not null)
        {
            try
            {
                if (settings is not null) _current.UpdateSettings(settings, darkTheme);
                _current.Activate();
                _current.RevealExit();
                return;
            }
            catch
            {
                // 极端情况下（窗口已销毁但字段没清）兜一下：丢掉引用重新开
                _current = null;
            }
        }

        var window = new ClockFullscreenWindow(settings ?? new ClockSettings(), darkTheme);
        _current = window;
        window.Start();
    }

    public ClockFullscreenWindow(ClockSettings settings, bool darkTheme)
    {
        _settings = settings;
        _dark = darkTheme;
        InitializeComponent();
        Title = "全屏时钟";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();

        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _exitTimer.Tick += (_, _) => FadeExit();

        Closed += OnClosed;
    }

    public void Start()
    {
        Show();

        try
        {
            // 铺满整个物理屏幕（差几个像素就会看起来"没盖住"）。
            // Avalonia 的 FullScreen 状态走无边框全屏，任务栏也被盖住 —— 与原版 AppWindow 全屏语义一致。
            WindowState = WindowState.FullScreen;

            // Win11 上全屏窗口默认带圆角与细边框，会在屏幕四角露出底下桌面；
            // 关闭圆角并将边框设为无色（Win10 / Win7 不识别这些属性，调用失败可忽略）。
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd != IntPtr.Zero) WindowChrome.RemoveBorder(hwnd, rounded: false, dark: _dark);
        }
        catch { /* 全屏失败也能当普通窗口用 */ }

        Apply();

        Tick();
        _timer.Start();

        // 首次显示时短暂提示退出方式（触屏环境无 Esc 键）
        RevealExit();

        // 显示一帧后再落焦点（窗口还没起来时 Focus 不生效）
        Dispatcher.UIThread.Post(() => Root.Focus());
    }

    /// <summary>就地换一套外观设置（已经在全屏了就改这一份，不新开窗口）。</summary>
    private void UpdateSettings(ClockSettings settings, bool darkTheme)
    {
        _settings = settings;
        _dark = darkTheme;
        Apply();
        Tick();
    }

    private bool HasPhoto => !string.IsNullOrWhiteSpace(_settings.BackgroundImagePath)
                             && File.Exists(_settings.BackgroundImagePath);

    private void Apply()
    {
        var face = new SolidColorBrush(ClockRender.FaceColor(_settings, _dark, HasPhoto));
        TimeText.Foreground = face;
        SecText.Foreground = face;
        DateText.Foreground = face;

        if (HasPhoto)
        {
            try
            {
                var next = new Bitmap(_settings.BackgroundImagePath);
                var old = _bgBitmap;
                _bgBitmap = next;
                Root.Background = new ImageBrush
                {
                    Source = next,
                    Stretch = Stretch.UniformToFill,
                };
                old?.Dispose();
            }
            catch
            {
                Root.Background = ClockRender.BaseBrush(_settings, _dark);
            }
        }
        else
        {
            Root.Background = ClockRender.BaseBrush(_settings, _dark);
        }

        Veil.Background = ClockRender.VeilBrush(_settings);

        // 走 NormalizeFont：旧存档里的 Georgia 已从候选表剔除，直接 new 出来还会继续抖
        // （见 ClockRender.FontFamilies / NormalizeFont 的说明）。
        var faceFont = new FontFamily(ClockRender.NormalizeFont(_settings.FontFamily));
        TimeText.FontFamily = faceFont;
        SecText.FontFamily = faceFont;
        DateText.FontFamily = faceFont;
    }

    private void Tick()
    {
        var now = DateTime.Now;
        TimeText.Text = ClockRender.TimeText(now, _settings.Hour12);
        SecText.Text = ClockRender.SecText(now);
        DateText.Text = ClockRender.DateText(now);
        SecText.IsVisible = _settings.ShowSeconds;
        DateText.IsVisible = _settings.ShowDate;
        Layout();
    }

    private void Root_SizeChanged(object? sender, SizeChangedEventArgs e) => Layout();

    /// <summary>按屏幕尺寸算大字号（网页版：有时/分时 min(30vw,46vh)，带秒时 min(22vw,34vh)）。</summary>
    private void Layout()
    {
        var w = Root.Bounds.Width;
        var h = Root.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var baseSize = (_settings.ShowSeconds ? Math.Min(w * 0.22, h * 0.34) : Math.Min(w * 0.30, h * 0.46))
                       * _settings.Scale;
        if (baseSize <= 1) return;

        TimeText.FontSize = baseSize;
        SecText.FontSize = baseSize * 0.5;
        SecText.Margin = new Thickness(0, 0, 0, baseSize * 0.18);
        DateText.FontSize = Math.Max(16, Math.Min(w * 0.024, h * 0.04) * _settings.Scale);
    }

    // ══════════ 退出入口 ══════════

    /// <summary>把「怎么退出」那块亮出来，并重新开始倒计时。</summary>
    private void RevealExit()
    {
        _exitRemainMs = ExitHoldMs;
        ExitBar.Opacity = 1;
        ExitBar.IsHitTestVisible = true;
        if (!_exitTimer.IsEnabled) _exitTimer.Start();
    }

    /// <summary>倒计时收尾：最后一段淡出，到点就彻底藏掉（并交出命中测试，别挡住双击退出）。</summary>
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
        // 鼠标划过 / 手指按一下都算「他在找退出」，重新亮出来
        if (_exitRemainMs < ExitHoldMs - 300 || ExitBar.Opacity <= 0) RevealExit();
    }

    private void Root_PointerPressed(object? sender, PointerPressedEventArgs e)
        => Root_PointerMoved(sender, e);

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
        _bgBitmap?.Dispose();
        _bgBitmap = null;
    }
}
