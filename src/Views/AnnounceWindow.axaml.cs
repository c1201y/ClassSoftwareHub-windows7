using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 教室喊话窗 v3（对齐上游 dv1.2.0-insider1.7，第三版）：
/// 远端喊话的全屏大字展示，倒计时到点自动关，Esc / 双击 / 右上角按钮可提前退。
///
/// <para>
/// 本版对齐上游的新能力：
/// ① 提示音改为用户自选的 wav（<see cref="AnnounceSound"/>，替换原 TimerAlarm）；
/// ② 出场动画按设置走预设（打字机 / 每行交错划入 / 弹入 / 无动画兜底）；
/// ③ 有完整出场流程：文字先退场 → 再淡出窗体 → 关窗；
/// ④ 字号自适应改为离屏探针逐档测量（树内直接量会拿到缓存值，长消息会被裁）。
/// </para>
///
/// <para>
/// 同一时刻只允许存在一个实例。<see cref="Show"/> 在窗口已开时就地换内容、倒计时重走
/// （Win7 版既有的不闪屏行为，优于上游的仅拉前台）；<see cref="Preview"/> 是设置页的本机
/// 预览入口，先关掉正在显示的再弹示例，方便连续切换。
/// </para>
///
/// <para>
/// 平台差异（Win7 版）：上游用 DesktopAcrylicController 压暗桌面，Win7 上不可用，按上游的
/// 兜底路径走不透明深底；动画用 Avalonia Transitions（改属性值即开始过渡）等价实现；
/// 朗读用 Tts（COM 实例 UI 线程创建与使用）。
/// </para>
/// </summary>
public sealed partial class AnnounceWindow : Window
{
    private static AnnounceWindow? _current;

    /// <summary>喊话窗当前是否开着（AnnounceService 播报期间跳过轮询的判断位）。</summary>
    public static bool IsShowing => _current is not null;

    private readonly DispatcherTimer _countdown;      // 自动关窗倒计时
    private DispatcherTimer? _typeTimer;              // 打字机逐字计时
    private int _durationSec;                         // 会随「就地换内容」更新，进度条才对得上
    private int _remainSec;
    private readonly bool _quiet;
    private readonly bool _speak;
    private bool _exiting;
    private bool _begun;

    /// <summary>「交错划入」切出的行文本（BuildSlideLinesIfNeeded 填充）。</summary>
    private readonly List<string> _lineTexts = new();

    /// <summary>完整正文。打字机过程中 BodyText.Text 只剩「已显示的那几个字」，
    /// 而 SizeChanged 会再次触发 FitText —— 拿半截文本量高度，字号会一路变大跳版。</summary>
    private string _fullText = "";

    /// <summary>FitText 算出的正文实际高度（打字机据此预留位置，避免逐字时行数跳动）。</summary>
    private double _fittedHeight;

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
            // ⛔ 必须清掉 _current：Start() 抛异常时窗口根本没打开，但 _current 已经指过去；
            //    留着它 IsShowing 恒为真、后续每条喊话都只会 Replace 到一个不存在的窗口上。
            _current = null;
            AppLog.Fail("announce", ex, "打开喊话窗失败");
        }
    }

    /// <summary>
    /// 本机预览：不走服务端，直接弹示例。与 <see cref="Show"/> 的区别是先关掉正在显示的，
    /// 这样能连续切换示例（正常来消息时只会就地换内容）。
    /// </summary>
    public static void Preview(AnnounceMessage msg, bool quiet, bool speak)
    {
        try { _current?.Close(); } catch { /* 已销毁，忽略 */ }
        _current = null;
        Show(msg, quiet, speak);
    }

    private AnnounceWindow(AnnounceMessage msg, bool quiet, bool speak)
    {
        _quiet = quiet;
        _speak = speak;

        InitializeComponent();

        // 服务端约束 10~120 秒，默认 30；这里再夹一次，防语义漂移导致窗挂很久
        var dur = msg.Duration <= 0 ? 30 : msg.Duration;
        _durationSec = Math.Clamp(dur, 5, 180);
        _remainSec = _durationSec;

        _fullText = msg.Text ?? "";
        BodyText.Text = _fullText;

        // 署名只写名字（保留 Win7 版的破折号样式）
        var from = (msg.From ?? "").Trim();
        FromText.IsVisible = from.Length > 0;
        FromText.Text = from.Length > 0 ? "—— " + from : "";

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => Tick();

        Root.SizeChanged += (_, _) =>
        {
            if (_begun) FitText();     // 尺寸变化（极少）时重排
            TryBegin();                // 首次量到真实尺寸后开场
        };

        Closed += OnClosed;
        UpdateCountText();

        // Esc 挂在**窗口**上（隧道级），不靠内部焦点：
        // ⛔ 只留 XAML 里的 Root.KeyDown + Root.Focus() 不够 —— 2026-10-29 打包实测，
        //    窗口已是前台（GetForegroundWindow 就是它）、真实按下 Esc，窗口毫无反应：
        //    全屏窗里没有任何可聚焦元素时，按键不会路由到 Root 网格。
        //    界面底部写着「按 Esc 可提前关闭」，这条承诺不能靠焦点碰运气。
        AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnEscapeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _ = PlayOutroAsync();
    }

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

        // 必须等布局拿到真实尺寸再开场：FitText、按行切分、打字机预留高度全靠它，
        // 早一步就会量到 0 宽 —— 长文本被截断、交错划入退化成整块滑入，根因都是这个（上游实测）。
        _ = ForceBeginAsync();
        Dispatcher.UIThread.Post(() => Root.Focus(), DispatcherPriority.Background);
    }

    private void TryBegin()
    {
        if (_begun) return;
        if (Root.Bounds.Width < 200 || Root.Bounds.Height < 200) return;

        _begun = true;

        FitText();
        BuildSlideLinesIfNeeded();
        PlayIntro();

        // 声音：安静模式什么都不放；否则先放提示音（用户可选），再按开关朗读
        if (!_quiet)
        {
            AnnounceSound.Play();
            // ⛔ 必须读 _fullText：打字机出场时 BodyText.Text 已被清成空串，读它等于什么都不读
            //    （默认出场就是 typewriter，于是默认配置下「朗读」从来没响过）。
            if (_speak) Tts.Speak(_fullText);
        }

        _countdown.Start();
    }

    /// <summary>兜底：万一 SizeChanged 没给到尺寸（极少见），1.2 秒后强行开场，别让窗口空着。</summary>
    private async Task ForceBeginAsync()
    {
        await Task.Delay(1200);
        await Dispatcher.UIThread.InvokeAsync(TryBegin);
    }

    /// <summary>播报期间又来一条：换内容 + 重走倒计时（窗口不动，不闪屏，不重放提示音）。</summary>
    private void Replace(AnnounceMessage msg)
    {
        if (_exiting) return;

        try { _typeTimer?.Stop(); } catch { }
        _typeTimer = null;
        _lineTexts.Clear();

        LinesPanel.Children.Clear();
        LinesPanel.IsVisible = false;
        BodyText.IsVisible = true;

        var dur = msg.Duration <= 0 ? 30 : msg.Duration;
        _durationSec = Math.Clamp(dur, 5, 180);
        _remainSec = _durationSec;

        var from = (msg.From ?? "").Trim();
        FromText.IsVisible = from.Length > 0;
        FromText.Text = from.Length > 0 ? "—— " + from : "";

        // 换内容后按新文本重排版，再快速淡入一次（不做完整出场动画，避免打扰）
        _fullText = string.IsNullOrWhiteSpace(msg.Text) ? "（空喊话）" : msg.Text;
        BodyText.Text = _fullText;
        BodyHost.Opacity = 1;
        FitText();
        BuildSlideLinesIfNeeded();

        UpdateCountText();
        if (!_countdown.IsEnabled) _countdown.Start();

        if (!_quiet && _speak) Tts.Speak(msg.Text);   // Speak 自带「清掉上一段再读」
    }

    // ════════════════════════ 入场 ════════════════════════

    private void PlayIntro()
    {
        Fade(TopBar, 0, 1, 320, beginMs: 140);
        Fade(Footer, 0, 1, 320, beginMs: 220);
        Fade(CloseButton, 0, 1, 320, beginMs: 140);

        // 强调色竖条与正文一起入场
        AccentBar.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(260),
                Easing = new CubicEaseOut(),
            },
        };
        AccentBar.Opacity = 1;

        var preset = (App.Settings.Current.AnnounceAnimation ?? "none").ToLowerInvariant();

        switch (preset)
        {
            case "typewriter":
                StartTypewriter();
                break;

            case "slide":
                StartSlideIn();
                break;

            case "pop":
                StartPop();
                break;

            default:
                // 兜底：不做动画，直接显示（不想要动画的人用这个）
                BodyHost.Opacity = 1;
                FromText.Opacity = FromText.IsVisible ? 0.75 : 0;
                break;
        }
    }

    /// <summary>打字机：逐字显示，完成后署名淡入。先按最终排版占好高度，避免逐字时行数跳动。</summary>
    private void StartTypewriter()
    {
        var full = BodyText.Text ?? "";
        BodyHost.Opacity = 1;

        // 预留最终高度（FitText 用全文真实排版量出来的），否则文字一多就边打边长、整块往上跳
        var height = _fittedHeight;
        if (height <= 0)
            height = MeasureTextHeight(full, BodyText.FontSize, Root.Bounds.Width - 280);
        if (height > 0) BodyText.MinHeight = height;

        BodyText.Text = "";
        var shown = 0;
        var delay = full.Length > 60 ? 18 : 30;

        _typeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        _typeTimer.Tick += (_, _) =>
        {
            shown++;
            BodyText.Text = full[..Math.Min(shown, full.Length)];

            if (shown < full.Length) return;

            _typeTimer?.Stop();
            if (FromText.IsVisible)
                Fade(FromText, 0, 0.75, 420, beginMs: 120);
        };
        _typeTimer.Start();
    }

    /// <summary>弹入：整体放大到原尺寸，带一点回弹。</summary>
    private void StartPop()
    {
        BodyHost.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        var scale = new ScaleTransform { ScaleX = 0.86, ScaleY = 0.86 };
        BodyHost.RenderTransform = scale;

        AttachTransition(scale, ScaleTransform.ScaleXProperty, 460, new BackEaseOut());
        AttachTransition(scale, ScaleTransform.ScaleYProperty, 460, new BackEaseOut());
        scale.ScaleX = 1;
        scale.ScaleY = 1;

        Fade(BodyHost, 0, 1, 260);
        if (FromText.IsVisible)
            Fade(FromText, 0, 0.75, 380, beginMs: 380);
    }

    /// <summary>每行从屏幕左侧外侧交错滑入（上游点名的效果）。</summary>
    private void StartSlideIn()
    {
        if (_lineTexts.Count == 0 || LinesPanel.Children.Count == 0)
        {
            // 没算出分行（极短消息等），退化成整体滑入
            BodyHost.Opacity = 1;
            var whole = new TranslateTransform { X = -80 };
            BodyHost.RenderTransform = whole;
            AttachTransition(whole, TranslateTransform.XProperty, 420, new CubicEaseOut());
            whole.X = 0;
            if (FromText.IsVisible) Fade(FromText, 0, 0.75, 380, beginMs: 260);
            return;
        }

        BodyHost.Opacity = 1;
        var screenWidth = Root.Bounds.Width;

        for (var i = 0; i < LinesPanel.Children.Count; i++)
        {
            if (LinesPanel.Children[i] is not TextBlock child) continue;

            child.Opacity = 1;
            var transform = new TranslateTransform { X = -(screenWidth + 120) };
            child.RenderTransform = transform;

            // 交错：第 i 行延迟 i*90ms 起跳（Avalonia Transition 没有 BeginTime，用一次性计时器）
            var index = i;
            Delayed(index * 90, () =>
            {
                AttachTransition(transform, TranslateTransform.XProperty, 460, new CubicEaseOut());
                transform.X = 0;
            });
        }

        var totalMs = 460 + Math.Max(0, _lineTexts.Count - 1) * 90;
        if (FromText.IsVisible) Fade(FromText, 0, 0.75, 380, beginMs: Math.Max(0, totalMs - 120));
    }

    // ════════════════════════ 出场 ════════════════════════

    /// <summary>
    /// 出场（上游 Nick 要求）：文字先退场 → 窗体再淡出 → 关窗。
    /// 用「先动画再延时关」而不是直接 Close：直接关会硬切，看不出「慢慢变回去」。
    /// </summary>
    private async Task PlayOutroAsync()
    {
        if (_exiting) return;
        _exiting = true;

        try { _countdown.Stop(); } catch { }
        try { _typeTimer?.Stop(); } catch { }
        try { AnnounceSound.Stop(); } catch { }

        Fade(BodyHost, BodyHost.Opacity, 0, 260);
        Fade(FromText, FromText.Opacity, 0, 220);
        Fade(TopBar, TopBar.Opacity, 0, 200);
        Fade(Footer, Footer.Opacity, 0, 200);
        Fade(CloseButton, CloseButton.Opacity, 0, 200);

        await Task.Delay(300);
        Fade(Root, Root.Opacity, 0, 420);
        await Task.Delay(460);

        Close();
    }

    // ════════════════════════ 排版 ════════════════════════

    /// <summary>
    /// 字号自适应：从上限 <see cref="TextFit.MaxSize"/> 起算，放不下就逐档缩（96 → 30）。
    /// ⛔ 起点必须是常量上限，不能读 <c>BodyText.FontSize</c>：XAML 没给正文设字号，
    ///   它继承的是主题默认 14 —— 拿 14 当起点，PickFontSize 只会往下缩，
    ///   全屏正文就永远停在 14 号（2026-10-29 真机验证：整屏大字变成一行小字）。
    /// ⛔ 量的对象是 <see cref="_fullText"/>（全文）而不是 BodyText.Text —— 打字机过程中后者只剩半截，
    ///   拿来量高度会让字号一路变大跳版。
    /// ⛔ 高度取 max(真实测量, 保守估算)，规则全在 Core/TextFit（纯函数）里：
    ///   长文本被裁的两次线上问题（上游 10-25、10-26）都是这里的条件写错，抽出去才测得到。
    /// </summary>
    private void FitText()
    {
        var width = Root.Bounds.Width - 280;
        var maxHeight = Root.Bounds.Height - 270;
        if (width <= 0 || maxHeight <= 0) return;

        var fit = TextFit.PickFontSize(
            _fullText, width, maxHeight,
            TextFit.MaxSize,
            (text, fontSize, lineHeight, w) =>
            {
                if (string.IsNullOrEmpty(text) || w <= 0) return 0;

                // 离屏探针：新对象、每次真的按当前字号排一遍（不能对树内的 BodyText 调 Measure()，
                // 布局干净时返回缓存值，改字号不重算 —— 上游实测长消息被上下裁掉）。
                var probe = new TextBlock
                {
                    Text = text,
                    FontFamily = BodyText.FontFamily,
                    FontWeight = BodyText.FontWeight,
                    FontSize = fontSize,
                    LineHeight = lineHeight,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Width = w,
                };
                probe.Measure(new Size(w, double.PositiveInfinity));
                return probe.DesiredSize.Height;
            });

        BodyText.FontSize = fit.FontSize;
        BodyText.LineHeight = fit.LineHeight;
        _fittedHeight = fit.Height;
    }

    /// <summary>量一段文字在给定宽度下的高度（打字机预留高度用）。</summary>
    private double MeasureTextHeight(string text, double fontSize, double width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0) return 0;

        var probe = new TextBlock
        {
            Text = text,
            FontFamily = BodyText.FontFamily,
            FontWeight = BodyText.FontWeight,
            FontSize = fontSize,
            LineHeight = Math.Round(fontSize * 1.22),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Width = width,
        };
        probe.Measure(new Size(width, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    /// <summary>
    /// 「交错划入」需要按行摆放，所以先把正文按实际宽度切行（贪心逐字加）。
    /// 切完若总高超出可用区域，就缩字号重来（最多几轮）。
    /// </summary>
    private void BuildSlideLinesIfNeeded()
    {
        var preset = (App.Settings.Current.AnnounceAnimation ?? "none").ToLowerInvariant();
        if (preset != "slide") return;

        var width = Root.Bounds.Width - 280;
        var height = Root.Bounds.Height - 270;
        var text = BodyText.Text ?? "";
        if (width <= 0 || height <= 0 || text.Length == 0) return;

        var size = BodyText.FontSize;
        _lineTexts.Clear();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var lines = WrapLines(text, size, width);
            var total = lines.Count * Math.Round(size * 1.22);
            if (total <= height || size <= 30)
            {
                _lineTexts.AddRange(lines);
                break;
            }
            size -= 4;
        }

        if (_lineTexts.Count == 0) return;

        LinesPanel.Children.Clear();
        foreach (var line in _lineTexts)
        {
            LinesPanel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = size,
                LineHeight = Math.Round(size * 1.22),
                FontWeight = BodyText.FontWeight,
                FontFamily = BodyText.FontFamily,
                Foreground = BodyText.Foreground,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            });
        }

        BodyText.IsVisible = false;
        LinesPanel.IsVisible = true;
    }

    /// <summary>贪心切行：逐字累加，超宽就断开。中文没有空格，只能这样切。</summary>
    private static List<string> WrapLines(string text, double fontSize, double width)
    {
        var lines = new List<string>();
        var probe = new TextBlock
        {
            FontSize = fontSize,
            TextWrapping = TextWrapping.NoWrap,
        };

        var current = "";
        foreach (var ch in text)
        {
            var candidate = current + ch;
            probe.Text = candidate;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            if (probe.DesiredSize.Width > width && current.Length > 0)
            {
                lines.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    // ════════════════════════ 动画工具 ════════════════════════

    /// <summary>
    /// 淡入淡出。Avalonia 没有 Storyboard，用 Transitions（改属性值即开始过渡）等价实现；
    /// 延迟起跳用一次性计时器（Transition 没有 BeginTime）。
    /// </summary>
    private static void Fade(Control element, double from, double to, int ms, int beginMs = 0,
                             Easing? easing = null)
    {
        element.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(ms),
                Easing = easing ?? new CubicEaseOut(),
            },
        };
        element.Opacity = Math.Clamp(from, 0, 1);
        Delayed(beginMs, () => element.Opacity = Math.Clamp(to, 0, 1));
    }

    /// <summary>给变换对象挂一条 double 过渡（ScaleX / ScaleY / X 等都用它）。</summary>
    private static void AttachTransition(Transform transform, AvaloniaProperty property, int ms,
                                         Easing? easing = null)
    {
        transform.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = property,
                Duration = TimeSpan.FromMilliseconds(ms),
                Easing = easing ?? new CubicEaseOut(),
            },
        };
    }

    /// <summary>一次性延时（Avalonia Transition 无 BeginTime，交错 / 延迟起跳用它）。</summary>
    private static void Delayed(int ms, Action action)
    {
        if (ms <= 0)
        {
            action();
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    // ════════════════════════ 倒计时 ════════════════════════

    private void Tick()
    {
        if (_exiting) return;

        _remainSec--;
        if (_remainSec <= 0)
        {
            _remainSec = 0;
            UpdateCountText();
            _ = PlayOutroAsync();
            return;
        }

        UpdateCountText();
    }

    private void UpdateCountText()
    {
        CountText.Text = $"{_remainSec} 秒后自动关闭　·　按 Esc 或右上角按钮可提前关闭";
        RemainBar.Value = _durationSec > 0
            ? Math.Clamp(_remainSec * 100.0 / _durationSec, 0, 100)
            : 0;
    }

    // ════════════════════════ 关闭 ════════════════════════

    private void Close_Click(object? sender, RoutedEventArgs e) => _ = PlayOutroAsync();

    private void Root_KeyDown(object? sender, KeyEventArgs e) => OnEscapeKeyDown(sender, e);

    private void Root_DoubleTapped(object? sender, TappedEventArgs e) => _ = PlayOutroAsync();

    private void OnClosed(object? sender, EventArgs args)
    {
        _countdown.Stop();
        try { _typeTimer?.Stop(); } catch { }
        if (ReferenceEquals(_current, this)) _current = null;

        // 声音必须收干净：提示音停、朗读掐掉（Tts.Cancel 只清排队，COM 实例留给下次复用）
        try { AnnounceSound.Stop(); } catch { }
        try { Tts.Cancel(); } catch { }

        var played = Math.Max(0, _durationSec - Math.Max(0, _remainSec));
        AppLog.Done("announce", "喊话展示结束", TimeSpan.FromSeconds(played));
    }
}
