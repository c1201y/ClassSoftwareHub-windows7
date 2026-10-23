using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 触屏「单指平移 + 惯性」实现（2026-10-23 第四版：每帧写 Offset，滚动条同步连贯）。
///
/// <para>
/// 演进过程（不要再走回头路）：
/// </para>
/// <para>
/// ① 用 Avalonia 自带 <c>ScrollGestureRecognizer</c> —— **崩**（0xC00000FD）。它的手势处理会写
///    <c>ScrollViewer.Offset</c>，与 presenter 的双向绑定互相推值；夹取结果一旦永不等于传入值
///    （NaN 参与时必然如此），环就在库内部同一次调用栈里不收敛，dump 三次实锤。
/// </para>
/// <para>
/// ② 自己写、但每个 PointerMoved 都写 Offset —— **卡**（写 Offset 触发重排，长页面软件渲染扛不住）。
/// </para>
/// <para>
/// ③ 拖动只改 <c>TranslateTransform</c>、抬手才写 Offset —— 画面顺了，但**滚动条一卡一卡**
///    （滚动条跟的是 Offset，拖动期间它压根没动）。
/// </para>
/// <para>
/// ✅ 现在：拖动与惯性都用 **15ms 定时器把「最新目标位置」写进 <c>Offset</c>**。
///    指针事件只更新目标值（可能一帧来 3~4 个事件，只取最后那个），所以**每帧最多写一次**，
///    既不会像②那样每个事件都重排，又让内容与滚动条同步、连贯地走。
///    写入前一律做有限性判断 + extent/viewport 夹取（NaN 是那个死循环的引信）。
///    因为写入方是我们（不是 presenter 的手势回调），不存在"presenter ↔ viewer 互相推值"的环。
/// </para>
/// <para>
/// ⚠️ 只处理 <see cref="PointerType.Touch"/>；鼠标、触控板滚轮、文本选择、按钮点击不受影响。
///    位移不到 6dip 抬手 = 点击（事件不吞）。
/// </para>
/// </summary>
internal static class TouchPanScroll
{
    private const double StartThreshold = 6;
    private const double FlingThreshold = 0.12;   // dip/ms
    private const double FrictionPer16Ms = 0.92;
    private const int TickMs = 15;

    private sealed class Sample
    {
        public long Ms;
        public Point Point;
    }

    private sealed class State
    {
        public bool Tracking;
        public bool Dragging;
        public Point Start;
        public Vector StartOffset;
        public Vector Target;          // 最新目标位置（夹取后）
        public bool HasTarget;

        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly System.Collections.Generic.List<Sample> Samples = new();

        public DispatcherTimer? Pump;  // 每帧把 Target 写进 Offset
        public Vector Velocity;        // dip/ms
        public bool Inertia;
        public long LastTickMs;
    }

    public static void Attach(ScrollContentPresenter presenter)
    {
        try
        {
            if (presenter.GetValue(AttachedProperty) is true) return;
            presenter.SetValue(AttachedProperty, true);

            var state = new State();

            // ── 按下：记录起点，停止惯性 ──
            presenter.AddHandler(InputElement.PointerPressedEvent,
                (_, e) =>
                {
                    if (e.Pointer.Type != PointerType.Touch) return;

                    var sv = FindScrollViewer(presenter);
                    if (sv is null) return;

                    state.Inertia = false;
                    state.Velocity = default;

                    state.Tracking = true;
                    state.Dragging = false;
                    state.Start = e.GetPosition(presenter);
                    state.StartOffset = sv.Offset;
                    state.Target = sv.Offset;
                    state.HasTarget = false;

                    state.Samples.Clear();
                    state.Samples.Add(new Sample { Ms = state.Clock.ElapsedMilliseconds, Point = state.Start });
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // ── 移动：只更新目标值，真正写 Offset 交给定时器（每帧一次） ──
            presenter.AddHandler(InputElement.PointerMovedEvent,
                (_, e) =>
                {
                    if (!state.Tracking || e.Pointer.Type != PointerType.Touch) return;

                    var now = e.GetPosition(presenter);

                    if (!state.Dragging)
                    {
                        var first = now - state.Start;
                        if (Math.Abs(first.X) < StartThreshold && Math.Abs(first.Y) < StartThreshold) return;

                        state.Dragging = true;
                        e.Pointer.Capture(presenter);
                        StartPump(presenter, state);
                    }

                    var sv = FindScrollViewer(presenter);
                    if (sv is null) return;

                    var delta = now - state.Start;
                    var target = Clamp(sv, new Vector(state.StartOffset.X - delta.X, state.StartOffset.Y - delta.Y));
                    if (target is null) return;

                    state.Target = target.Value;
                    state.HasTarget = true;

                    TrackSample(state, now);
                    e.Handled = true;
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // ── 抬手：有速度就切惯性（同一套定时器继续写），否则停泵 ──
            void End(PointerEventArgs? pe)
            {
                if (!state.Tracking) return;

                var wasDragging = state.Dragging;
                state.Tracking = false;
                state.Dragging = false;

                if (wasDragging && pe is not null)
                {
                    TrackSample(state, pe.GetPosition(presenter));

                    var velocity = EstimateVelocity(state);
                    if (velocity.Length > FlingThreshold)
                    {
                        state.Velocity = velocity;
                        state.Inertia = true;
                        state.LastTickMs = state.Clock.ElapsedMilliseconds;

                        try { pe.Pointer.Capture(null); } catch { }
                        return;
                    }
                }

                StopPump(state);

                if (wasDragging && pe is not null)
                {
                    try { pe.Pointer.Capture(null); } catch { }
                }
            }

            presenter.AddHandler(InputElement.PointerReleasedEvent,
                (_, e) => End(e), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            presenter.AddHandler(InputElement.PointerCaptureLostEvent,
                (_, _) => End(null), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[touch-pan] 挂载失败: " + ex.Message);
        }
    }

    // ────────────────────────── 每帧泵 ──────────────────────────

    /// <summary>启动 15ms 定时器：拖动期间把 Target 写进 Offset；抬手后若带速度则继续跑惯性。</summary>
    private static void StartPump(ScrollContentPresenter presenter, State state)
    {
        if (state.Pump is not null) return;

        state.LastTickMs = state.Clock.ElapsedMilliseconds;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        state.Pump = timer;

        timer.Tick += (_, _) =>
        {
            try
            {
                var sv = FindScrollViewer(presenter);
                if (sv is null) { StopPump(state); return; }

                if (state.Inertia)
                {
                    var now = state.Clock.ElapsedMilliseconds;
                    var dt = Math.Clamp(now - state.LastTickMs, 1, 60);
                    state.LastTickMs = now;

                    var decay = Math.Pow(FrictionPer16Ms, dt / 16.0);
                    state.Velocity = new Vector(state.Velocity.X * decay, state.Velocity.Y * decay);

                    if (state.Velocity.Length < 0.03)
                    {
                        ApplyOffset(sv, state.Target);   // 收尾补一次，确保落在边界内
                        StopPump(state);
                        return;
                    }

                    var next = Clamp(sv, new Vector(
                        state.Target.X + state.Velocity.X * dt,
                        state.Target.Y + state.Velocity.Y * dt));

                    if (next is null) { StopPump(state); return; }

                    // 触到边界：该方向速度清零，避免贴边抖动
                    if (Math.Abs(next.Value.X - state.Target.X) < 0.01) state.Velocity = new Vector(0, state.Velocity.Y);
                    if (Math.Abs(next.Value.Y - state.Target.Y) < 0.01) state.Velocity = new Vector(state.Velocity.X, 0);

                    state.Target = next.Value;
                    state.HasTarget = true;
                }
                else if (!state.Dragging)
                {
                    StopPump(state);
                    return;
                }

                if (state.HasTarget) ApplyOffset(sv, state.Target);
            }
            catch (Exception ex)
            {
                Core.AppLog.Debug("ui", "[touch-pan] 帧异常: " + ex.Message);
                StopPump(state);
            }
        };

        timer.Start();
    }

    private static void StopPump(State state)
    {
        try { state.Pump?.Stop(); } catch { }
        state.Pump = null;
        state.Inertia = false;
        state.HasTarget = false;
    }

    /// <summary>写 Offset（有限性已由 Clamp 保证；这里再挡一道，且只在真的变化时写）。</summary>
    private static void ApplyOffset(ScrollViewer sv, Vector target)
    {
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y)) return;

        var current = sv.Offset;
        if (Math.Abs(current.X - target.X) < 0.01 && Math.Abs(current.Y - target.Y) < 0.01) return;

        sv.Offset = target;
    }

    // ────────────────────────── 速度估算 ──────────────────────────

    private static void TrackSample(State state, Point point)
    {
        var now = state.Clock.ElapsedMilliseconds;
        state.Samples.Add(new Sample { Ms = now, Point = point });

        while (state.Samples.Count > 2 && now - state.Samples[0].Ms > 120)
            state.Samples.RemoveAt(0);
    }

    private static Vector EstimateVelocity(State state)
    {
        if (state.Samples.Count < 2) return default;

        var first = state.Samples[0];
        var last = state.Samples[^1];
        var ms = Math.Max(1, last.Ms - first.Ms);

        return new Vector(-(last.Point.X - first.Point.X) / ms, -(last.Point.Y - first.Point.Y) / ms);
    }

    private static readonly Avalonia.StyledProperty<bool> AttachedProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<ScrollContentPresenter, bool>("TouchPanAttached", typeof(TouchPanScroll));

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
