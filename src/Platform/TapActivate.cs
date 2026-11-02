using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 卡片类元素的点击绑定：**抬手、位移未超阈值、且仍落在元素内**才算点击。
///
/// <para>
/// 用于替代 <c>control.PointerPressed += ...</c> 的「按下即触发」写法。触屏上滑动列表时手指必然
/// 先落在卡片上，按下即触发会让滚动变成「顺手点开一张卡」；而抬手才触发时，拖动会先被
/// <see cref="TouchPanScroll"/> 接管并抢走指针捕获，本方法收到 CaptureLost 后放弃这次点击。
/// </para>
/// <para>
/// 位移阈值直接取 <see cref="TouchPanScroll.StartThreshold"/>，保证「它认为在拖动」与
/// 「我认为不是点击」是同一个界限，不会出现中间地带两边都不认。
/// </para>
/// </summary>
internal static class TapActivate
{
    /// <summary>绑定一次点击（幂等性由调用方保证：只在元素构建时调用一次）。</summary>
    public static void Bind(Control target, Action activate)
    {
        var press = default(Point);
        var armed = false;
        var moved = false;

        target.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            // 右键/中键不算点击
            if (e.Pointer.Type == PointerType.Mouse &&
                !e.GetCurrentPoint(target).Properties.IsLeftButtonPressed)
            {
                armed = false;
                return;
            }

            press = e.GetPosition(target);
            armed = true;
            moved = false;
        }, RoutingStrategies.Bubble, handledEventsToo: false);

        target.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (!armed || moved) return;

            var now = e.GetPosition(target);
            if (Math.Abs(now.X - press.X) > TouchPanScroll.StartThreshold ||
                Math.Abs(now.Y - press.Y) > TouchPanScroll.StartThreshold)
                moved = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        target.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!armed) return;
            armed = false;

            // 从元素外抬手（拖出去再松）不算点击
            var now = e.GetPosition(target);
            if (now.X < 0 || now.Y < 0 ||
                now.X > target.Bounds.Width || now.Y > target.Bounds.Height) return;

            // 距离再判一次：拖动期间指针被滚动层捕获时收不到 Moved，只能靠起点/终点补上
            if (moved ||
                Math.Abs(now.X - press.X) > TouchPanScroll.StartThreshold ||
                Math.Abs(now.Y - press.Y) > TouchPanScroll.StartThreshold) return;

            activate();
        }, RoutingStrategies.Bubble, handledEventsToo: false);

        // 拖动开始时指针被滚动层抢走捕获，这次按下就此作废
        target.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => armed = false,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
