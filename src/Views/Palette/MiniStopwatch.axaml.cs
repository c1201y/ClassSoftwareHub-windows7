using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClassSoftwareHub.Desktop.Views.Palette;

/// <summary>
/// 浮窗版秒表（正计时 + 计次 + 全屏）：课堂上做限时活动、比赛计时用。
/// 状态只在内存里，收起浮窗时暂停、再打开接着走（跟其它小工具一致）。
///
/// <para>
/// 2026-10-03（上游 dv1.1.0）：上游把「计次（分段）」**整个砍掉**，换成了「全屏」按钮
///   （理由是"原来最多记三次，实测教室里没人用"）。
///
/// 2026-10-06（Nick 实机反馈「秒表计次只有 3 次改一下」）：**计次恢复，并且去掉 3 次上限**。
///   取舍说明：
///     · 上游砍它的真实原因是"只能记 3 条"这个**上限**，不是计次本身没用 ——
///       一次活动里要记十几个人的成绩，3 条当然不够；
///     · 现在条目不限量，列表装不下就滚动（见 <c>LapPanel</c>），不再占掉显示区；
///     · 上游那份「全屏」改进一并保留，两个都在。
/// </para>
/// </summary>
public sealed partial class MiniStopwatch : UserControl
{
    private readonly DispatcherTimer _tick;
    private readonly Stopwatch _sw = new();
    private readonly List<long> _laps = new();      // 每次计次时的累计毫秒（**不限条数**）

    private long _accMs;          // 暂停前累计的毫秒
    private bool _running;
    private bool _pendingResume;  // 收起时还在跑，再打开要接着跑

    public MiniStopwatch()
    {
        InitializeComponent();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick += (_, _) => UpdateDisplay();

        UpdateDisplay();
    }

    private long ElapsedMs => _accMs + (_running ? _sw.ElapsedMilliseconds : 0);

    // ── 控制 ────────────────────────────────────────────────

    private void StartPause_Click(object? sender, RoutedEventArgs e)
    {
        if (_running) PauseInternal();
        else StartInternal();
    }

    private void StartInternal()
    {
        _sw.Restart();
        _running = true;
        _tick.Start();
        StartButton.Content = "暂停";
        LapButton.IsEnabled = true;
        UpdateDisplay();
    }

    private void PauseInternal()
    {
        if (_running)
        {
            _accMs += _sw.ElapsedMilliseconds;
            _sw.Reset();
        }
        _running = false;
        _tick.Stop();
        StartButton.Content = ElapsedMs > 0 ? "继续" : "开始";
        LapButton.IsEnabled = _running;          // 暂停时按「计次」记的是停表那一刻，没有意义
        UpdateDisplay();
    }

    /// <summary>
    /// 记一次计次（分段）。**不限条数** —— 记多少条就显示多少条，装不下由列表自己滚。
    /// </summary>
    private void Lap_Click(object? sender, RoutedEventArgs e)
    {
        if (!_running) return;

        _laps.Add(ElapsedMs);
        RefreshLaps();
    }

    /// <summary>全屏：把当前秒表时间交给全屏窗口，跟着一起走（关闭全屏不影响这里的表）。</summary>
    private void Fullscreen_Click(object? sender, RoutedEventArgs e)
        => Views.StopwatchFullscreenWindow.Show(() => ElapsedMs, () => _running);

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        _tick.Stop();
        _sw.Reset();
        _accMs = 0;
        _running = false;
        _pendingResume = false;
        _laps.Clear();
        StartButton.Content = "开始";
        LapButton.IsEnabled = false;
        RefreshLaps();
        UpdateDisplay();
    }

    // ── 收起 / 再打开（浮窗隐藏时别白烧 CPU） ──────────────────

    /// <summary>浮窗收起：停表但留着状态。</summary>
    public void Pause()
    {
        _tick.Stop();
        if (_running)
        {
            _accMs += _sw.ElapsedMilliseconds;
            _sw.Reset();
            _running = false;
            _pendingResume = true;
        }
        LapButton.IsEnabled = _running;
        UpdateDisplay();
    }

    /// <summary>浮窗再打开：刚才在跑就接着跑。</summary>
    public void Resume()
    {
        if (!_pendingResume) return;
        _pendingResume = false;
        StartInternal();
    }

    // ── 显示 ────────────────────────────────────────────────

    private void UpdateDisplay()
    {
        var ms = ElapsedMs;
        var hours = ms / 3_600_000;
        var minutes = ms % 3_600_000 / 60_000;
        var seconds = ms % 60_000 / 1000;
        var hundredths = ms % 1000 / 10;

        TimeText.Text = hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
        CsText.Text = $".{hundredths:00}";

        StateText.Text = _running ? "计时中" : (ElapsedMs > 0 ? "已暂停" : "未开始");
    }

    /// <summary>
    /// 重建计次列表：**新的排在最上面**（不用滚动就能看见刚记的那一条），每条一行。
    ///
    /// ⚠️ 显示的是**分段成绩**（这一次按下距上一次按下的间隔），不是累计时间 ——
    ///    2026-10-02 实机反馈过「计次有问题」：原来显示累计值、编号还倒着排，
    ///    结果「第 1 次 6.08 · 第 2 次 5.93 · 第 3 次 5.80」越排越小，看不出每次间隔。
    ///    编号按**按下顺序**（第 1 次 = 最先按的），值 = 该次与上一次的差值。
    /// </summary>
    private void RefreshLaps()
    {
        LapHost.Children.Clear();

        LapPanel.IsVisible = _laps.Count > 0;
        if (_laps.Count == 0) return;

        for (var i = _laps.Count - 1; i >= 0; i--)          // 从新到旧
        {
            var split = _laps[i] - (i > 0 ? _laps[i - 1] : 0);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = $"第 {i + 1} 次",
                FontSize = 12,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var value = new TextBlock
            {
                Text = Format(split),
                FontSize = 13,
                FontFamily = new FontFamily("Bahnschrift"),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);

            LapHost.Children.Add(row);
        }

        // 新条目插在最前，把列表拉回顶部（不然停在上一轮的滚动位置上，看不见新记的那条）
        LapScroll.Offset = new Vector(0, 0);
    }

    private static string Format(long ms)
    {
        var minutes = ms / 60_000;
        var seconds = ms % 60_000 / 1000;
        var hundredths = ms % 1000 / 10;
        return minutes > 0 ? $"{minutes}:{seconds:00}.{hundredths:00}" : $"{seconds}.{hundredths:00}";
    }
}
