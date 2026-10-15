using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 教室喊话窗：远端喊话的全屏大字展示，倒计时到点自动关，Esc / 双击 / 右上角按钮可提前退。
///
/// <para>
/// ⚠️ **同一时刻只允许存在一个**（同 <see cref="MessageFullscreenWindow"/> 的理由：全屏窗叠全屏窗
/// 就是"关不掉"事故）。播报期间又拉到新消息 → 就地换内容、倒计时重走。
/// </para>
///
/// <para>
/// 声音分两个独立开关（设置页）：
///   · <paramref name="quiet"/> = 安静模式：只展示不出声（提示音、朗读全免）；
///   · <paramref name="speak"/> = 朗读：非安静模式下，先响一声铃（<see cref="TimerAlarm"/>，引起注意）
///     再朗读正文（<see cref="Tts"/>）；speak=false 就只有提示音。
/// </para>
///
/// <para>字号算法与全屏留言同源：按"最长一行放得下 92% 宽、总高不超 86%"夹取。</para>
/// </summary>
public sealed partial class AnnounceWindow : Window
{
    private static AnnounceWindow? _current;

    /// <summary>喊话窗当前是否开着（AnnounceService 播报期间跳过轮询的判断位）。</summary>
    public static bool IsShowing => _current is not null;

    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    private readonly DispatcherTimer _exitTimer;      // 退出提示条的亮/淡
    private readonly DispatcherTimer _countdown;      // 自动关窗倒计时
    private int _exitRemainMs;
    private int _totalSec;      // 本次播报的总时长（结算耗时的分母）
    private int _remainSec;

    private AnnounceMessage _msg = new();

    /// <summary>唯一入口：弹一条喊话；已经开着就就地换内容、倒计时重走。</summary>
    public static void Show(AnnounceMessage msg, bool quiet, bool speak)
    {
        try
        {
            if (_current is not null)
            {
                _current.Replace(msg);
                return;
            }

            var window = new AnnounceWindow(msg, quiet, speak);
            _current = window;
            window.Start();
        }
        catch (Exception ex)
        {
            AppLog.Fail("announce", ex, "打开喊话窗失败");
        }
    }

    private AnnounceWindow(AnnounceMessage msg, bool quiet, bool speak)
    {
        _msg = msg;
        _quiet = quiet;
        _speak = speak;

        InitializeComponent();
        Title = "教室喊话";

        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _exitTimer.Tick += (_, _) => FadeExit();

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => CountDown();

        Root.PointerMoved += Root_PointerMoved;
        Root.PointerPressed += Root_PointerPressed;
        Root.Tapped += Root_Tapped;
        Root.SizeChanged += (_, _) => Layout();      // 首帧量到真实尺寸后重排字号

        Closed += OnClosed;
    }

    private readonly bool _quiet;
    private readonly bool _speak;

    private void Start()
    {
        Show();

        try
        {
            WindowState = WindowState.FullScreen;
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd != IntPtr.Zero) WindowChrome.RemoveBorder(hwnd, rounded: false, dark: true);
        }
        catch { /* 全屏失败也能当普通窗口用 */ }

        Render();
        RevealExit();

        // 声音：先响铃引起注意，再朗读正文（安静模式两个都免）。
        // ⚠️ 都在 UI 线程发起 —— Tts 的 COM 实例"谁创建谁使用"，这里创建就一辈子在这条线程用。
        if (!_quiet)
        {
            TimerAlarm.Play();
            if (_speak) Tts.Speak(_msg.Text);
        }

        Dispatcher.UIThread.Post(() => Root.Focus());
    }

    /// <summary>播报期间又来一条：换内容 + 重走倒计时（窗口不动，不闪屏）。</summary>
    private void Replace(AnnounceMessage msg)
    {
        _msg = msg;
        Render();          // 换内容 + 倒计时重走（都在 Render 里）
        RevealExit();

        if (!_quiet && _speak)
        {
            Tts.Speak(msg.Text);   // Speak 自带"清掉上一段再读"，正好接上
        }
    }

    private void Render()
    {
        Body.Text = string.IsNullOrWhiteSpace(_msg.Text) ? "（空喊话）" : _msg.Text;
        Body.FontWeight = Avalonia.Media.FontWeight.Bold;

        var from = (_msg.From ?? "").Trim();
        FromLine.IsVisible = from.Length > 0;
        FromLine.Text = "—— " + from;

        Layout();

        var dur = Math.Clamp(_msg.Duration is >= 5 and <= 300 ? _msg.Duration : 30, 5, 300);
        _totalSec = dur;
        _remainSec = dur;
        UpdateRemainText();
        StartCountdown();
    }

    /// <summary>按屏幕尺寸 + 字数算字号（同 MessageFullscreenWindow.Layout）。</summary>
    private void Layout()
    {
        var w = Root.Bounds.Width;
        var h = Root.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            // 首帧还没量到尺寸：先给个能看的值，SizeChanged 起来后会重算
            Body.FontSize = 64;
            return;
        }

        var lines = (_msg.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var longest = lines.Length == 0 ? 1 : Math.Max(1, lines.Max(VisualLength));

        var byWidth = w * 0.92 / longest;
        var byHeight = h * 0.86 / Math.Max(1, lines.Length) / 1.30;

        var size = Math.Max(16, Math.Min(byWidth, byHeight));
        Body.FontSize = size;
        Body.LineHeight = Math.Round(size * 1.22);
    }

    private void StartCountdown()
    {
        _countdown.Stop();
        _countdown.Start();
    }

    private void CountDown()
    {
        _remainSec--;
        UpdateRemainText();

        // 最后 5 秒提示条常亮，免得"还剩几秒"跟着淡出条一起看不见
        if (_remainSec is > 0 and <= 5) RevealExit();

        if (_remainSec <= 0)
        {
            _countdown.Stop();
            Close();
        }
    }

    private void UpdateRemainText()
        => RemainText.Text = $"{Math.Max(0, _remainSec)} 秒后自动关闭 · ";

    // ══════════ 自适应字号用的字宽估算（与 MessageFullscreenWindow 同一套）══════════

    /// <summary>估算一行有多"宽"（按字号为单位）：中日韩全角算 1，其余算 0.55。</summary>
    private static double VisualLength(string line)
    {
        var n = 0.0;
        foreach (var c in line)
            n += c > 0x2E7F ? 1.0 : 0.55;
        return n;
    }

    // ══════════ 退出入口（与 MessageFullscreenWindow 同一套）══════════

    private void RevealExit()
    {
        _exitRemainMs = ExitHoldMs;
        ExitBar.Opacity = 1;
        ExitBar.IsHitTestVisible = true;
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
        _exitTimer.Stop();
        _countdown.Stop();
        if (ReferenceEquals(_current, this)) _current = null;

        // 声音必须收干净：铃停、朗读掐掉、COM 实例放掉（Tts.Cancel 只清排队，实例留给下次复用）
        try { TimerAlarm.Stop(); } catch { }
        try { Tts.Cancel(); } catch { }

        var played = Math.Max(0, _totalSec - Math.Max(0, _remainSec));
        AppLog.Done("announce", "喊话展示结束", TimeSpan.FromSeconds(played));
    }
}
