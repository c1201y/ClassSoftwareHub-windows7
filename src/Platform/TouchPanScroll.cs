using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 触屏「单指平移滚动」的安全 + 流畅实现（2026-10-23 第二次迭代）。
///
/// <para>
/// 背景一（为什么要自己做）：Avalonia 自带的 <c>ScrollGestureRecognizer</c> 会走
/// <c>ScrollContentPresenter.OnScrollGesture</c> → 写 <c>ScrollViewer.Offset</c> →
/// 与 viewer 的双向绑定互相推值；一旦夹取结果与传入值永不相等（NaN 参与时必然如此），
/// 这个环就不收敛 —— 0xC00000FD 栈溢出，dump 三次实锤。所以那个识别器已经被摘掉。
/// </para>
/// <para>
/// 背景二（为什么不能每个事件都写 Offset）：写 <c>Offset</c> 会触发**重新布局 + 整页重排**，
/// 而 Win7 路线是软件渲染、长页面节点多 —— 每个 PointerMoved 都重排一次就是"滑动不跟手"。
/// </para>
/// <para>
/// ✅ 现在：拖动过程中只改**渲染变换**（<see cref="TranslateTransform"/>，纯视觉、零布局），
///    抬手时把最终位置**一次性**写进 <c>Offset</c> 并撤掉变换。既流畅，又只写一次 Offset
///    （仍然带 extent/viewport 有限性夹取，NaN 直接放弃）。
/// </para>
/// <para>
/// ⚠️ 只处理 <see cref="PointerType.Touch"/>：鼠标、触控板滚轮、文本选择、按钮点击都不受影响。
///    按下后位移不到 6dip 就抬手 = 点击，事件不吞。
///    有意不做惯性滑动：惯性需要定时器持续写 Offset，正是崩溃链最危险的一环。
/// </para>
/// </summary>
internal static class TouchPanScroll
{
    /// <summary>超过这个位移（dip）才算"开始拖动"，否则按点击处理。</summary>
    private const double StartThreshold = 6;

    private sealed class State
    {
        public bool Tracking;          // 已按下，还没决定是不是拖动
        public bool Dragging;          // 已接管
        public Point Start;            // 按下点（呈现器坐标）
        public Vector StartOffset;     // 按下时的滚动偏移
        public Visual? Content;        // 被平移的内容（presenter.Child）
        public TranslateTransform? Shift; // 拖动期间挂上去的渲染变换
        public Vector Pending;         // 当前目标偏移（抬手时一次性提交）
    }

    /// <summary>给一个内容呈现器挂上触屏平移（幂等）。</summary>
    public static void Attach(ScrollContentPresenter presenter)
    {
        try
        {
            if (presenter.GetValue(AttachedProperty) is true) return;
            presenter.SetValue(AttachedProperty, true);

            var state = new State();

            // ── 按下：只记录，不动任何东西（轻点要能正常点按钮） ──
            presenter.AddHandler(InputElement.PointerPressedEvent,
                (_, e) =>
                {
                    if (e.Pointer.Type != PointerType.Touch) return;

                    var sv = FindScrollViewer(presenter);
                    if (sv is null) return;

                    state.Tracking = true;
                    state.Dragging = false;
                    state.Start = e.GetPosition(presenter);
                    state.StartOffset = sv.Offset;
                    state.Pending = sv.Offset;
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

                    e.Handled = true;
                },
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

            // ── 抬手 / 捕获丢失：一次性提交位置，撤掉变换 ──
            void End(PointerEventArgs? pe)
            {
                if (!state.Tracking) return;

                var wasDragging = state.Dragging;
                state.Tracking = false;
                state.Dragging = false;

                var sv = FindScrollViewer(presenter);
                if (wasDragging && sv is not null)
                {
                    // 先落位、再撤变换：同一帧内完成，用户看不到跳变
                    var commit = Clamp(sv, state.Pending);
                    if (commit is not null) sv.Offset = commit.Value;
                }

                EndDrag(state);

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

    /// <summary>结束拖动：撤掉渲染变换（位置已由调用方提交）。</summary>
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
