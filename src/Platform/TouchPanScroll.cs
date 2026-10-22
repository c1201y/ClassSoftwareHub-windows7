using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 触屏「单指平移 + 惯性」的安全实现（2026-10-23 第三版）。
///
/// <para>
/// 背景一（为什么不用 Avalonia 自带手势）：自带的 <c>ScrollGestureRecognizer</c> 走
/// <c>ScrollContentPresenter.OnScrollGesture</c> → 写 <c>ScrollViewer.Offset</c> →
/// 与 viewer 的双向绑定互相推值。夹取结果一旦与传入值永不相等（NaN 参与时必然如此），
/// 这个环就在**库内部同一次调用栈**里永不收敛 —— 0xC00000FD，dump 三次实锤。
/// 我们的钩子都在环外，救不回来，只能自己实现。
/// </para>
/// <para>
/// 背景二（为什么不逐帧写 Offset）：写 <c>Offset</c> 会触发重新布局 + 整页重排；
/// Win7 路线是软件渲染、长页面节点多 —— 每帧重排就是"滑动不跟手"。
/// </para>
/// <para>
/// ✅ 本实现的铁律：**拖动与惯性期间只改 <see cref="TranslateTransform"/>（纯视觉、零布局），
///    抬手（含惯性结束）时一次性把位置写进 <c>Offset</c>**。全程最多写一次 Offset，
///    且带 extent/viewport 有限性夹取（NaN 直接放弃），所以既跟手、又不可能触发那个死循环。
/// </para>
/// <para>
/// 惯性：抬手时按最近 ~100ms 的位移估速度，超过阈值就起一个 15ms 定时器，
/// 速度按 0.92^(dt/16ms) 衰减，画面继续平移（仍只改变换），速度低于 0.05dip/ms 或触到边界即停，
/// 停下时提交一次 Offset。再次按下会立刻中止惯性。
/// </para>
/// <para>
/// ⚠️ 只处理 <see cref="PointerType.Touch"/>：鼠标、触控板滚轮、文本选择、按钮点击都不受影响。
///    按下后位移不到 6dip 就抬手 = 点击，事件不吞。
/// </para>
/// </summary>
internal static class TouchPanScroll
{
    /// <summary>超过这个位移（dip）才算"开始拖动"，否则按点击处理。</summary>
    private const double StartThreshold = 6;

    /// <summary>低于这个速度（dip/ms）就不起惯性 / 结束惯性。</summary>
    private const double FlingThreshold = 0.12;

    /// <summary>惯性每帧衰减系数（按 16ms 归一）。</summary>
    private const double FrictionPer16Ms = 0.92;

    /// <summary>惯性定时器间隔。</summary>
    private const int InertiaTickMs = 15;

    private sealed class Sample
    {
        public long Ms;
        public Point Point;
    }

    private sealed class State
    {
        public bool Tracking;             // 已按下，还没决定是不是拖动
        public bool Dragging;             // 已接管
        public Point Start;               // 按下点（呈现器坐标）
        public Vector StartOffset;        // 按下时的滚动偏移
        public Visual? Content;           // 被平移的内容（presenter.Child）
        public TranslateTransform? Shift; // 拖动 / 惯性期间挂上去的渲染变换
        public Vector Pending;            // 当前目标偏移（结束时一次性提交）

        public readonly List<Sample> Samples = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();

        public DispatcherTimer? Inertia;
        public Vector Velocity;           // dip/ms，最后一段手指/惯性速度
        public long LastTickMs;
    }

    /// <summary>给一个内容呈现器挂上触屏平移（幂等）。</summary>
    public static void Attach(ScrollContentPresenter presenter)
    {
        try
        {
            if (presenter.GetValue(AttachedProperty) is true) return;
            presenter.SetValue(AttachedProperty, true);

            var state = new State();

            // ── 按下：记录起点，顺手停掉正在跑的惯性 ──
            presenter.AddHandler(InputElement.PointerPressedEvent,
                (_, e) =>
                {
                    if (e.Pointer.Type != PointerType.Touch) return;

                    StopInertia(presenter, state, commit: true, releaseCapture: false);

                    var sv = FindScrollViewer(presenter);
                    if (sv is null) return;

                    state.Tracking = true;
                    state.Dragging = false;
                    state.Start = e.GetPosition(presenter);
                    state.StartOffset = sv.Offset;
                    state.Pending = sv.Offset;

                    state.Samples.Clear();
                    state.Samples.Add(new Sample { Ms = state.Clock.ElapsedMilliseconds, Point = state.Start });
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // ── 移动：超过阈值后只平移画面（RenderTransform），不写 Offset ──
            presenter.AddHandler(InputElement.PointerMovedEvent,
                (_, e) =>
                {
                    if (!state.Tracking || e.Pointer.Type != PointerType.Touch) return;

                    var now = e.GetPosition(presenter);

                    if (!state.Dragging)
                    {
                        var first = now - state.Start;
                        if (Math.Abs(first.X) < StartThreshold && Math.Abs(first.Y) < StartThreshold) return;

                        if (!BeginDrag(presenter, state)) return;

                        state.Dragging = true;
                        e.Pointer.Capture(presenter);
                    }

                    var sv = FindScrollViewer(presenter);
                    if (sv is null || state.Shift is null) return;

                    var delta = now - state.Start;
                    var target = Clamp(sv, new Vector(state.StartOffset.X - delta.X, state.StartOffset.Y - delta.Y));
                    if (target is null) return;

                    state.Pending = target.Value;

                    // 画面按「目标 − 起点」反向位移：往下滑内容往上走
                    state.Shift.X = state.StartOffset.X - target.Value.X;
                    state.Shift.Y = state.StartOffset.Y - target.Value.Y;

                    TrackSample(state, now);
                    e.Handled = true;
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // ── 抬手：起惯性或直接落位 ──
            void End(PointerEventArgs? pe)
            {
                if (!state.Tracking) return;

                var wasDragging = state.Dragging;
                state.Tracking = false;
                state.Dragging = false;

                if (wasDragging)
                {
                    TrackSample(state, pe is null ? state.Start : pe.GetPosition(presenter));

                    var velocity = EstimateVelocity(state);
                    if (velocity.Length > FlingThreshold && StartInertia(presenter, state, velocity))
                    {
                        // 惯性接管：位置等惯性停稳后再提交
                        if (pe is not null)
                        {
                            try { pe.Pointer.Capture(null); } catch { }
                        }
                        return;
                    }
                }

                CommitAndEnd(presenter, state);

                if (wasDragging && pe is not null)
                {
                    try { pe.Pointer.Capture(null); } catch { }
                }
            }

            presenter.AddHandler(InputElement.PointerReleasedEvent,
                (_, e) => End(e), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // 捕获丢失事件没有 Pointer 属性，只清状态
            presenter.AddHandler(InputElement.PointerCaptureLostEvent,
                (_, _) => End(null), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[touch-pan] 挂载失败: " + ex.Message);
        }
    }

    // ────────────────────────── 拖动 / 惯性 ──────────────────────────

    /// <summary>开始拖动：给内容挂上渲染变换。</summary>
    private static bool BeginDrag(ScrollContentPresenter presenter, State state)
    {
        try
        {
            if (presenter.Child is not Visual content) return false;

            var shift = new TranslateTransform();
            content.RenderTransform = shift;

            state.Content = content;
            state.Shift = shift;
            return true;
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[touch-pan] 拖动初始化失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>记录采样点（只留最近 ~120ms 的，够估速度）。</summary>
    private static void TrackSample(State state, Point point)
    {
        var now = state.Clock.ElapsedMilliseconds;
        state.Samples.Add(new Sample { Ms = now, Point = point });

        while (state.Samples.Count > 2 && now - state.Samples[0].Ms > 120)
            state.Samples.RemoveAt(0);
    }

    /// <summary>按最近采样估速度：dip/ms（画面位移方向，与手指相反）。</summary>
    private static Vector EstimateVelocity(State state)
    {
        if (state.Samples.Count < 2) return default;

        var first = state.Samples[0];
        var last = state.Samples[^1];
        var ms = Math.Max(1, last.Ms - first.Ms);

        // 手指位移 → 内容位移取反（内容往手指反方向走）
        return new Vector(-(last.Point.X - first.Point.X) / ms, -(last.Point.Y - first.Point.Y) / ms);
    }

    /// <summary>启动惯性（只跑渲染变换；停稳后才写 Offset）。</summary>
    private static bool StartInertia(ScrollContentPresenter presenter, State state, Vector velocity)
    {
        var sv = FindScrollViewer(presenter);
        if (sv is null || state.Shift is null) return false;

        state.Velocity = velocity;
        state.LastTickMs = state.Clock.ElapsedMilliseconds;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(InertiaTickMs) };
        state.Inertia = timer;

        timer.Tick += (_, _) =>
        {
            try
            {
                var now = state.Clock.ElapsedMilliseconds;
                var dt = Math.Clamp(now - state.LastTickMs, 1, 50);
                state.LastTickMs = now;

                // 速度衰减（按 16ms 归一的摩擦系数）
                var decay = Math.Pow(FrictionPer16Ms, dt / 16.0);
                state.Velocity = new Vector(state.Velocity.X * decay, state.Velocity.Y * decay);

                var svNow = FindScrollViewer(presenter);
                if (svNow is null || state.Shift is null) { StopInertia(presenter, state, commit: true, releaseCapture: false); return; }

                var target = Clamp(svNow, new Vector(
                    state.Pending.X + state.Velocity.X * dt,
                    state.Pending.Y + state.Velocity.Y * dt));

                if (target is null) { StopInertia(presenter, state, commit: true, releaseCapture: false); return; }

                // 触到边界：把该方向速度清零，避免"贴边抖动"
                if (Math.Abs(target.Value.X - state.Pending.X) < 0.01) state.Velocity = new Vector(0, state.Velocity.Y);
                if (Math.Abs(target.Value.Y - state.Pending.Y) < 0.01) state.Velocity = new Vector(state.Velocity.X, 0);

                state.Pending = target.Value;
                state.Shift.X = state.StartOffset.X - target.Value.X;
                state.Shift.Y = state.StartOffset.Y - target.Value.Y;

                if (state.Velocity.Length < 0.05)
                    StopInertia(presenter, state, commit: true, releaseCapture: false);
            }
            catch (Exception ex)
            {
                Core.AppLog.Debug("ui", "[touch-pan] 惯性帧异常: " + ex.Message);
                StopInertia(presenter, state, commit: true, releaseCapture: false);
            }
        };

        timer.Start();
        return true;
    }

    /// <summary>停止惯性（可选提交位置）。</summary>
    private static void StopInertia(ScrollContentPresenter presenter, State state, bool commit, bool releaseCapture)
    {
        if (state.Inertia is null) return;

        try { state.Inertia.Stop(); } catch { }
        state.Inertia = null;

        if (commit) CommitAndEnd(presenter, state);

        if (releaseCapture)
        {
            try { presenter.Cursor = null; } catch { }
        }
    }

    /// <summary>把 Pending 一次性提交成 Offset，然后撤掉渲染变换。</summary>
    private static void CommitAndEnd(ScrollContentPresenter presenter, State state)
    {
        try
        {
            var sv = FindScrollViewer(presenter);
            if (sv is not null)
            {
                var commit = Clamp(sv, state.Pending);
                if (commit is not null && (Math.Abs(sv.Offset.X - commit.Value.X) > 0.01 || Math.Abs(sv.Offset.Y - commit.Value.Y) > 0.01))
                    sv.Offset = commit.Value;
            }
        }
        finally
        {
            EndDrag(state);
        }
    }

    /// <summary>撤掉渲染变换。</summary>
    private static void EndDrag(State state)
    {
        try
        {
            if (state.Content is not null && state.Shift is not null && ReferenceEquals(state.Content.RenderTransform, state.Shift))
                state.Content.RenderTransform = null;
        }
        catch
        {
            // 内容已被回收（页面切走）—— 忽略
        }

        state.Content = null;
        state.Shift = null;
    }

    private static readonly Avalonia.StyledProperty<bool> AttachedProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<ScrollContentPresenter, bool>("TouchPanAttached", typeof(TouchPanScroll));

    /// <summary>把目标偏移夹进合法范围；任何非有限值一律放弃（NaN 是死循环引信）。</summary>
    private static Vector? Clamp(ScrollViewer sv, Vector target)
    {
        var maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

        if (!double.IsFinite(maxX) || !double.IsFinite(maxY)) return null;
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y)) return null;

        return new Vector(Math.Clamp(target.X, 0, maxX), Math.Clamp(target.Y, 0, maxY));
    }

    private static ScrollViewer? FindScrollViewer(Visual from) =>
        from.FindAncestorOfType<ScrollViewer>(includeSelf: false);
}
