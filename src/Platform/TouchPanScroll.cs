using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 触屏「单指平移滚动」的安全实现（2026-10-23）。
///
/// <para>
/// 背景：Avalonia 自带的 <c>ScrollGestureRecognizer</c> 会触发
/// <c>ScrollContentPresenter.OnScrollGesture</c> → 写 <c>ScrollViewer.Offset</c> →
/// 与 viewer 的**双向模板绑定**互相推值、在某些状态下永不收敛（0xC00000FD，dump 三次实锤）。
/// 我们把那个识别器摘掉了，但教室机有触摸屏，**不能让触屏滑不动** ——
/// 于是这里用 Pointer 事件自己实现一份最小可行的平移：
/// 手指按下 → 移动超过阈值后接管 → 直接把 Offset 设成「起点偏移 − 手指位移」（带有限性夹取）
/// → 抬手结束。全程只做**一次**赋值，不经过任何绑定回推，因此不可能触发那个死循环。
/// </para>
/// <para>
/// ⛔ 有意不做惯性滑动（inertia）：惯性要跑定时器持续写 Offset，正是崩溃链最危险的部分；
///    触屏翻页场景下"手停就停"完全够用。
/// </para>
/// <para>
/// ⚠️ 只对 <see cref="PointerType.Touch"/> 生效：鼠标拖动、触控板（走滚轮事件）、
///    文本选择等行为一律不受影响。按下后**没有超过阈值就抬手**仍然算点击（不吞事件）。
/// </para>
/// </summary>
internal static class TouchPanScroll
{
    /// <summary>超过这个位移（dip）才算"开始拖动"，否则按点击处理。</summary>
    private const double StartThreshold = 6;

    private sealed class State
    {
        public bool Tracking;      // 已按下，还没决定是不是拖动
        public bool Dragging;      // 已接管
        public Point Start;        // 按下点
        public Point Last;         // 上一次移动点
        public Vector StartOffset; // 按下时的滚动偏移
    }

    /// <summary>给一个内容呈现器挂上触屏平移（幂等）。</summary>
    public static void Attach(ScrollContentPresenter presenter)
    {
        try
        {
            if (presenter.GetValue(AttachedProperty) is true) return;
            presenter.SetValue(AttachedProperty, true);

            var state = new State();

            presenter.AddHandler(InputElement.PointerPressedEvent,
                (_, e) =>
                {
                    if (e.Pointer.Type != PointerType.Touch) return;

                    var sv = FindScrollViewer(presenter);
                    if (sv is null) return;

                    state.Tracking = true;
                    state.Dragging = false;
                    state.Start = e.GetPosition(presenter);
                    state.Last = state.Start;
                    state.StartOffset = sv.Offset;
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            presenter.AddHandler(InputElement.PointerMovedEvent,
                (_, e) =>
                {
                    if (!state.Tracking || e.Pointer.Type != PointerType.Touch) return;

                    var now = e.GetPosition(presenter);
                    var moved = now - state.Start;

                    if (!state.Dragging)
                    {
                        if (Math.Abs(moved.X) < StartThreshold && Math.Abs(moved.Y) < StartThreshold) return;

                        state.Dragging = true;
                        e.Pointer.Capture(presenter);
                    }

                    var sv = FindScrollViewer(presenter);
                    if (sv is null) return;

                    var delta = now - state.Last;
                    state.Last = now;

                    if (TryScroll(sv, new Vector(sv.Offset.X - delta.X, sv.Offset.Y - delta.Y)))
                        e.Handled = true;
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            void End(object? _, bool wasPointerEvent, PointerEventArgs? pe)
            {
                if (!state.Tracking) return;

                var wasDragging = state.Dragging;
                state.Tracking = false;
                state.Dragging = false;

                if (wasDragging && wasPointerEvent && pe is not null)
                {
                    try { pe.Pointer.Capture(null); } catch { }
                }
            }

            presenter.AddHandler(InputElement.PointerReleasedEvent,
                (s, e) => End(s, true, e), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // 捕获丢失事件没有 Pointer 属性可释放，只清状态
            presenter.AddHandler(InputElement.PointerCaptureLostEvent,
                (s, e) => End(s, false, null), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[touch-pan] 挂载失败: " + ex.Message);
        }
    }

    private static readonly Avalonia.StyledProperty<bool> AttachedProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<ScrollContentPresenter, bool>("TouchPanAttached", typeof(TouchPanScroll));

    /// <summary>把偏移写到 ScrollViewer 上，且只在**有限、且确实变化**时才写。</summary>
    private static bool TryScroll(ScrollViewer sv, Vector target)
    {
        var maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

        // ⛔ 只要有一个不是有限值就整次放弃：NaN 写进 Offset 正是那条死循环的引信。
        if (!double.IsFinite(maxX) || !double.IsFinite(maxY)) return false;
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y)) return false;

        var x = Math.Clamp(target.X, 0, maxX);
        var y = Math.Clamp(target.Y, 0, maxY);

        var current = sv.Offset;
        if (Math.Abs(current.X - x) < 0.01 && Math.Abs(current.Y - y) < 0.01) return false;

        sv.Offset = new Vector(x, y);
        return true;
    }

    private static ScrollViewer? FindScrollViewer(Visual from) =>
        from.FindAncestorOfType<ScrollViewer>(includeSelf: false);
}
