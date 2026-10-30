using System;
using System.Threading;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 窗口位移动画（侧边栏滑入/滑出、浮窗滑入/滑出）的"正在进行"提示。
///
/// 为什么要这个东西（2026-10-25 实测）：首页推荐条有一支 16ms 定时器在**永不停歇**地
/// 平移卡片 —— 主窗可见时等于每 16ms 重画一次整个 1950×1135 的窗口。侧边栏滑动那 200 多毫秒里，
/// 这支重画和窗口 Move 抢同一条 UI 线程，实测单帧被顶到 100ms 以上，滑动就是"一顿一顿"的。
/// 滑动期间把这类纯装饰性的连续重画让开，滑动结束后自己恢复，观感差别极大
/// （本机实测：滑动帧间隔从 30~130ms 回到 10~19ms）。
///
/// 用法：滑动开始时 <see cref="Hold"/> 一个时长；装饰性循环（推荐条等）每帧先问 <see cref="Busy"/>。
/// 用"截止时间"而不是配对的 Begin/End —— 动画可能被收起/拖动/再展开打断、也可能根本跑不完全程，
/// 配对计数迟早会漏减；截止时间到期自动失效，漏不掉，也不会把装饰动画永久冻住。
/// </summary>
public static class MotionGate
{
    private static long _busyUntil;

    /// <summary>是否有窗口位移动画正在跑。</summary>
    public static bool Busy => Environment.TickCount64 < Volatile.Read(ref _busyUntil);

    /// <param name="ms">动画时长；额外多留 120ms 余量，盖住起步等待和最后一帧的收尾。</param>
    public static void Hold(double ms) =>
        Volatile.Write(ref _busyUntil, Environment.TickCount64 + (long)ms + 120);
}
