using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 全屏留言窗：把留言铺满整块屏幕，Esc / 双击 / 右上角按钮退出。
///
/// <para>
/// ⚠️ **同一时刻只允许存在一个**（跟 <see cref="ClockFullscreenWindow"/> 同一个理由）：
/// 以前每次点都 new 一个，触屏上连点几下就叠出一堆长得一模一样、又都盖满屏幕的窗口，
/// 现象就是"点了一下然后关不掉了"。统一走 <see cref="Show"/>：已经开着就地换内容并拉到前台。
/// </para>
///
/// <para>
/// 字号怎么定：**内容 + 屏幕一起算**。
///   · 先按设定的「字号 × 全屏放大倍数」要一个目标值；
///   · 再按"最长一行要放得下屏幕 92% 宽、总高不超过 86%"算一个上限；
///   · 取两者较小值 —— 短句能放大到很醒目，长段落又绝不会溢出屏幕。
/// 中文按 1 个字宽、西文按 0.55 个字宽估算（<see cref="VisualLength"/>）。
/// </para>
/// </summary>
public sealed partial class MessageFullscreenWindow : Window
{
    private static MessageFullscreenWindow? _current;

    private const int ExitHoldMs = 4500;
    private const int ExitFadeMs = 900;

    private readonly DispatcherTimer _exitTimer;
    private int _exitRemainMs;

    private MessageConfig _cfg = new();

    /// <summary>唯一入口：开一个全屏留言；已经开着就就地更新并拉到前面。</summary>
    public static void Show(MessageConfig cfg)
    {
        try
        {
            if (_current is not null)
            {
                _current.Update(cfg);
                _current.Activate();
                _current.RevealExit();
                return;
            }

            var window = new MessageFullscreenWindow(cfg);
            _current = window;
            window.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warning("message", "全屏留言失败: " + ex.Message);
        }
    }

    private MessageFullscreenWindow(MessageConfig cfg)
    {
        _cfg = cfg;
        InitializeComponent();
        Title = "全屏留言";

        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _exitTimer.Tick += (_, _) => FadeExit();

        // 鼠标一动就把「怎么退出」重新亮出来（本窗是新写的，这里显式挂上；
        // 沿用的全屏时钟/秒表把这两个处理器留成了没接线的死代码 —— 见那边的注释）。
        Root.PointerMoved += Root_PointerMoved;
        Root.PointerPressed += Root_PointerPressed;
        Root.Tapped += Root_Tapped;

        Closed += OnClosed;
    }

    private void Start()
    {
        Show();

        try
        {
            // 铺满整个物理屏幕；Win11 上无边框全屏默认还带圆角 + 细边框，会把屏幕四角/边缘露出来
            WindowState = WindowState.FullScreen;

            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd != IntPtr.Zero) WindowChrome.RemoveBorder(hwnd, rounded: false, dark: true);
        }
        catch { /* 全屏失败也能当普通窗口用 */ }

        Apply();
        RevealExit();

        // 显示一帧后再落焦点（窗口还没起来时 Focus 不生效）
        Dispatcher.UIThread.Post(() => Root.Focus());
    }

    /// <summary>就地换内容（已经全屏了就改这一份，不新开窗口）。</summary>
    private void Update(MessageConfig cfg)
    {
        _cfg = cfg;
        Apply();
    }

    private void Apply()
    {
        var text = string.IsNullOrWhiteSpace(_cfg.Text) ? "（还没有内容）" : _cfg.Text;

        Body.Text = text;
        Body.FontWeight = _cfg.Bold ? FontWeight.Bold : FontWeight.Normal;
        Body.Foreground = Brush(_cfg.TextColor, Colors.White);

        // 全屏底色：用户设的底色（默认是浅色纸感底）+ 文字色一般也是深色，读起来最舒服。
        // ⚠️ 不能用"透明"：全屏窗就该铺满，透出桌面反而看不清字。
        Root.Background = Brush(_cfg.BackColor, Color.FromRgb(0x10, 0x14, 0x18));
        Background = Root.Background;

        Layout();
    }

    private void Root_SizeChanged(object? sender, SizeChangedEventArgs e) => Layout();

    /// <summary>按屏幕尺寸 + 字数算字号（见类注释）。</summary>
    private void Layout()
    {
        var w = Root.Bounds.Width;
        var h = Root.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var lines = TextLines(_cfg.Text);
        var longest = lines.Length == 0 ? 1 : Math.Max(1, lines.Max(VisualLength));

        var byWidth = w * 0.92 / longest;                                  // 一个字宽 ≈ 字号
        var byHeight = h * 0.86 / Math.Max(1, lines.Length) / 1.30;        // 一行高 ≈ 字号 × 1.3

        var want = _cfg.FontSize * Math.Clamp(_cfg.FullscreenScale, 1, 6);
        var size = Math.Max(16, Math.Min(want, Math.Min(byWidth, byHeight)));

        Body.FontSize = size;
        Body.LineHeight = Math.Round(size * 1.22);
    }

    private static string[] TextLines(string? text)
        => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>估算一行有多"宽"（按字号为单位）：中日韩全角算 1，其余算 0.55。</summary>
    private static double VisualLength(string line)
    {
        var n = 0.0;
        foreach (var c in line)
            n += c > 0x2E7F ? 1.0 : 0.55;      // 0x2E80 起是 CJK 部首扩展
        return n;
    }

    // ══════════ 退出入口（与 ClockFullscreenWindow 同一套）══════════

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
        if (ReferenceEquals(_current, this)) _current = null;
        AppLog.Info("message", "全屏留言已关闭");
    }

    private static IBrush Brush(string? hex, Color fallback)
        => new SolidColorBrush(MessageConfig.TryParseColor(hex, out var c) ? c : fallback);
}
