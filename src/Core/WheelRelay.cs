using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 滚轮接力（2026-10-13 上游建。Nick：「首页左右两边的空白区还能滚，别的页面只有中间那一条能滚」）。
///
/// 现象：除了首页，其它页面为了把内容钉在中间 960 宽，滚动容器**只盖住中间那一列** ——
///   两侧留白、标题行、页脚这些"看着有地方、其实没有可滚的东西"的区域，
///   滚轮命中的是页面根节点；根节点默认没有背景、不参与命中测试，
///   事件从滚动容器**外面**开始冒泡，容器自己永远收不到 ⇒ 鼠标在那儿滚，页面纹丝不动。
///
/// 做法（两步，缺一不可）：
///   ① AXAML：给**页面根 Grid** 加 <c>Background="Transparent"</c> —— 不画任何像素，
///      但把整块面积纳入命中测试（与 App.axaml 给全站 ScrollViewer 加 Transparent 是同一招）；
///   ② 代码：构造函数里 <c>Core.WheelRelay.Attach(this, 主滚动容器)</c>。
///
/// ⚠️ 接在**这一页（PageBase）**上、而不是根 Grid 上：PageBase 是冒泡路线上离得最近的共同祖先，
///    一条 AddHandler 就够，也不用给根 Grid 起名字。
/// ⚠️ <c>handledEventsToo</c> 传 false：指针落在**真的**滚动容器里时事件已被它自己消化，
///    这里不能再动一次（否则一格里滚两倍）。
/// ⚠️ 只做 ChangeView，不碰 ScrollMode —— 页面自己把滚动锁掉（如侧边布局拖动时）时自然不生效。
///
/// ⚠️ WinUI → Avalonia 移植说明（2026-10-17）：
///   · <c>PointerRoutedEventArgs.GetCurrentPoint</c> → Avalonia 的 <c>PointerEventArgs.GetPosition(host)</c>；
///   · 滚轮位移在 Avalonia 里走 <c>PointerWheelEventArgs.Delta</c>（整档，一般 ±120）；
///   · Avalonia 的 <c>ScrollViewer.Offset</c> 是 Vector，没有 <c>ChangeView</c> —— 直接赋值
///     <c>Offset = new Vector(Offset.X, to)</c> 即可（无动画，与原版 disableAnimation:true 同效）。
/// </summary>
public static class WheelRelay
{
    /// <summary>滚轮「一档」的位移。WinUI 的 ScrollViewer 默认一档＝3 行 ≈ 48 DIP，这里对齐它。</summary>
    private const double NotchDip = 48.0;

    /// <summary>一次挂载记录：能转交给哪些容器。</summary>
    private sealed class Hook
    {
        public EventHandler<PointerWheelEventArgs> Handler = null!;
        public Control[] Targets = Array.Empty<Control>();
    }

    private static readonly ConditionalWeakTable<Control, Hook> Hooks = new();

    /// <summary>
    /// 自动接线：把页面上**最外层**的 ScrollViewer 全找出来挂上（页面挂树之后跑一次）。
    ///
    /// <para>为什么要有它：上游是每个页面在构造函数里显式 <c>WheelRelay.Attach(this, 主滚动容器)</c>，
    /// 但那要求给每个滚动容器起名。移植版页面多、且不少页面的滚动区是模板里的，
    /// 所以这里做一个等价的「自动版」：挂树后扫一遍可视树，只取**不被别的 ScrollViewer 包着**的那些
    /// （嵌套滚动区由内层自己消化，外层不该再重复转一次），再走同一套 OnWheel。</para>
    /// </summary>
    public static void AttachAuto(Control scrollHost)
    {
        void Wire()
        {
            try
            {
                var all = new List<ScrollViewer>();
                foreach (var v in scrollHost.GetVisualDescendants())
                    if (v is ScrollViewer sv) all.Add(sv);

                // 只留最外层（父链上没有别的 ScrollViewer）
                var outer = new List<Control>();
                foreach (var sv in all)
                {
                    var nested = false;
                    foreach (var anc in sv.GetVisualAncestors())
                    {
                        if (anc is ScrollViewer) { nested = true; break; }
                    }
                    if (!nested) outer.Add(sv);
                }

                if (outer.Count > 0) Attach(scrollHost, outer.ToArray());
            }
            catch
            {
                // 接线失败只是"留白处滚不动"，不该影响页面本身
            }
        }

        if (scrollHost.IsAttachedToVisualTree())
            Wire();
        else
            scrollHost.AttachedToVisualTree += (_, _) => Wire();
    }

    /// <summary>
    /// 把页面上收到的滚轮，转交给 targets 里第一个「指针压在上面」或「真的滚得动」的滚动区。
    /// </summary>
    /// <param name="scrollHost">接事件的元素，传 <c>this</c>（页面自身）即可。</param>
    /// <param name="targets">滚动容器，按优先级排。可给 ScrollViewer，也可给 ListView / ListBox /
    /// ItemsControl —— 会自己找到内部的滚动区。同页互斥的两个滚动区（如抽号的抽取/分组视图）都传进来。</param>
    public static void Attach(Control scrollHost, params Control[]? targets)
    {
        if (Hooks.TryGetValue(scrollHost, out var old))
            scrollHost.RemoveHandler(InputElement.PointerWheelChangedEvent, old.Handler);

        var hook = new Hook
        {
            Targets = targets is { Length: > 0 } ? targets : Array.Empty<Control>(),
        };
        hook.Handler = (_, e) => OnWheel(scrollHost, hook, e);

        Hooks.Remove(scrollHost);
        Hooks.Add(scrollHost, hook);
        scrollHost.AddHandler(InputElement.PointerWheelChangedEvent, hook.Handler, handledEventsToo: false);
    }

    private static void OnWheel(Control host, Hook hook, PointerWheelEventArgs e)
    {
        var point = e.GetPosition(host);
        var delta = e.Delta.Y;                    // 一档 ±1（有的鼠标是 ±120 的倍数），统一按档算
        if (delta == 0) return;

        // 指针压在哪条滚动区上就转给哪条（同页有两个时才分得清）；都没压中就落到第一条。
        ScrollViewer? pick = null;
        ScrollViewer? first = null;
        foreach (var target in hook.Targets)
        {
            var sv = Resolve(target);
            if (sv is null || sv.Extent.Height <= sv.Viewport.Height) continue;   // 收起 / 没内容 / 装得下 ⇒ 跳过
            first ??= sv;
            if (Contains(sv, host, point))
            {
                pick = sv;
                break;
            }
        }

        pick ??= first;
        if (pick is null) return;

        // 一档 = NotchDip；e.Delta.Y 一般是 ±1（±120 的就除回来）
        var notches = Math.Abs(delta) >= 120 ? delta / 120.0 : delta;
        var max = Math.Max(0, pick.Extent.Height - pick.Viewport.Height);

        // ⛔⛔ 2026-10-18 修（崩溃 0xc00000fd）：布局还没跑完时 Extent/Viewport 可能是 NaN，
        //    `Math.Clamp(x, 0, NaN)` 返回的是 **NaN**，一旦把 NaN 写进 ScrollViewer.Offset，
        //    Avalonia 的 CoerceOffset 会一路 NaN 下去：NaN != NaN ⇒ 每次都被判成"变了"，
        //    ScrollContentPresenter 与 ScrollViewer 之间的双向绑定就无限互相触发，
        //    栈直接爆掉（dump 里那 600 多层 CoerceOffset 就是它）。所以这里必须挡住非有限值。
        if (!double.IsFinite(max) || !double.IsFinite(pick.Offset.Y))
        {
            Core.AppLog.Debug("ui", $"滚轮接力：跳过（max={max}, offset={pick.Offset.Y} 不是有限值）");
            return;
        }

        var to = Math.Clamp(pick.Offset.Y - notches * NotchDip, 0, max);
        if (!double.IsFinite(to)) return;

        e.Handled = true;
        pick.Offset = new Vector(pick.Offset.X, to);
    }

    /// <summary>把目标解析成真正的 ScrollViewer：给的本来就是就直接用，给的是列表控件就找它内部的滚动区。</summary>
    private static ScrollViewer? Resolve(Control? target)
    {
        if (target is null) return null;
        if (target is ScrollViewer direct) return direct;

        // 广度优先 ⇒ 拿到的是**最外层**那个滚动区（ListBox / ItemsControl 模板里的那个）
        foreach (var child in target.GetVisualDescendants())
        {
            if (child is ScrollViewer sv && sv.Bounds.Height > 0) return sv;
        }
        return null;
    }

    /// <summary>指针位置是否落在某个容器此刻的实际范围里（同页有多个滚动区时用来挑一个）。</summary>
    private static bool Contains(ScrollViewer sv, Control host, Point p)
    {
        try
        {
            // ⚠️ Avalonia 的 Matrix 没有 TransformBounds（WinUI 才有）→ 用 TranslatePoint 算左上角再拼矩形。
            var topLeft = sv.TranslatePoint(new Point(0, 0), host);
            if (topLeft is not { } tl) return false;

            var rect = new Rect(tl, sv.Bounds.Size);
            return rect.Contains(p);
        }
        catch { return false; }
    }
}
