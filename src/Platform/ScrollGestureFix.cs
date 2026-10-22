using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 把「滚动手势识别器」从视觉树上彻底摘掉（2026-10-22，第二次修）。
///
/// <para>
/// ⛔⛔ 崩溃链条（三次 dump 完全一致，0xC00000FD 栈溢出）：
///    <c>ScrollGestureRecognizer</c> 在 PointerReleased 之后延时发 <c>ScrollGesture</c>
///    → <c>ScrollContentPresenter.OnScrollGesture</c> 里 <c>SetCurrentValue(Offset)</c>
///    → <c>ScrollViewer.Offset</c> 的**双向模板绑定**把值推回 presenter
///    → presenter 的 <c>OnPropertyChanged</c> 再写一次 Offset ……
///    只要 <c>CoerceOffset</c> 的结果与传入值不等（NaN / extent、viewport 未定），环就**永不收敛**：
///    实测同一段 17 帧重复 500+ 次，栈直接爆。2026-10-18「点侧边栏再点导航」、
///    2026-10-22 两次（含窗口在托盘静置 11 分钟、无任何人工输入 —— 幽灵手势也会踩中）。
/// </para>
/// <para>
/// ⛔ 走过的弯路（别再试）：
///    · 在页面根上注册 Tunnel 的 ScrollGesture 处理器 —— **无效**：该事件注册路由是
///      <c>Bubble</c>，压根没有隧道阶段；等冒泡传到页面根，presenter 早把 Offset 写进去了。
///    · 只查 <c>ScrollViewer.GestureRecognizers</c> —— 恒为空；识别器是
///      **<c>ScrollContentPresenter</c>** 自己挂的（识别器在 presenter 上，事件处理也在 presenter 上）。
/// </para>
/// <para>
/// ✅ 现在：遍历视觉树，对**每个**带 <c>GestureRecognizers</c> 的元素（重点 ScrollContentPresenter
///    与 ScrollViewer），把其中类型名含 <c>ScrollGestureRecognizer</c> 的项摘掉；
///    <c>GestureRecognizerCollection</c> 没有公开 Remove（内部是 <c>List&lt;GestureRecognizer&gt; _items</c>），
///    所以用反射拿内部 list 删。摘掉之后手势事件根本不会产生 —— 源头切断。
/// </para>
/// <para>
/// 副作用：触摸 / 触控板**两指平移**失效；滚轮、滚动条、键盘翻页、代码设 Offset 全部正常。
/// </para>
/// </summary>
internal static class ScrollGestureFix
{
    /// <summary>安装过的根（弱引用）。</summary>
    private static readonly System.Collections.Generic.List<WeakReference<Visual>> Roots = new();

    /// <summary>定时巡检（1.2 秒一次）：识别器不一定是"扫描那一刻"就在的 ——
    /// ScrollContentPresenter 的模板应用、页面重建都会在之后才挂上。</summary>
    private static Avalonia.Threading.DispatcherTimer? _timer;


    /// <summary>在某个根上安装巡检（幂等）。调用方：PageBase 构造、MainWindow 构造。</summary>
    public static void Install(InputElement root)
    {
        try
        {
            lock (Roots)
            {
                foreach (var w in Roots)
                    if (w.TryGetTarget(out var seen) && ReferenceEquals(seen, root)) return;

                if (root is Visual v) Roots.Add(new WeakReference<Visual>(v));
            }

            Strip(root);

            if (_timer is null)
            {
                _timer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1200),
                };
                _timer.Tick += (_, _) => Sweep();
                _timer.Start();
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("ui", "[scroll-fix] 安装失败: " + ex.Message);
        }
    }

    /// <summary>把登记过的根都扫一遍（定时器调用）。</summary>
    private static void Sweep()
    {
        try
        {
            Visual?[] snapshot;
            lock (Roots)
            {
                snapshot = new Visual?[Roots.Count];
                for (var i = 0; i < Roots.Count; i++)
                {
                    snapshot[i] = Roots[i].TryGetTarget(out var v) ? v : null;
                }
            }

            foreach (var root in snapshot)
                if (root is not null) Strip(root);
        }
        catch
        {
            // 巡检失败不影响任何功能
        }
    }

    /// <summary>遍历子树，把所有滚动手势识别器摘掉。</summary>
    public static void Strip(Visual root)
    {
        try
        {
            if (root is InputElement ie) Fix(ie);

            foreach (var child in root.GetVisualChildren())
                Strip(child);
        }
        catch
        {
            // 视觉树遍历中变化是常态，下一帧再来
        }
    }

    private static void Fix(InputElement element)
    {
        try
        {
            var coll = element.GestureRecognizers;
            if (coll is null) return;


            // 内部实体是 List<GestureRecognizer> _recognizers（公开 API 没有 Remove）
            var itemsField = coll.GetType().GetField("_recognizers", BindingFlags.NonPublic | BindingFlags.Instance);
            if (itemsField?.GetValue(coll) is not IList items) return;

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
