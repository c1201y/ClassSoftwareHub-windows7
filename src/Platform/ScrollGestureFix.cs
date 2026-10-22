using System;
using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 把「滚动手势识别器」从视觉树上摘掉（2026-10-22）—— 本项目 0xC00000FD 崩溃的根因处置。
///
/// <para>
/// ⛔⛔ 崩溃链条（三次 dump 完全一致）：
///    <c>ScrollGestureRecognizer</c> 在 PointerReleased 之后延时发 <c>ScrollGesture</c>
///    → <c>ScrollContentPresenter.OnScrollGesture</c> 里 <c>SetCurrentValue(Offset)</c>
///    → <c>ScrollViewer.Offset</c> 的**双向模板绑定**把值推回 presenter
///    → presenter 的 <c>OnPropertyChanged</c> 再写一次 Offset ……
///    只要 <c>CoerceOffset</c> 的结果与传入值不等（NaN / extent、viewport 未定），环就**永不收敛**：
///    实测同一段 17 帧重复 500+ 次，栈直接爆。触发场景包括「点侧边栏再点导航」和
///    「窗口在托盘静置 11 分钟、无任何人工输入」（幽灵手势）。
/// </para>
/// <para>
/// ⛔ 走过的弯路（别再试）：
///    · 页面根上注册 Tunnel 的 ScrollGesture 处理器 —— 无效：该事件注册路由是 <c>Bubble</c>，没有隧道阶段。
///    · 只查 <c>ScrollViewer.GestureRecognizers</c> —— 恒为空；识别器是 <c>ScrollContentPresenter</c> 自己挂的。
///    · 反射字段名写 <c>_items</c> —— 实际是 <c>_recognizers</c>（写错时静默返回，白忙一轮）。
///    · 每隔 1 秒全树遍历一次 —— 实机 CPU 直接被吃掉一半、连启动日志都写不出来。**扫描必须是一次性的**。
/// </para>
/// <para>
/// ✅ 现在：每个根只扫**三次**（立刻 / 下一个后台调度 / 600ms 后）—— 覆盖"模板应用时才挂识别器"的时序；
///    页面每次导航都会重建并重新调 <see cref="Install"/>，新页面同样覆盖。
///    摘掉之后手势事件根本不会产生，递归的触发源消失。
/// </para>
/// <para>
/// 副作用：触摸 / 触控板**两指平移**失效；滚轮、滚动条、键盘翻页、代码设置 Offset 全部正常。
/// </para>
/// </summary>
internal static class ScrollGestureFix
{
    /// <summary>防重入：摘识别器会牵动布局回调，避免重入遍历。</summary>
    private static bool _stripping;

    /// <summary>在某个根上安装（幂等）。调用方：PageBase 构造、MainWindow 构造。</summary>
    public static void Install(InputElement root)
    {
        try
        {
            Strip(root);

            Dispatcher.UIThread.Post(() => Strip(root), DispatcherPriority.Background);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Strip(root);
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[scroll-fix] 安装失败: " + ex.Message);
        }
    }

    /// <summary>遍历子树，把所有滚动手势识别器摘掉。</summary>
    public static void Strip(Visual root)
    {
        if (_stripping) return;

        try
        {
            _stripping = true;
            Walk(root);
        }
        catch
        {
            // 视觉树在遍历中被改是常态，忽略
        }
        finally
        {
            _stripping = false;
        }
    }

    private static void Walk(Visual root)
    {
        if (root is InputElement element) Fix(element);

        foreach (var child in root.GetVisualChildren())
            Walk(child);
    }

    private static void Fix(InputElement element)
    {
        try
        {
            var coll = element.GestureRecognizers;
            if (coll is null) return;

            // 内部是 List<GestureRecognizer> _recognizers（公开 API 只有 Count、没有 Remove）
            var field = coll.GetType().GetField("_recognizers", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(coll) is not IList items) return;

            var removed = 0;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var name = items[i]?.GetType().Name ?? "";
                if (name.Contains("ScrollGestureRecognizer", StringComparison.Ordinal))
                {
                    items.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0)
                Core.AppLog.Debug("ui",
                    $"[scroll-fix] 从 {element.GetType().Name} 摘掉滚动手势识别器 {removed} 个（0xC00000FD 根因）");
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[scroll-fix] 摘除失败: " + ex.Message);
        }
    }
}
