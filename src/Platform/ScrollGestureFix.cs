using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 在**隧道路由**上吞掉「滚动手势」事件（2026-10-22）——本项目的两次崩溃根因。
///
/// <para>
/// ⛔⛔ 崩溃链条（两次 dump 完全一致，0xC00000FD 栈溢出）：
///    <c>ScrollGestureRecognizer</c> 在 PointerReleased 之后延时发 <c>ScrollGesture</c>
///    → <c>ScrollContentPresenter.OnScrollGesture</c> 里 <c>SetCurrentValue(Offset)</c>
///    → <c>ScrollViewer.Offset</c> 的**双向模板绑定**把值推回 presenter
///    → presenter 的 <c>OnPropertyChanged</c> 再写一次 Offset ……
///    只要 <c>CoerceOffset</c> 算出的结果与传入值不等（NaN 参与、或 extent/viewport 未定），
///    这个环就**永不收敛**：实测同一段 17 帧重复约 500 次，栈直接爆。
///    2026-10-18 触发场景「点完侧边栏再点导航」；2026-10-22 触发场景「窗口收进托盘 11 分钟后
///    自己崩的」—— 后者没有任何人工输入，说明幽灵手势（触控板噪声 / 合成事件）也会踩中。
/// </para>
/// <para>
/// ⛔ 为什么不能"把识别器删掉"：<c>ScrollGestureRecognizer</c> 并**不在**
///    <c>ScrollViewer.GestureRecognizers</c> 集合里（实测该集合恒为空），它是
///    <c>ScrollContentPresenter</c> 内部自建、直接订阅事件的，公开 API 摘不掉。
/// </para>
/// <para>
/// ✅ 处置：在**页面根 / 窗口根**上注册 Tunnel 阶段的
///    <see cref="Gestures.ScrollGestureEvent"/> 与 <see cref="Gestures.ScrollGestureEndedEvent"/>
///    处理器并 <c>Handled = true</c> —— 隧道先于冒泡，presenter 的处理器根本没机会跑，
///    递归从源头断掉。代价：触摸 / 触控板两指平移失效；
///    **滚轮、滚动条、键盘翻页、代码设置 Offset 全部保留**（都不走这个事件）。
///    教室机以鼠标为主，用"两指平移"换"不崩"是划算的。
/// </para>
/// </summary>
internal static class ScrollGestureFix
{
    private static readonly ConditionalWeakTable<InputElement, object> Hooked = new();

    /// <summary>在某个根上安装（幂等）。调用方：PageBase 构造、MainWindow 构造。</summary>
    public static void Install(InputElement root)
    {
        try
        {
            if (Hooked.TryGetValue(root, out _)) return;
            Hooked.Add(root, new object());

            root.AddHandler(Gestures.ScrollGestureEvent,
                (_, e) => e.Handled = true, RoutingStrategies.Tunnel, handledEventsToo: true);

            root.AddHandler(Gestures.ScrollGestureEndedEvent,
                (_, e) => e.Handled = true, RoutingStrategies.Tunnel, handledEventsToo: true);

            // 惯性起步同样拦掉：它就发生在 ScrollGestureRecognizer 内部，进去之后还是会写 Offset
            root.AddHandler(Gestures.ScrollGestureInertiaStartingEvent,
                (_, e) => e.Handled = true, RoutingStrategies.Tunnel, handledEventsToo: true);

            Core.AppLog.Debug("ui", "[scroll-fix] 已在该根上吞掉滚动手势（Tunnel）");
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[scroll-fix] 安装失败: " + ex.Message);
        }
    }
}
