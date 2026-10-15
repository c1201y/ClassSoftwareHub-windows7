using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 屏幕边缘侧边栏：贴在屏幕的某一条边上（上下左右都能放）。
/// 全屏放 PPT / 视频时够不到任务栏，从边上点一下就能用工具。
///   · 收起 = **两套样式**（2026-10-11 加）：grip 圆角抓手（原版）｜ line 细竖线（窗口缩成 14×220 的圆角细线）。
///     都是"点一下展开"，另外还能"朝屏幕外滑一下"展开（见 <see cref="Strip_PointerMoved"/>）。
///     **拖拽换边已于 2026-10-04 整条撤除**（见字段区注释），要换边请到设置里选。
///   · 展开 = **两套样式**（2026-10-11 加）：classic 工具竖排列表 + 底排五颗（原版）｜
///     grid 图标网格（4 列一页 + 分页点）+ 快捷设置行。见 <see cref="SyncStyleViews"/>。
/// 位置（贴哪条边 + 沿边位置）会记在设置里；「位置复原」= 回到右边的居中位置。
/// 置顶、不进任务栏、无标题栏、不能缩放/最大化/最小化。
///
/// ⚠️ 2026-09-27（Nick）：**左右两边可以同时显示**（设置里选「左右两边」= <c>SidebarEdge</c> 为 "both"）。
/// 所以它不再是个单例 —— 见 <see cref="_pool"/>：一条边一个实例，设置里的 "both" 会被拆成 left + right 两条。
/// 两条共享 <c>SidebarPosRatio</c>，上下位置天然一致；拖任意一条时对面实时跟随（<see cref="FollowPartner"/>）。
/// 上边 / 下边**只有单条**（Nick：横着放一条就够了）。
///
/// ⚠️ 2026-09-28（Nick）：再加**两种模式**（<c>SidebarMode</c>）——
///   · <c>dock</c> 贴靠模式：**只贴左右两条边**（可选「左右两边」同时显示两条），沿边位置共用
///     <c>SidebarPosRatio</c>；上/下边**不归它管**。
///   · <c>free</c> 自由模式：**四条边都能吸**（左/右/上/下），贴哪条边存 <c>SidebarFreeEdge</c>，
///     贴上/下边时是横条。它**不是**"浮在屏幕中间不吸边"—— 侧边栏永远吸在某条边上。
/// 两种模式走的是**同一套**实例池与吸附逻辑（<see cref="_pool"/> / <see cref="SyncInstances"/>），
/// 差别只在"允许哪几条边"（见 <see cref="DesiredEdges"/>）与"松手时把边存进哪个设置"（<see cref="DockToNearestEdge"/>）。
///
/// 用法：<c>ToolSidebarWindow.ShowSidebar()</c> / <c>HideSidebar()</c> / <c>ApplySetting()</c>。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · AppWindow（Move/Resize/MoveAndResize/Position/Size，全是物理像素）→ 统一改走 Win32
///     <c>SetWindowPos / GetWindowRect</c>（无边框窗口外框 = 客户区，坐标语义与原版完全一致），
///     封成本类底部的 <c>MoveWin / ResizeWin / MoveResizeWin / WinPos / WinSizePx</c>；
///   · DisplayArea.Primary.WorkArea → Avalonia <c>Screens.Primary.WorkingArea</c>（同为物理像素）；
///   · DispatcherQueueTimer → Avalonia <see cref="DispatcherTimer"/>；
///   · ElementCompositionPreview 的透明度动画 → 普通 Opacity + 按帧计时器（见 SetPanelOpacity /
///     FadePanelToOpaque 的移植注释），偏移动画（PlaySlideIn）改用 RenderTransform 平移；
///   · 原生 Flyout → Avalonia <see cref="Flyout"/>（ShouldConstrainToRootBounds 在 Avalonia 没有
///     对应属性，Flyout 默认就不裁在宿主窗口里）；
///   · UISettings 取系统强调色 → Avalonia PlatformSettings.PlatformColorValues（取不到给兜底色）。
/// </summary>
public sealed partial class ToolSidebarWindow : Window
{
    private const int CollapsedThicknessDip = 20;   // 收起时那条的厚度（竖条=宽，横条=高）—— grip 抓手样式的原版值。
                                                    // ⚠️ 2026-10-12 误把"细一点的线"改到了这里，grip 被压成 8dip 扁条（2026-10-13 用户反馈修复）；
                                                    //    细线样式有自己的 LineThicknessDip，两者别混。
    private const int CollapsedLengthDip = 110;     // 收起时的长度（竖条=高）
    // Win32 ShowWindow 的 nCmdShow 值（与 FlyoutChrome / ToolPaletteWindow 同一套私有常量；
    // Platform.NativeMethods 只收 int 参数，不在业务代码里另写 DllImport）
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;
    private const int PanelThicknessDip = 92;       // 展开后的厚度（竖着放时=宽）
    private const int PanelLengthDip = 450;         // 展开后的长度（竖着放时=高）
    private const int PanelThicknessFlatDip = 112;  // 上/下边时：两行（工具一行、按钮一行）的高度
    private const int PanelLengthFlatDip = 470;     // 上/下边时：展开面板长度的**下限**（真实宽度按内容算，见 PlannedSize）

    /// <summary>
    /// 收起动画的第二段：面板滑出屏幕之后，**抓手从屏幕外滑回贴边位**要花的毫秒（2026-10-01）。
    /// 第一段是 <see cref="SlideOutToEdge"/> 的 240ms（整个面板推出屏幕）。两段加起来 ≈ 430ms。
    /// 抓手的行程很短（只有自身厚度 20dip + 2px ≈ 27px），所以这段比第一段快一些才跟得上。
    /// ⚠️ 移植说明：上游改用渲染回调驱动；本仓库沿用 16ms <see cref="DispatcherTimer"/>（见 TweenWindow），
    ///    时长这个"行为参数"照搬。
    /// </summary>
    private const double CollapseSlideMs = 190;

    /// <summary>
    /// 底部按钮那一排在竖条里的高度增量 —— <b>五颗全显</b>时的经验值（2026-09-27 定的）。
    /// 注意它**不是**五颗的真实高度（那是 5×44 + 4×2 = 228）：面板高度基数
    /// <see cref="PanelLengthDip"/>（450）里本来就已经含了标题和一段留白，这 94 只是把五颗"补齐"。
    /// ⚠️ 2026-09-29 底排改成**逐颗开关**后，这里必须按**可见颗数**摊算（见 <see cref="FooterDipFor"/>），
    ///    别改写成 <c>n × 44</c> 这种"真实高度" —— 会跟 450 的基数重复计算，面板凭空长一截。
    /// </summary>
    private const int FooterBaseDip = 94;

    /// <summary>底排可见 <paramref name="visible"/> 颗时，竖条高度里的增量（0 颗 = 完全不留）。</summary>
    private static int FooterDipFor(int visible) =>
        visible <= 0 ? 0 : (int)Math.Round(FooterBaseDip * visible / 5.0);

    /// <summary>
    /// 上/下边（横条）下**只有工具那一行**时的高度（= 底排一颗都不显示）。
    /// 两行版是 <see cref="PanelThicknessFlatDip"/>（112）；底排一颗都没有时只剩一行，
    /// 面板上下的 padding（5+6）+ 外框（2）+ 工具按钮的行高（图标 20 + 间距 3 + 文字 11 ≈ 54）
    /// ≈ 67，给几像素余量取 72。不跟着缩的话底下会空出一大块；反过来给小了会把按钮裁掉。
    /// </summary>
    private const int PanelThicknessFlatBareDip = 72;

    // 上/下边（横条）时底排按钮的排版参数。
    // ⚠️ 横条宽度必须容得下**底排这一整排**（见 PlannedSize），不然最后一颗会被面板裁掉 ——
    //    2026-09-27 用户截图就是这个：「打开应用」只露出半个「打」字。
    private const int FooterFlatButtonWidthDip = 104;   // 底排每个按钮的最小宽度
    private const int FooterFlatSpacingDip = 8;         // 底排按钮之间的间距
    private const int FlatRowPadDip = 24;               // 横条里每一行的左右内边距 + 余量

    // ── 展开样式的第二套：图标网格（grid）───────────────────────
    // 尺寸全部按 dip 写死，算式在 GridPanelDip() 里，改这里就整块跟着变。
    private const int GridCellWDip = 60;                // 每颗格子的宽
    private const int GridCellHDip = 60;                // 每颗格子的高（图标 + 短名）
    private const int GridGapDip = 6;                   // 格子之间的缝
    private const int GridPadDip = 8;                   // 面板四周内边距（= GridPanel 的 Margin）
    private const int GridHeaderDip = 26;               // 顶条（标题 + 四颗小图标键）的高度
    private const int GridSectionGapDip = 6;            // 顶条 / 网格 / 分页点 / 快捷设置行 之间的间距
    private const int GridDotsDip = 14;                 // 分页点那一行的高度（圆点 7 + 每边触摸靶 3）
    private const int GridQuickDip = 56;                // 快捷设置行的高度
    private const int GridColsSide = 4;                 // 贴左右边时一页几列
    private const int GridRowsSide = 2;                 // 贴左右边时一页几行
    private const int GridColsFlat = 8;                 // 贴上下边时一页几列（横条宽，一行铺开）
    private const int GridRowsFlat = 1;                 // 贴上下边时一页几行

    // ── 收起样式的第二套：细竖线（line）─────────────────────────
    // ⚠️ 窗口厚度不能给到太细：RoundedCorners 的圆角半径固定 8dip，
    //    窗口比 16 还窄的话 GDI 区域裁剪会把两端裁成尖的（椭圆比矩形宽）。
    //    现取 6dip（药丸形，看着就是"一条线"）。⚠️ 2026-10-12 用户要求更细：14 → 6，
    //    同日再去掉内芯——窗口本身就是那条线，悬停反馈走 LineHover 整条亮。
    private const int LineThicknessDip = 6;             // 竖线样式：窗口厚度（竖条=宽，横条=高）
    private const int LineLengthDip = 220;              // 竖线样式：窗口长度（竖条=高）

    /// <summary>收起条上"滑动展开"的方向阈值（dip）。小于它的一律当点击（见 Strip_PointerReleased）。</summary>
    private const double SwipeThresholdDip = 20;

    /// <summary>
    /// 按边缓存的窗口实例（键 = left | right | top | bottom）。
    ///
    /// ⚠️ 2026-09-27（Nick 需求）：竖直状态要能**左右同时**有侧边栏，所以从"单例"改成了"一条边一个实例"。
    /// 建过的实例留着复用 —— 切边只是显隐，不反复建窗。当前该显示哪几条见 <see cref="SyncInstances"/>。
    /// 设置里的 "both" 在这儿会被拆成 left + right 两个实例，每个实例的边存 <see cref="_edge"/>（不读设置）。
    /// 换边 / 换模式都只是改这个字典里"该显示哪几条" —— 同一条路（<see cref="SyncInstances"/>）。
    /// </summary>
    private static readonly Dictionary<string, ToolSidebarWindow> _pool = new();

    /// <summary>这条实例贴的边：left | right | top | bottom。**实例级** —— 设置里那个 "both" 是拆出来的两条，不是它的值。</summary>
    private readonly string _edge;

    private bool _expanded;

    private int _collapseEpoch;                            // 收起滑出/抓手淡入的批次号：展开、再次收起都能把它作废
    private bool _shownOnce;
    private bool _visible;

    /// <summary>圆角（Win11 走 DWM，Win7/10 走窗口区域裁剪）。挂在 Resized 上跟着尺寸重算。</summary>
    private RoundedCorners? _corners;

    private readonly DispatcherTimer _idle;

    /// <summary>展开滑动用的按帧计时器（滑完置空）。
    /// ⚠️ 移植说明：上游 dv1.1.0 起改用 <c>CompositionTarget.Rendering</c> 渲染回调 + 兜底 watchdog 驱动
    ///    （嫌 16ms 计时器精度低、忙时会合并 tick）；本仓库既有约定是 15~16ms <see cref="DispatcherTimer"/>
    ///    （见 SidebarLayoutPage 的 HookGhostFrame 注释），这里**保持本仓库写法不动**。</summary>
    private DispatcherTimer? _slideTimer;

    /// <summary>滑动的**落点存档**（目标位置）。滑动进行中 <see cref="CurrentRect"/> 返回它，
    /// 别让锚定方（音量浮窗）读到半路上的实时位置 —— 锚到半路的位置，等边条滑到位两个就叠上了（2026-10-01 修）。</summary>
    private PixelRect? _restRect;

    /// <summary>
    /// 滑动动画的"代次"。每次状态变化（收起/展开/拖动/隐藏）都 +1，让**还在跑的那一波动画立刻作废**。
    /// 没有它就会出现"展开到一半快速点收起 → 动画的后续帧又把窗口挪回去"的竞态（按钮跑到左上角就是这么来的）。
    /// </summary>
    private int _slideEpoch;

    /// <summary>抓手淡入用的按帧计时器（Composition 动画的替代，见类注释）。</summary>
    private DispatcherTimer? _fadeTimer;

    /// <summary>展开面板里的工具按钮（按「侧边布局」的模块清单动态生成，见 BuildToolButtons）。</summary>
    private readonly List<Button> _toolButtons = new();

    /// <summary>
    /// 「展开样式 = 图标网格」那一套的格子（模块 + 固定补的「主页 / 设置」两颗）。
    /// ⚠️ 只建一次、之后靠 <see cref="RenderGridPage"/> 搬进搬出 <c>ToolGrid</c> 翻页 ——
    ///    重建 Button 会让悬停动画、按住型（放大镜）的手势捕获全部断掉。
    /// </summary>
    private readonly List<Control> _gridCells = new();

    /// <summary>网格当前在第几页（0 起）。模块清单变少时会被 <see cref="RenderGridPage"/> 归零。</summary>
    private int _gridPage;

    /// <summary>收起条现在算不算"悬停中"（鼠标 / 手指搭在上面）。只有竖线样式会用它换色。</summary>
    private bool _hover;

    /// <summary>收起条上按下 / 最近一次移动的位置（dip，Root 坐标）—— 判断"点击还是向外滑"的两点。</summary>
    private Point _pressPos;
    private Point _lastPos;

    /// <summary>这一轮按下已经靠"向外滑"展开过了（抬起 / 丢失捕获就别再展一次）。</summary>
    private bool _swipedOpen;

    /// <summary>当前那个"要先问一句"的原生 Flyout（一次只有一个）。</summary>
    private Flyout? _confirmFlyout;

    /// <summary>「安全选项」菜单的 Flyout（一次只有一个）。</summary>
    private Flyout? _securityFlyout;

    /// <summary>「收起」键的那个图标（内容会按贴边重建，所以要留住当前这个实例才好换箭头方向）。</summary>
    private FontIcon? _foldIcon;

    /// <summary>「常驻」键的图标（同上，钉住/松开要换样子）。</summary>
    private FontIcon? _pinIcon;

    /// <summary>网格面板顶条上那两颗的图标（跟 <see cref="_foldIcon"/> / <see cref="_pinIcon"/> 同步换样子）。</summary>
    private FontIcon? _gridFoldIcon;
    private FontIcon? _gridPinIcon;

    /// <summary>
    /// 暂时压住"自动收起"（按住型动作、以及"还有话要问用户"的确认面板期间）。
    ///
    /// ⚠️ 这必须在**自己会过期**：早先写成普通 bool，一旦设了 true 而对面又没回来清（比如面板没弹出来、
    /// 用户压根没理），侧边栏就**永远不再自动收起**了 —— 真出过这个 bug。
    /// 现在本质是"压到某个时间点为止"，最长 `SuppressMaxSeconds`，到点自己恢复。
    /// </summary>
    public static bool SuppressAutoCollapse
    {
        get => DateTime.Now < _suppressUntil;
        set => _suppressUntil = value ? DateTime.Now.AddSeconds(SuppressMaxSeconds) : DateTime.MinValue;
    }

    private static DateTime _suppressUntil = DateTime.MinValue;

    /// <summary>压住自动收起的最长时间（秒）。确认面板活 10 秒，这里给够余量。</summary>
    private const int SuppressMaxSeconds = 15;

    /// <summary>
    /// 侧边栏当前在屏幕上的矩形（给"挨着它弹浮窗 / 弹提示"用）；还没建/拿不到就返回 null。
    ///
    /// ⚠️ 2026-10-02 改：**优先"刚被点出浮窗的那一条"**（见 <see cref="AnchorInstance"/>），
    ///    不再是"一律优先右边那条"。左右两边同时显示时要贴着被点的那条弹，否则浮窗会跑到对面去。
    /// </summary>
    public static PixelRect? CurrentRect
    {
        get
        {
            var w = AnchorInstance();
            if (w is null) return null;
            try
            {
                // 滑动进行中：返回落点存档（动画的目标位置）。实时位置还在半路上，
                // 锚定方拿到它会把自己的落座点算歪，等边条滑到位就叠上了。
                if (w._slideTimer is not null && w._restRect is { } rest) return rest;

                var pos = w.WinPos();
                var size = w.WinSizePx();
                return new PixelRect(pos.X, pos.Y, size.Width, size.Height);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 挑一条"可见的"实例当锚点。
    ///
    /// ⚠️ 2026-10-02 改：**优先"刚被点出浮窗的那一条"**。
    ///    以前这里只认右边那条（因为音量浮窗的边被写死成 right），于是用户把侧边栏放到左边、
    ///    或者选了「左右两边」去点左边那条的音量，浮窗会跑到屏幕右侧去 —— 就是他报的
    ///    「在左侧打开音量调节会在右侧显示」。现在哪条被点就跟哪条，上下边（自由模式）也一起对上了。
    /// </summary>
    private static ToolSidebarWindow? AnchorInstance()
    {
        if (_flyoutAnchor is { _visible: true }) return _flyoutAnchor;
        if (_pool.TryGetValue("right", out var r) && r._visible) return r;
        foreach (var w in _pool.Values) if (w._visible) return w;
        return null;
    }

    /// <summary>最近一次点出浮窗的那条边条（音量 / 亮度模块所在的那一条）。</summary>
    private static ToolSidebarWindow? _flyoutAnchor;

    /// <summary>把某条边条记为浮窗锚点 —— 在"点音量 / 点亮度"的那一刻调用。</summary>
    private static void SetFlyoutAnchor(ToolSidebarWindow window) => _flyoutAnchor = window;

    /// <summary>
    /// 浮窗该贴在屏幕哪条边（左/右/上/下）—— 跟着**被点击的那条边条**走。
    /// 一条边条都没有时返回 null，由调用方退回"读设置"。
    /// </summary>
    public static string? AnchorEdge => AnchorInstance()?._edge;

    /// <summary>
    /// 侧边栏**当前实际**贴的那条边（音量浮窗按它决定往哪边排）。
    ///
    /// ⚠️ 绝不能用 <c>App.Settings.Current.SidebarEdge</c> 代替：那条是**停靠模式**的设置，
    ///    **自由模式**下它可能还是老值（`SidebarFreeEdge` 才是真身），用户还能把边条拖到任意一边。
    ///    读错方向的后果（2026-10-01 踩到）：浮窗被摆到边条的**另一侧**（等于屏幕外）→
    ///    又被 ClampToWork 夹回屏幕边缘 → 正好压在边条（乃至合成器）身上，看着就是"三个窗叠一起"。
    /// ⚠️ 移植说明：上游 2026-10-01 新增；逻辑照搬（本仓库自由模式的真身在 SidebarFreeEdge / 实例 _edge）。
    /// </summary>
    public static string CurrentEdge
    {
        get
        {
            try
            {
                var inst = AnchorInstance();
                if (inst is not null) return inst._edge;

                // 没有可见实例：按设置推一个（音量浮窗一直挂右边，双双模式也取右）
                var want = DesiredEdges();
                return want.Contains("right") ? "right" : want[0];
            }
            catch
            {
                return "right";
            }
        }
    }

    // ⚠️⚠️ 2026-10-04（用户第 17 轮，第三次反馈同一条）：
    //    收起条的**拖拽整条撤除** —— 用户原话「不要拖动了，点击就展开」。
    //
    //    为什么"拖拽"和"点击展开"天生冲突（这才是三次都没修好的真原因）：
    //      区分"这是点击还是拖动"只能靠**位移阈值**（原来 8px）。触摸屏上手指按下必然带
    //      几像素抖动，稍一动就超过阈值 → 判成拖动 → 松手走 DockToNearestEdge()（吸附/换边），
    //      **永远走不到 Expand()**。用户看到的就是"点了没反应 / 展不开"；
    //      鼠标场景则表现为"想点一下，偏偏被拖到别处、还换了边"。
    //    ⚠️ 第 16 轮把这条误判成了**音量浮窗**的拖动（GripDragFrame，已删）——
    //      用户说的"悬浮窗"其实是本类（侧边栏收起来那条 20dip 的细把手，看着就是个悬浮条），
    //      所以那一轮改完用户还是说"没改啊"。
    //
    //    现在只剩：按下 → 抬起 = 展开。要换贴哪条边请到设置里选（侧边栏 → 贴边 / 自由模式）。
    //    原先那一大堆拖动专用字段（_dragging / _dragOrigin / _useSnapToFinger / _winTargetX …）
    //    随拖拽一起删掉了。
    //
    //    ⚠️ 2026-10-11 加回**一样**东西：朝屏幕外"滑动展开"（贴左边向右滑、贴右边向左滑，上下边同理）。
    //       它跟拖拽不是一回事，那条红线一点没松：
    //         · 滑动只是**多开一条路**，抬手时位移不够阈值的**一律还按点击展开**；
    //         · 唯一被挡下的是"明确朝屏幕里滑够 20dip"——手指抖动到不了这个量级。
    //       判据全在 Strip_PointerReleased / Strip_PointerMoved 里，改动前先把这段读完。
    private bool _pressed;

    private ToolSidebarWindow(string edge)
    {
        _edge = edge;
        InitializeComponent();

        // 展开后没人动 → 自己收回去（触屏没地方"点空白处收起"）
        _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idle.Tick += (_, _) => { if (_expanded && !App.Settings.Current.SidebarPinned && !SuppressAutoCollapse) Collapse(); };

        Configure();

        Core.AppLog.Info("exit", $"侧边栏实例已建 edge={_edge}");
        Closed += (_, _) => Core.AppLog.Info("exit", $"侧边栏 Closed edge={_edge}");
    }

    // ── 对外入口 ─────────────────────────────────────────────

    /// <summary>显示侧边栏（托盘菜单等入口调它；顺手把设置里的开关打开）。</summary>
    public static void ShowSidebar()
    {
        if (!App.Settings.Current.SidebarEnabled)
        {
            App.Settings.Current.SidebarEnabled = true;
            App.Settings.Save();
        }
        foreach (var w in SyncInstances()) w.Present();
    }

    public static void HideSidebar()
    {
        foreach (var w in _pool.Values) w.HideSelf();
    }

    /// <summary>
    /// 退出应用时把实例池里的窗口**真正关掉**（平时只是显隐，从不销毁）。
    /// ⛔ 为什么必须有：desktop.Shutdown() 会漏窗口 —— 2026-10-04 实测，
    ///    托盘「退出」后侧边栏常常是唯一活下来的那个窗口，把消息循环撑住、进程退不掉。
    /// </summary>
    public static void CloseForExit()
    {
        foreach (var w in _pool.Values.ToList())
        {
            try { w.Close(); } catch { }
        }
        _pool.Clear();
        Core.AppLog.Info("exit", "侧边栏实例已请求 Close");
    }

    /// <summary>
    /// 按设置把实例集合对齐到"该有哪几条边"：该显示的建出来/显示，不该显示的藏起来。
    /// 返回**本次该显示的那些实例**（顺序：左 → 右；单条就是一个）。
    /// </summary>
    private static List<ToolSidebarWindow> SyncInstances()
    {
        var want = DesiredEdges();

        foreach (var kv in _pool)
            if (!want.Contains(kv.Key)) kv.Value.HideSelf();

        var list = new List<ToolSidebarWindow>();
        foreach (var e in want)
        {
            if (!_pool.TryGetValue(e, out var w))
            {
                w = new ToolSidebarWindow(e);
                _pool[e] = w;
            }
            list.Add(w);
        }
        return list;
    }

    /// <summary>
    /// 设置说该有哪几条边。
    ///   · 贴靠模式（<c>dock</c>）：**只有左右两条边** —— <c>both</c> 拆成 left + right。
    ///   · 自由模式（<c>free</c>）：用户选的那**一条边**，左/右/上/下都行（<see cref="FreeEdgeSetting"/>），
    ///     贴上/下边时是横条（<see cref="IsFlat"/>）。自由模式**不提供「左右两边」**。
    /// </summary>
    private static List<string> DesiredEdges()
    {
        if (IsFreeMode) return new List<string> { FreeEdgeSetting };

        return App.Settings.Current.SidebarEdge switch
        {
            "left" => new List<string> { "left" },
            "both" => new List<string> { "left", "right" },
            // ⚠️ 贴靠模式只认左右。老设置里如果留着 top/bottom（那时贴靠也能贴上下边），这里一律归到右边 ——
            //    想贴上下边得切到自由模式（Nick 2026-09-28 定：贴靠 = 左右模式）。
            _ => new List<string> { "right" },
        };
    }

    /// <summary>自由模式贴的那条边（设置值认不出就退回 right）。</summary>
    private static string FreeEdgeSetting => App.Settings.Current.SidebarFreeEdge switch
    {
        "left" or "top" or "bottom" => App.Settings.Current.SidebarFreeEdge,
        _ => "right",
    };

    /// <summary>
    /// 音量浮窗（主音量 + 合成器）**全部收干净了**叫一声：边条这时候也该跟着收回去。
    ///
    /// 为什么必须有它：点「音量」时我们压住了自动收起（不然浮窗一抢焦点边条就缩），
    /// 那份压制一旦放开，边条自己的"失焦收起"**早就在被压制时错过了**，
    /// 结果就是"浮窗收了两键也缩了、边条却赖着不动"。所以在这儿显式叫它收。
    /// 钉了常驻（SidebarPinned）的不收 —— 用户明确要它留着。
    /// </summary>
    public static void CollapseAfterVolumeFlyoutsClosed()
    {
        foreach (var w in _pool.Values)
        {
            try
            {
                if (!w._visible || !w._expanded) continue;
                if (App.Settings.Current.SidebarPinned) continue;
                w.Collapse();
            }
            catch { }
        }
    }

    /// <summary>
    /// 「截屏」专用的收起：把边条收成那条细把手（**不是隐藏**），免得它被照进截图里。
    /// 边条展开着的时候才需要收；已经收着就啥也不做。
    /// </summary>
    public static void CollapseForCapture()
    {
        foreach (var w in _pool.Values)
        {
            try
            {
                if (!w._expanded) continue;
                w.Collapse(animate: false);               // 截图前必须当帧收干净，不然把淡出中的边条也照进去
            }
            catch { }
        }
    }

    public static bool IsSidebarVisible
    {
        get
        {
            foreach (var w in _pool.Values) if (w._visible) return true;
            return false;
        }
    }

    /// <summary>设置里的开关/边选项变了：开就显示、关就藏起来；边变了重新贴过去。</summary>
    public static void ApplySetting()
    {
        if (!App.Settings.Current.SidebarEnabled) { HideSidebar(); return; }
        foreach (var w in SyncInstances())
        {
            w.Present();
            w.SnapToSetting();
        }
    }

    private void SnapToSetting()
    {
        ApplyEdgeLayout();
        ApplySize();
        MoveToEdge();
    }

    // ── 窗口本身 ─────────────────────────────────────────────

    /// <summary>这条实例贴的边：left | right | top | bottom。⚠️ 实例级 —— 别改回"读设置"，那样两条实例会贴到同一条边上去。</summary>
    private string Edge => _edge;

    /// <summary>横条（贴上/下边）还是竖条（贴左/右边）。</summary>
    private bool IsFlat => _edge is "top" or "bottom";

    /// <summary>
    /// 「展开样式」选的是图标网格（第二套）。⚠️ 认不出的值一律当 classic（老存档/手改坏的设置不许崩）。
    /// </summary>
    private static bool UseGridStyle => App.Settings.Current.SidebarPanelStyle == "grid";

    /// <summary>「收起样式」选的是细竖线（第二套）。⚠️ 认不出的值一律当 grip。</summary>
    private static bool UseLineCollapsed => App.Settings.Current.SidebarCollapsedStyle == "line";

    // ── 网格面板的排版参数（都由上面那几个常量算出来）──────────

    /// <summary>一页几列：贴左右边 4 列、贴上下边 8 列（横条宽，一行铺开才不浪费）。</summary>
    private int GridCols => IsFlat ? GridColsFlat : GridColsSide;

    /// <summary>一页几行。</summary>
    private int GridRows => IsFlat ? GridRowsFlat : GridRowsSide;

    /// <summary>一共几页（向上取整）。一页放得下就是 1 —— 分页点这时**不显示**。</summary>
    private int GridPages
    {
        get
        {
            var per = GridCols * GridRows;
            if (per <= 0) return 1;
            var n = Math.Max(1, _gridCells.Count);
            return (n + per - 1) / per;
        }
    }

    /// <summary>
    /// 网格面板该多大（dip）。
    /// ⚠️ 宽度按"一页能铺开的列数"算死，**不随模块数变** —— 模块多了只加页，面板不会越撑越宽
    ///    （参考图里那个面板也是固定宽度 + 分页点）。高度只在"放得下两页"时多 16 给分页点。
    /// </summary>
    private (double W, double H) GridPanelDip()
    {
        var cols = GridCols;
        var rows = GridRows;

        var w = cols * GridCellWDip + (cols - 1) * GridGapDip + GridPadDip * 2;
        var h = GridPadDip * 2
                + GridHeaderDip + GridSectionGapDip
                + rows * GridCellHDip + (rows - 1) * GridGapDip + GridSectionGapDip
                + (GridPages > 1 ? GridDotsDip + GridSectionGapDip : 0)
                + GridQuickDip;

        return (w, h);
    }

    // ── 收起态的 dip 尺寸（CollapsedSize 的 dip 版；圆角按它算）──

    /// <summary>收起态窗口宽度（dip）。</summary>
    private double CollapsedDipW => IsFlat
        ? (UseLineCollapsed ? LineLengthDip : CollapsedLengthDip)
        : (UseLineCollapsed ? LineThicknessDip : CollapsedThicknessDip);

    /// <summary>收起态窗口高度（dip）。</summary>
    private double CollapsedDipH => IsFlat
        ? (UseLineCollapsed ? LineThicknessDip : CollapsedThicknessDip)
        : (UseLineCollapsed ? LineLengthDip : CollapsedLengthDip);

    /// <summary>设置里选的是自由模式（能吸四条边）。⚠️ 认不出的值一律当贴靠模式。</summary>
    private static bool IsFreeMode => App.Settings.Current.SidebarMode == "free";

    /// <summary>左右两条同时显示（贴靠模式里选的 "both"）。拖动时靠它决定"锁竖直、不换边"以及要不要带对面一起动。</summary>
    private static bool IsDual => !IsFreeMode && App.Settings.Current.SidebarEdge == "both";

    /// <summary>对面那条实例（双边模式下用来做位置联动）；自由模式、单边模式都是 null。</summary>
    private ToolSidebarWindow? Partner()
    {
        if (!IsDual) return null;
        var other = _edge == "left" ? "right" : "left";
        return _pool.TryGetValue(other, out var w) ? w : null;
    }

    private void Configure()
    {
        try
        {
            Title = "工具侧边栏";
            ShowInTaskbar = false;          // 不进任务栏、不进 Alt+Tab（SystemDecorations=None 已去掉标题栏/边框）
            Topmost = true;                 // 全屏播放时也要显示在上面
            CanResize = false;

            // 退出追踪：上游原版挂在 AppWindow.Closing 上；本仓库无 AppWindow，挂 Avalonia 窗口的 Closing。
            Closing += (_, _) => Core.AppLog.Info("exit", $"侧边栏 Closing edge={_edge}");

            // 亚克力底（模糊背后的画面）；机器不支持的话会自动退回纯色，不会崩。
            // ⚠️ 材质要等窗口真显示出来再上（Present 里走 Backdrop.Apply），构造期拿不到 HWND。
            // ⚠️ 2026-10-06（用户「首次启动侧边栏白底白字」）：Apply 必须排在 ApplyPanelBrush
            //    **前面** —— 面板底色是按 ActualThemeVariant 取的，先取色后设主题的话，
            //    取到的是还没传播的默认浅色（白面板），而文字/图标资源却按深色渲染 → 白底白字。
            ThemeCompat.Apply(Root);                        // 跟「设置」里的深浅色走（不只是跟系统走）
            ApplyPanelBrush();
            BuildToolButtons();                             // 按「侧边布局」的模块清单生成工具按钮
            BuildGridButtons();                             // 同一份清单，给「展开样式 = 图标网格」那套用
            SyncStyleViews();                               // 两套展开样式 / 两套收起样式先摆好

            // 触屏滑动翻页（2026-10-12 用户反馈）：按在格子上也能滑出去 ——
            // ⚠️ 必须 handledEventsToo=true：格子的 PointerReleased 会被 Button 标 handled。
            ToolGrid.AddHandler(InputElement.PointerPressedEvent,
                new EventHandler<PointerPressedEventArgs>(ToolGrid_PointerPressed),
                RoutingStrategies.Bubble, true);
            ToolGrid.AddHandler(InputElement.PointerReleasedEvent,
                new EventHandler<PointerReleasedEventArgs>(ToolGrid_PointerReleased),
                RoutingStrategies.Bubble, true);
            Root.PointerMoved += (_, _) => Touch();
            Root.PointerPressed += (_, _) => Touch();

            Deactivated += (_, _) =>
            {
                // 常驻时：失焦也不收（鼠标点去别处、切到别的窗口都保持展开）
                if (_expanded && !_pressed && !App.Settings.Current.SidebarPinned && !SuppressAutoCollapse)
                    Collapse();
            };
            Root.ActualThemeVariantChanged += (_, _) =>
            {
                ApplyPanelBrush();
                UpdatePinVisual();                         // 常驻块的图标颜色也跟主题（钉住=主题色，松开=默认前景）
                // 收起条的悬停色、网格分页点的圆点色都是**按当时的深浅色算出来写死**的，
                // 换主题不会自己跟着变 —— 这里重刷一遍（分页点只在真的显示着网格面板时才重建）。
                _hover = false;
                UpdateCollapsedLook();
                if (UseGridStyle && GridPanel.IsVisible) RenderGridPage();
                // 窗口那圈边的颜色也是跟着深浅色走的，换主题得重画一次
                try
                {
                    WindowChrome.RemoveBorder(Backdrop.TryGetHwnd(this), rounded: true,
                                              dark: Root.ActualThemeVariant == ThemeVariant.Dark);
                }
                catch { }
            };

            ApplyEdgeLayout();
        }
        catch (Exception ex)
        {
            Log("初始化失败: " + ex.Message);
        }
    }

    private void Present()
    {
        try
        {
            ApplyEdgeLayout();
            ApplySize();

            var hwnd = Backdrop.TryGetHwnd(this);

            if (!_shownOnce)
            {
                _shownOnce = true;
                // 显示前先把窗口底色定成成品色 —— 否则 Show() 那一帧是"不透明的默认白"，
                // 每次弹出都白闪一下（用户 2026-10-02 反馈的"显示和隐藏时会闪一下白色"）。
                PrimeOpaqueBackdrop();
                Show();

                // ⚠️ 2026-10-06（用户「首次启动悬浮窗和展开侧边栏白色看不清字，切一下深浅色就好了」）：
                //    Configure 里那次主题/取色发生在窗口还没显示时 —— RequestedThemeVariant 刚设上，
                //    ActualThemeVariant 未必传播到位，面板可能按默认浅色画成白的，而文字/图标资源
                //    却按深色渲染（白底白字）。现在窗口已进视觉树，重设主题会真正触发
                //    ActualThemeVariantChanged → 整套配色（面板、钉住图标、窗口边）跟着重刷；
                //    再 Dispatcher 补一拍，兜住"变体传播要等一轮布局"的情况。
                ThemeCompat.Apply(Root);
                ApplyPanelBrush();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ThemeCompat.Apply(Root);
                    ApplyPanelBrush();
                    PrimeOpaqueBackdrop();
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                // 之前被「隐藏」或收起过 → 得显式再显示出来，否则窗口一直藏着
                PrimeOpaqueBackdrop();      // 每次重新显形都要，不然同样会白闪
                NativeMethods.ShowWindow(hwnd, SW_SHOW);
                NativeMethods.SetForegroundWindow(hwnd);
            }

            _visible = true;
            Collapse(animate: false);                      // 每次出现都从收起状态开始（不挡画面）

            Topmost = true;

            MoveToEdge();
            // ⚠️ 外观要等窗口真显示出来之后再收尾（构造期改窗口样式会让窗口显示不出来）
            WindowChrome.RemoveBorder(hwnd, rounded: true, dark: Root.ActualThemeVariant == ThemeVariant.Dark);
            // 圆角：RemoveBorder 里那次是"一次性"的，而侧边栏展开/收起时宽度会变 ——
            // 区域是按当时的尺寸算死的，不重算就会裁歪（实测过：窗口 40 宽、区域还停在 59 宽）。
            // Attach 挂在 Resized 上自动重算，正好治这个。
            // ⚠️ force=true（2026-10-14）：Win11 上这扇 WS_POPUP + 每像素 alpha 的浮窗 DWM 拒绝
            //    给圆角（CORNER_PREFERENCE=ROUND 也不认，实测 26200），区域裁剪是唯一出路。
            try { _corners ??= RoundedCorners.Attach(this, RoundedCorners.DefaultCornerRadiusDip, force: true); } catch { }
            _corners?.Refresh();
            // 亚克力底（Win7 → Aero 毛玻璃 → 纯色兜底），也是显示之后才能上
            Backdrop.Apply(this, null, "acrylic");
            // ⚠️ Win11：这扇 NOREDIRECTIONBITMAP 浮窗 DWM 不认 SetWindowRgn（2026-10-15 实测，
            //    日志"裁剪成功"但像素纹丝不动），圆角只能靠内容 alpha 抠 —— 底色必须全透，
            //    否则面板圆角与窗口矩形之间的月牙缝漏出窗口白底（用户实测的"圆角没裁干净"）。
            if (OsInfo.SupportsWindowRounding) Backdrop.EnablePerPixelCorners(this);
        }
        catch (Exception ex)
        {
            Log("显示失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        if (!_visible) return;
        StopSlide();
        _visible = false;

        // 音量浮窗（主音量 + 合成器）跟着一起收
        VolumeFlyoutGroup.CloseAll();

        try { NativeMethods.ShowWindow(Backdrop.TryGetHwnd(this), SW_HIDE); } catch { }
    }

    /// <summary>亚克力上面再压一层很淡的色：深色主题压深、浅色主题压白，保证字看得清。
    /// ⚠️ 色值跟 FlyoutChrome.ApplyMaterial 逐字一致（2026-10-03 一起提到 205 的白 —— 浅色模式下要真的是白色）。</summary>
    private void ApplyPanelBrush()
    {
        // ⚠️ 2026-10-03（用户第 12 轮）：改全不透明强制色，与 FlyoutChrome.ApplyMaterial 逐字一致 ——
        //    深色黑面板 #202020、浅色白面板 #FCFCFC；毛玻璃再灰也透不上来了。
        var dark = Root.ActualThemeVariant == ThemeVariant.Dark;
        Panel.Background = new SolidColorBrush(dark
            ? Color.FromRgb(0x20, 0x20, 0x20)
            : Color.FromRgb(0xFC, 0xFC, 0xFC));
    }

    /// <summary>
    /// 每次显形**之前**把窗口自己的底色定成成品色。
    ///
    /// <para>
    /// ── 为什么（用户 2026-10-02：「显示和隐藏时会闪一下白色」）────────────────
    /// <c>Show()</c> / <c>ShowWindow(SW_SHOW)</c> 只是让 Win32 窗口可见，
    /// 视效树的第一帧要等下一轮渲染 —— 中间那一帧窗口是**不透明但空白**的，
    /// 系统给的默认底色是**白的**。侧边栏弹出/收起非常频繁，所以这个白闪特别显眼。
    /// 显形前把底色设成成品色，闪的就变成"成品色 → 成品色"，看不出来了。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 只在**没有模糊支持**的环境做（<see cref="OsInfo.SupportsAcrylicBlur"/> 为假）：
    ///    能透视时窗口本来就能透出桌面、不会白闪，这时设成不透明反而会盖掉亚克力。
    /// ⚠️ 色值跟 <see cref="Views.FlyoutChrome"/> 里那套**逐字一致**，改就一起改。
    /// </para>
    /// </summary>
    private void PrimeOpaqueBackdrop()
    {
        if (OsInfo.SupportsAcrylicBlur) return;

        try
        {
            var dark = Root.ActualThemeVariant == ThemeVariant.Dark;
            Background = new SolidColorBrush(dark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xF3, 0xF3, 0xF3));
        }
        catch
        {
            // 设不上就算了：顶多还是白闪一下，不影响功能
        }
    }

    // ── 展开 / 收起 / 隐藏 ───────────────────────────────────

    private void Expand()
    {
        StopSlide();                                      // 掐掉上一次没跑完的（防连点/竞态）
        _collapseEpoch++;                                 // 取消可能还在跑的"收起滑出/抓手淡入"
        _expanded = true;
        ResetPanelOpacity();
        CollapsedView.IsVisible = false;
        ExpandedView.IsVisible = true;
        ApplyScrollLimit();                               // 先把滚动范围算好；尺寸交给滑入动画一帧设到位
        ApplyCornerRadius();                              // 竖线样式下窗口马上要变宽，圆角得先回到 8

        // ⚠️ 这里**不要**再单独 ApplySize() + MoveToEdge()：
        //    那会让窗口先出现在"终点位置"并画出一帧展开态，紧接着又被滑入动画挪到起点 ——
        //    肉眼就是"闪一下，然后再滑"。滑入动画自己会用**一次** MoveAndResize
        //    把"起点位置 + 展开尺寸"同时设下去（2026-09-26 优化）。
        SlideInFromEdge();
        Touch();
    }

    private void Collapse(bool animate = true)
    {
        StopSlide();                                      // ⚠️ 必须停：不然展开动画的后续帧还会把窗口挪回去
        _idle.Stop();

        // 展开 → 收起给个过渡：整个窗口往贴的那条边**滑出去**（跟滑进来同一条路子，方向相反），滑完再真收。
        // animate=false 用在"要立刻消失"的场合（截图前、刚出现时），那种必须当帧就收干净。
        if (!_expanded || !ExpandedView.IsVisible || !animate)
        {
            FinishCollapse();
            return;
        }

        _expanded = false;                                // 先立旗：滑出期间自动收起那条路别再来一遍
        var epoch = ++_collapseEpoch;
        // 滑完才真收，而且要让抓手**淡入**：滑出终点在屏幕外，小条直接"啪"一下冒出来太生硬
        if (SlideOutToEdge(() => { if (epoch == _collapseEpoch) FinishCollapse(fadeIn: true); })) return;
        FinishCollapse();                                 // 没有窗口可滑（极少数）：当帧收干净，不做动画
    }


    /// <summary>
    /// 真收：换回抓手 + 落位。中途用户又展开了就别收（_expanded 已被置回 true）。
    /// </summary>
    /// <param name="fadeIn">
    /// true = 抓手淡入（**走滑动动画那条路用它**：面板刚滑出屏幕，小条淡入比"啪一下出现"自然）。
    /// false = 当帧就位（截图前、窗口刚出现时那种要立刻收干净的场合，不能有任何可见动画）。
    /// </param>
    private void FinishCollapse(bool fadeIn = false)
    {
        if (_expanded) return;

        // 先按住透明度，等落位之后再放出来 —— 顺序反了会先闪一帧不透明的小条
        if (fadeIn) SetPanelOpacity(0f);

        ExpandedView.IsVisible = false;
        CollapsedView.IsVisible = true;
        ApplyCornerRadius();                              // 竖线样式下窗口马上要缩到 14 宽，圆角得先降下来，不然两端被裁尖

        // 一次到位：收起尺寸 + 位置。
        // 滑出动画已经把**整个窗口**推出屏幕外了，所以这一步的"变身"（尺寸 92×450 → 20×110、
        // 内容换视图）用户在屏幕上看不到 —— 不会再出现"没滑出去就突然缩一下"（2026-09-26 修）。
        //
        // ⚠️ fadeIn 的落点是**屏幕外的抓手起点**，不是贴边位（2026-10-01 改）：
        //    面板滑出去之后，抓手再从屏幕外滑回贴边（见下面的 TweenWindow）——
        //    "大块滑走 + 小条滑回"一口气看完，比"大块滑走 + 小条原地淡入"连贯。
        //    起点在屏幕外，所以尺寸变身依旧藏得住。
        PixelPoint? slideFrom = null, slideTo = null;
        try
        {
            var size = CollapsedSize();
            var pos = EdgePosition(size.Width, size.Height);
            var startPos = fadeIn
                ? OutwardOffset(pos, (IsFlat ? size.Height : size.Width) + 2)   // 整个抓手推到屏幕外 + 2px 余量
                : pos;

            MoveResizeWin(startPos, size.Width, size.Height);

            if (fadeIn)
            {
                slideFrom = startPos;
                slideTo = pos;
                _restRect = new PixelRect(pos.X, pos.Y, size.Width, size.Height);  // 滑动期间 CurrentRect 返回它
            }
        }
        catch (Exception ex)
        {
            Log("收尾落位失败: " + ex.Message);
        }

        if (fadeIn)
        {
            FadePanelToOpaque(CollapseSlideMs);
            if (slideFrom is { } f && slideTo is { } t) TweenWindow(f, t, CollapseSlideMs);
        }
        else ResetPanelOpacity();

        ReassertCollapsed();
    }

    private void ResetPanelOpacity() => SetPanelOpacity(1f);

    /// <summary>
    /// 把整块面板的透明度按住（不走动画）。收起后要给抓手"淡入"，就先用它把面板压到 0。
    /// ⚠️ 移植说明：原版走 ElementCompositionPreview 直改合成器 Visual 的 Opacity；
    ///    Avalonia 没有这套 API，直接设 <c>Panel.Opacity</c>（这一处本来就不需要动画）。
    /// </summary>
    private void SetPanelOpacity(double value)
    {
        try
        {
            StopFade();
            Panel.Opacity = value;
        }
        catch { }
    }

    /// <summary>
    /// 面板从当前透明度淡到不透明（收起后让小条"浮现"而不是"闪现"）。
    /// ⚠️ 移植说明：原版是合成器 ScalarKeyFrameAnimation + 缓出贝塞尔；Avalonia 没有逐 Visual 的
    ///    合成动画，这里用 16ms 按帧计时器做线性淡入 —— 150ms 的透明度过渡，肉眼差别可以忽略。
    /// </summary>
    private void FadePanelToOpaque(double ms)
    {
        try
        {
            StopFade();
            var from = Panel.Opacity;
            if (ms <= 0 || from >= 1) { Panel.Opacity = 1; return; }

            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
                Panel.Opacity = from + (1 - from) * t;    // 缓出近似：直接线性，尾巴没原版柔
                if (t >= 1)
                {
                    Panel.Opacity = 1;
                    StopFade();
                }
            };
            _fadeTimer = timer;
            timer.Start();
        }
        catch (Exception ex)
        {
            Log("抓手淡入失败: " + ex.Message);
            ResetPanelOpacity();                            // 出问题就退回"直接可见"，别留个透明的边条
        }
    }

    private void StopFade()
    {
        _fadeTimer?.Stop();
        _fadeTimer = null;
    }

    /// <summary>
    /// 下一帧再确认一次"收起态"的尺寸和位置。
    /// 有些时候尺寸/位置要等系统下一帧才对得上（尤其是刚快速开关过），再兜一次就不会跑偏。
    /// 只在"仍然是收起态"时才兜 —— 用户这会儿要展开的话，不能跟展开打架。
    /// </summary>
    private void ReassertCollapsed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_expanded) return;

                // ⚠️ 抓手正在从屏幕外滑入时**别纠位**：这一帧它本来就还在路上，
                //    抢先挪到贴边位会把滑入动画打断成"闪一下就到了"（2026-10-01）。
                //    落位交给动画自己的最后一帧（TweenWindow 里那次 MoveWin）。
                if (_slideTimer is not null) return;

                // ⚠️ 已经落对了就**什么都别做**。多挪一次窗口就多一帧重绘 ——
                //    动画刚结束那一下最容易看出抖，这里不能无脑再摆一次（2026-09-26 优化）。
                var size = CollapsedSize();
                var pos = EdgePosition(size.Width, size.Height);
                var now = WinSizePx();
                var at = WinPos();
                if (now.Width == size.Width && now.Height == size.Height
                    && Math.Abs(at.X - pos.X) <= 2 && Math.Abs(at.Y - pos.Y) <= 2) return;

                Log("兜底落位：尺寸/位置没对上，纠一次");
                MoveResizeWin(pos, size.Width, size.Height);
            }
            catch (Exception ex)
            {
                Log("兜底落位失败: " + ex.Message);
            }
        });
    }

    private void Touch()
    {
        if (!_expanded || _pressed) return;
        if (App.Settings.Current.SidebarPinned) return;    // 常驻：不启动自动收起计时
        _idle.Stop();
        _idle.Start();
    }

    private void Collapse_Click(object? sender, RoutedEventArgs e) => Collapse();

    /// <summary>常驻开关：钉住 = 展开后不自动收起（手动「收起」还是能收）。</summary>
    private void Pin_Click(object? sender, RoutedEventArgs e)
    {
        var pinned = !App.Settings.Current.SidebarPinned;
        App.Settings.Current.SidebarPinned = pinned;
        App.Settings.Save();

        _idle.Stop();                       // 先把已经排队的自动收起取消掉
        UpdatePinVisual();
        Touch();                            // 松开时重新起计时；钉住时 Touch 自己会跳过
        Log(pinned ? "常驻：开" : "常驻：关");
    }

    /// <summary>「常驻」键的样子：钉住 = 实心钉 + 主题色；松开 = 空心钉。</summary>
    private void UpdatePinVisual()
    {
        try
        {
            var pinned = App.Settings.Current.SidebarPinned;
            if (_pinIcon is not null)
            {
                _pinIcon.Glyph = pinned ? "\uE840" : "\uE718";      // Pinned / Pin
                if (pinned)
                {
                    _pinIcon.Foreground = new SolidColorBrush(AccentColor());
                }
                else
                {
                    // ⚠️ 松开状态**清掉本地值、跟着主题走**（2026-10-01 修「亮色模式下钉子白得看不见」）：
                    //    以前这里写死成 SolidColorBrush（暗色给白、亮色给黑）—— 可换主题时只有面板底色会重刷
                    //    （ActualThemeVariantChanged 里只调了 ApplyPanelBrush），这个写死的颜色不会跟着变：
                    //    暗色下启动过再切亮色，就成了「白钉子压白底」，整颗图标消失。
                    //    清掉本地值后它回到 IconElement 默认前景（主题画刷），深浅色都自动对。
                    // ⚠️ 移植说明：FontIcon（FluentAvalonia）继承 IconElement，ForegroundProperty 走同一个。
                    _pinIcon.ClearValue(FontIcon.ForegroundProperty);
                }
            }

            // 网格面板顶条那颗「常驻」跟着一起换样子（两套展开样式共用同一个设置）
            if (_gridPinIcon is not null)
            {
                _gridPinIcon.Glyph = pinned ? "\uE840" : "\uE718";
                if (pinned) _gridPinIcon.Foreground = new SolidColorBrush(AccentColor());
                else _gridPinIcon.ClearValue(FontIcon.ForegroundProperty);   // 回到主题画刷，深浅色都对
            }

            if (PinButton is not null)
                ToolTip.SetTip(PinButton, pinned
                    ? "常驻中：展开后不会自动收起（再点一下松开）"
                    : "常驻：展开后不自动收起（手动点「收起」还是能收）");
        }
        catch (Exception ex)
        {
            Log("常驻外观更新失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 系统强调色。原版走 WinRT UISettings.GetColorValue(Accent)；
    /// Avalonia 对应 PlatformSettings.GetColorValues()（Win 上就是系统强调色），拿不到给经典蓝兜底。
    /// </summary>
    private static Color AccentColor()
    {
        try
        {
            var v = Application.Current?.PlatformSettings?.GetColorValues();
            if (v is not null) return v.AccentColor1;
        }
        catch { }
        return Color.FromArgb(255, 0, 120, 212);
    }

    /// <summary>
    /// 位置复原：沿边居中；两种模式都顺便把边退回默认的那条。
    /// ⚠️ 双边模式（both）下**不改边** —— 它没有"哪一条边"的概念，保持两条并一起摆正。
    /// </summary>
    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsDual)
        {
            if (IsFreeMode) App.Settings.Current.SidebarFreeEdge = "right";
            else App.Settings.Current.SidebarEdge = "right";
        }

        App.Settings.Current.SidebarPosRatio = -1;
        App.Settings.Save();

        // 边可能变了 → 得走 ApplySetting()（按新设置重建实例集合），不能只 SnapToSetting()
        ApplySetting();
        Partner()?.SnapToSetting();
        Touch();
    }

    /// <summary>隐藏：直接关掉（设置里也不显示了），想找回去去「内置工具」页或托盘图标菜单。</summary>
    private void Hide_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.Current.SidebarEnabled = false;
        App.Settings.Save();
        HideSidebar();                                    // 双边时两条一起藏
    }

    /// <summary>
    /// 打开应用：把**主窗口**叫出来（可能收在托盘里、也可能只是最小化了）。
    /// 侧边栏是独立小窗，经常是屏幕上唯一露着的东西 —— 给老师留一条回主界面的近路。
    /// </summary>
    private void OpenApp_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            App.MainWindow?.ShowFromTray();
        }
        catch (Exception ex)
        {
            Log("打开主界面失败: " + ex.Message);
        }
    }

    /// <summary>设置里的模块清单变了（侧边布局页改完调它）：重建按钮 + 重新量尺寸贴边（左右两条都要）。</summary>
    public static void ApplyModules()
    {
        if (!App.Settings.Current.SidebarEnabled) return;
        foreach (var w in _pool.Values) w.RebuildModules();
    }

    /// <summary>
    /// 底排那几颗按钮（收起 / 常驻 / 位置复原 / 隐藏 / 打开应用）的显示开关变了
    /// （「侧边布局」页底部那五个开关，逐颗）。
    /// 跟换模块清单走同一条路：重排 + 重新量尺寸 —— 面板高矮、横条宽窄都跟着**可见颗数**变。
    /// </summary>
    public static void ApplyFooterSetting() => ApplyModules();

    private void RebuildModules()
    {
        try
        {
            BuildToolButtons();
            BuildGridButtons();
            SyncStyleViews();
            ApplyEdgeLayout();
            ApplySize();
            MoveToEdge();
        }
        catch (Exception ex)
        {
            Log("重建模块失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 「侧边布局」页改了**展开样式 / 收起样式** → 所有实例重排（跟换模块清单同一条路）。
    ///
    /// <para>
    /// ⚠️ 收起样式换的是**窗口尺寸**（grip 20×110 → line 14×220），所以不能只换视图，
    ///    必须走完整的 ApplySize + MoveToEdge，否则收起条会按老尺寸贴边、看着就歪。
    /// </para>
    /// </summary>
    public static void ApplyStyle()
    {
        if (!App.Settings.Current.SidebarEnabled) return;
        foreach (var w in _pool.Values) w.RebuildModules();
    }

    /// <summary>
    /// 按设置把四块视图摆好：两套展开样式（经典竖排 / 图标网格）二选一，
    /// 两套收起样式（抓手 / 细竖线）二选一。
    ///
    /// <para>
    /// ⚠️ 切换是**纯 IsVisible**，不动事件接线：CollapsedView 无论哪种收起样式都是同一层、
    ///    挂同一套按下/抬起/滑动处理器；GridPanel 与 ExpandedStack 同在一个 Grid 里，
    ///    可见的那块盖住另一块。
    /// </para>
    /// </summary>
    private void SyncStyleViews()
    {
        try
        {
            var grid = UseGridStyle;
            var line = UseLineCollapsed;

            GridPanel.IsVisible = grid;
            ExpandedStack.IsVisible = !grid;

            LineView.IsVisible = line;
            GripStack.IsVisible = !line;

            if (grid) RenderGridPage();
            ApplyCornerRadius();
        }
        catch (Exception ex)
        {
            Log("切换样式失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 窗口圆角不能超过**半宽**：收起成细竖线后窗口只有 14dip 宽，
    /// 还按 XAML 里写死的 8 裁，GDI 区域就会把两端裁成尖的（椭圆比矩形宽）。
    /// ⚠️ 展开态一律用默认 8，别跟着网格面板的尺寸缩。
    /// </summary>
    private void ApplyCornerRadius()
    {
        try
        {
            double minDip;
            if (_expanded)
            {
                minDip = UseGridStyle
                    ? Math.Min(GridPanelDip().W, GridPanelDip().H)
                    : Math.Min(PanelThicknessDip, PanelLengthDip);
            }
            else
            {
                minDip = Math.Min(CollapsedDipW, CollapsedDipH);
            }

            var r = Math.Min(RoundedCorners.DefaultCornerRadiusDip, Math.Max(1, minDip / 2.0));
            Panel.CornerRadius = new CornerRadius(r);
        }
        catch (Exception ex)
        {
            Log("算圆角失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 按 AppSettings.SidebarModuleIds（= 「侧边布局」页里勾选+排序的结果）生成工具按钮。
    /// 清单里认不出来的 id（老设置里存了后来删掉的模块）直接跳过，不会崩。
    /// </summary>
    private void BuildToolButtons()
    {
        ToolStack.Children.Clear();
        _toolButtons.Clear();

        foreach (var id in App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>())
        {
            var m = SidebarModules.Find(id);
            if (m is null) continue;

            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(new FontIcon
            {
                Glyph = m.Glyph,
                FontSize = 20,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = m.ShortName,
                FontSize = 11,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            });

            var btn = new Button
            {
                Tag = m.Id,
                MinWidth = 0,
                MinHeight = 52,
                Padding = new Thickness(0, 8, 0, 8),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Content = content
            };
            // ⚠️ 2026-10-15：**故意不给模块按钮挂 ToolTip**（同第 14 轮标题栏按钮的取舍）。
            //    展开面板上本来就看得到模块名，tooltip 纯冗余；而它是个 Popup，
            //    悬停一秒弹出后，按下去那一下会被 Popup 的轻.dismiss 吃掉 ——
            //    实测（Win11 自动化点击）就是用户报的「秒表按钮点第一次没反应需要点第二次」。
            //    模块按钮是高频点击件，宁可没有提示也不能丢点击。

            if (m.Kind == SidebarModuleKinds.Action && TeachingActions.IsHold(m.Id))
            {
                // 按住型（放大镜）：按下开始 → 松手结束。按住期间别让侧边栏自动收起，
                // 否则按钮被一起收走、指针捕获也会断，放大镜就"按两下才亮"了。
                var holdId = m.Id;
                // ⚠️ handledEventsToo: true —— Button 内部会把 PointerPressed 标成 handled
                //（WinUI 的 UIElement 路由事件 → Avalonia 挂在控件实例上：control.AddHandler(event, handler, route, handledEventsToo)）
                btn.AddHandler(InputElement.PointerPressedEvent,
                    new EventHandler<PointerPressedEventArgs>((_, _) =>
                    {
                        SuppressAutoCollapse = true;
                        TeachingActions.Begin(holdId);
                    }), RoutingStrategies.Bubble, true);
                btn.AddHandler(InputElement.PointerReleasedEvent,
                    new EventHandler<PointerReleasedEventArgs>((_, _) => EndHold(holdId)), RoutingStrategies.Bubble, true);
                btn.AddHandler(InputElement.PointerCaptureLostEvent,
                    new EventHandler<PointerCaptureLostEventArgs>((_, _) => EndHold(holdId)), RoutingStrategies.Bubble, true);
            }
            else
            {
                btn.Click += Tool_Click;
            }

            ToolStack.Children.Add(btn);
            _toolButtons.Add(btn);
        }

        // 一个模块都没选：面板里给一句说明，别让用户以为坏了
        if (_toolButtons.Count == 0)
        {
            ToolStack.Children.Add(new TextBlock
            {
                Text = "还没选模块\n去「侧边布局」挑几个",
                FontSize = 11,
                Opacity = 0.7,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(2, 6, 2, 6)
            });
        }
    }

    // ── 展开样式的第二套：图标网格 ──────────────────────────

    /// <summary>网格里固定补的两颗格子的 id（**不是模块 id**，只活在内存里，不进设置存档）。</summary>
    private const string GridHomeId = "__home";
    private const string GridSettingsId = "__settings";

    /// <summary>
    /// 「展开样式 = 图标网格」那一套的格子：**同一份模块清单**（用户在「侧边布局」勾的那些），
    /// 末尾固定补「主页」「设置」两颗 —— 跟参考图里的 主页 / 设置 对应（用户 2026-10-11 定）。
    ///
    /// <para>
    /// ⚠️ 这里只把格子建出来放进 <see cref="_gridCells"/>；摆到 <c>ToolGrid</c> 上、翻页都归
    ///    <see cref="RenderGridPage"/> 管。为什么不每页重建 Button：重建会把按住型模块（放大镜）
    ///    的指针捕获、以及按钮的悬停/回弹动画全部弄断。
    /// </para>
    /// </summary>
    private void BuildGridButtons()
    {
        _gridCells.Clear();
        _gridPage = 0;

        foreach (var id in App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>())
        {
            var m = SidebarModules.Find(id);
            if (m is null) continue;                       // 老设置里留下的已删模块：跳过，不崩
            _gridCells.Add(CreateGridCell(m.Id, m.Glyph, m.ShortName, m.Note ?? m.Name, m));
        }

        // 固定补两颗（放最后 —— 翻到最后一页总能找到）
        _gridCells.Add(CreateGridCell(GridHomeId, "\uE80F", "主页", "打开应用主界面（主窗口在托盘里或最小化时，点它叫出来）", null));
        _gridCells.Add(CreateGridCell(GridSettingsId, "\uE713", "设置", "打开应用设置页", null));
    }

    /// <summary>建一颗网格格子。<paramref name="m"/> 为 null = 固定补的那两颗（主页 / 设置）。</summary>
    private Control CreateGridCell(string id, string glyph, string shortName, string tip, SidebarModule? m)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 22,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = shortName,
            FontSize = 10.5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        });

        var btn = new Button
        {
            Tag = id,
            MinWidth = GridCellWDip,
            MinHeight = GridCellHDip,
            Padding = new Thickness(2, 6, 2, 6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Content = content
        };
        ToolTip.SetTip(btn, tip);

        if (m is not null && m.Kind == SidebarModuleKinds.Action && TeachingActions.IsHold(m.Id))
        {
            // 按住型（放大镜）：跟经典竖排那套一模一样 —— 按下开始、松手结束，
            // 按住期间压住自动收起，免得面板被收走把指针捕获也一起断掉。
            var holdId = m.Id;
            btn.AddHandler(InputElement.PointerPressedEvent,
                new EventHandler<PointerPressedEventArgs>((_, _) =>
                {
                    SuppressAutoCollapse = true;
                    TeachingActions.Begin(holdId);
                }), RoutingStrategies.Bubble, true);
            btn.AddHandler(InputElement.PointerReleasedEvent,
                new EventHandler<PointerReleasedEventArgs>((_, _) => EndHold(holdId)), RoutingStrategies.Bubble, true);
            btn.AddHandler(InputElement.PointerCaptureLostEvent,
                new EventHandler<PointerCaptureLostEventArgs>((_, _) => EndHold(holdId)), RoutingStrategies.Bubble, true);
        }
        else
        {
            btn.Click += GridCell_Click;
        }

        return btn;
    }

    /// <summary>网格里那两颗固定格子（主页 / 设置）的动作；其余格子转给经典那套 <see cref="Tool_Click"/>。</summary>
    private void GridCell_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        try
        {
            if (id == GridHomeId)
            {
                // 「主页」＝底排那颗「打开应用」的等价物，所以同样**不收起**边条
                App.MainWindow?.ShowFromTray();
                return;
            }

            if (id == GridSettingsId)
            {
                // 「设置」＝跳主窗口的某一页，跟模块里 kind=page 的一样：先把边条收了，别挡着主窗口
                Collapse();
                App.MainWindow?.ShowFromTray();
                App.MainWindow?.Shell.NavigateTo("settings");
                return;
            }

            Tool_Click(sender, e);                          // 模块：跟竖排共用同一套点击逻辑
        }
        catch (Exception ex)
        {
            Log("网格格子打开失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 把当前页的格子摆进 <c>ToolGrid</c>，并按页数显隐 / 重建分页点。
    ///
    /// <para>
    /// ⚠️ 列数会随"贴哪条边"变（左右 4 列 / 上下 8 列），所以每次都重设行列定义；
    ///    格子本身**不重建**，只是从上一个父容器里搬过来（<c>Children.Clear()</c> 会先摘干净）。
    /// </para>
    /// </summary>
    private void RenderGridPage()
    {
        try
        {
            var cols = GridCols;
            var rows = GridRows;
            var per = cols * rows;
            var pages = GridPages;
            if (_gridPage >= pages) _gridPage = 0;           // 模块变少了：这一页已经不存在

            ToolGrid.Children.Clear();
            ToolGrid.ColumnDefinitions.Clear();
            ToolGrid.RowDefinitions.Clear();
            for (var c = 0; c < cols; c++)
                ToolGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var r = 0; r < rows; r++)
                ToolGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 锁死网格区高度：外层 GridPanel 是竖排 StackPanel，ToolGrid 的期望高取自当前页
            // 格子数 —— 翻到格子少的一页 Star 行塌下去，底下的分页点 / QuickBar 会整体上移。
            // 按行数算死高度，翻页时快捷设置行位置纹丝不动。
            ToolGrid.Height = rows * GridCellHDip + (rows - 1) * GridGapDip;

            for (var i = 0; i < per; i++)
            {
                var idx = _gridPage * per + i;
                if (idx >= _gridCells.Count) break;
                var cell = _gridCells[idx];
                Grid.SetRow(cell, i / cols);
                Grid.SetColumn(cell, i % cols);
                ToolGrid.Children.Add(cell);
            }

            BuildPagerDots(pages);

            // 两侧换页小箭头：多页才露面（单页没有可翻的东西）
            var showArrows = pages > 1;
            GridPrevPage.IsVisible = showArrows;
            GridNextPage.IsVisible = showArrows;
        }
        catch (Exception ex)
        {
            Log("网格翻页失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 分页点。一页放得下就整行收掉（面板也跟着矮一截，见 <see cref="GridPanelDip"/>）。
    /// ⚠️ 圆点外面包一层 Button 当触摸靶（7dip 的点在触屏上根本点不中）。
    /// </summary>
    private void BuildPagerDots(int pages)
    {
        PagerDots.IsVisible = pages > 1;
        PagerDots.Children.Clear();
        if (pages <= 1) return;

        var dark = Root.ActualThemeVariant == ThemeVariant.Dark;
        var idle = new SolidColorBrush(dark ? Color.FromRgb(0x8A, 0x8A, 0x8A) : Color.FromRgb(0x60, 0x60, 0x60));
        var accent = new SolidColorBrush(AccentColor());

        for (var p = 0; p < pages; p++)
        {
            var me = p;
            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Opacity = p == _gridPage ? 1.0 : 0.35,
                Background = p == _gridPage ? accent : idle,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var hit = new Button
            {
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(5, 3),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = dot
            };
            // ⚠️ Avalonia 的 ToolTip 是**附加属性**，C# 里只能用 SetTip 写，不能塞进对象初始化器
            ToolTip.SetTip(hit, "第 " + (p + 1) + " / " + pages + " 页");
            hit.Click += (_, _) =>
            {
                if (_gridPage == me) return;
                _gridPage = me;
                RenderGridPage();
                Touch();
            };
            PagerDots.Children.Add(hit);
        }
    }

    /// <summary>网格上滚滚轮翻页（触屏直接点分页点）。</summary>
    private void GridPanel_Wheel(object? sender, PointerWheelEventArgs e)
    {
        try
        {
            if (!UseGridStyle || GridPages <= 1) return;
            var next = _gridPage + (e.Delta.Y < 0 ? 1 : -1);
            if (next < 0) next = GridPages - 1;
            if (next >= GridPages) next = 0;
            if (next == _gridPage) return;
            _gridPage = next;
            e.Handled = true;
            RenderGridPage();
            Touch();
        }
        catch (Exception ex)
        {
            Log("网格滚轮翻页失败: " + ex.Message);
        }
    }

    // ── 网格换页（2026-10-12 用户反馈补）：两侧半透明小箭头 + 触屏左右滑动 ──

    /// <summary>翻一页（dir = +1 下一页 / -1 上一页，循环）。滚轮、箭头、触屏滑动共用这一个入口。</summary>
    private void FlipPage(int dir)
    {
        if (GridPages <= 1) return;
        var next = (_gridPage + dir) % GridPages;
        if (next < 0) next += GridPages;
        if (next == _gridPage) return;
        _gridPage = next;
        RenderGridPage();
        Touch();
    }

    private void GridPrevPage_Click(object? sender, RoutedEventArgs e) => FlipPage(-1);
    private void GridNextPage_Click(object? sender, RoutedEventArgs e) => FlipPage(1);

    /// <summary>触屏滑动翻页的起点（PointerPressed 时记下，Released 时算位移）。</summary>
    private Avalonia.Point? _gridSwipeStart;

    private void ToolGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
        => _gridSwipeStart = e.GetPosition(ToolGrid);

    private void ToolGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            var start = _gridSwipeStart;
            _gridSwipeStart = null;
            if (start is null || !UseGridStyle || GridPages <= 1) return;

            var end = e.GetPosition(ToolGrid);
            var dx = end.X - start.Value.X;
            var dy = end.Y - start.Value.Y;

            // 左右滑翻页：横向位移够大、且明显比纵向大（免得跟纵向滚动 / 斜着点格子打架）
            if (Math.Abs(dx) >= 48 && Math.Abs(dx) > Math.Abs(dy) * 1.4)
                FlipPage(dx < 0 ? 1 : -1);
        }
        catch (Exception ex)
        {
            Log("网格滑动翻页失败: " + ex.Message);
        }
    }

    // ── 网格面板的「快捷」行（关机 / 锁定 / 重启 / 打开应用，2026-10-11 用户定）────────
    //
    // ⚠️ 关机 / 重启 = 走系统 shutdown 命令（/t 0 立即执行，不弹确认）；
    //    锁定       = user32 LockWorkStation —— 回到登录界面，本会话和本程序都继续活着；
    //    打开应用   = 底排「打开应用」同一条路（不收边条）。

    private void QuickShutdown_Click(object? sender, RoutedEventArgs e) => RunPowerCommand("/s /t 0", "关机");

    private void QuickRestart_Click(object? sender, RoutedEventArgs e) => RunPowerCommand("/r /t 0", "重启");

    private void QuickLock_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Log("网格「锁定」→ LockWorkStation");
            Platform.NativeMethodsShell.LockWorkStation();
        }
        catch (Exception ex)
        {
            Log("网格「锁定」失败: " + ex.Message);
        }
    }

    private void QuickOpenApp_Click(object? sender, RoutedEventArgs e) => OpenApp_Click(sender, e);

    /// <summary>执行 shutdown 命令（关机 / 重启共用）。Window 隐藏、不抢焦点。</summary>
    private void RunPowerCommand(string args, string what)
    {
        try
        {
            Log("网格快捷行 → " + what);
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo("shutdown", args)
            {
                UseShellExecute = true,
                CreateNoWindow = true
            };
            p.Start();
        }
        catch (Exception ex)
        {
            Log(what + "失败: " + ex.Message);
        }
    }

    private void Tool_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;
        try
        {
            var m = SidebarModules.Find(id);
            if (m is null) return;

            // 讲台动作：按一下就干活（放大镜是"按住"型，走的是按下/松手那条路，不走这里）
            if (m.Kind == SidebarModuleKinds.Action)
            {
                // 「安全选项」= 弹一个三行小菜单（锁定 / 切换用户 / 注销），不直接干活
                if (m.Id == "security")
                {
                    ShowSecurityFlyout(b);
                    return;
                }

                // ⚠️ 「要先问一句」的动作（关全部）不能按老办法在 Run 之后就还焦点：
                //    侧边栏一失活，刚弹出来的确认面板就会被系统按"点了别处"关掉。
                //    Run 里已经按 AsksFirst 区分过了，这里只管把面板弹出来。
                var asks = TeachingActions.AsksFirst(m.Id);
                var hint = TeachingActions.Run(m.Id);

                if (hint is { Length: > 0 })
                {
                    // ① 边条**不许收**（失焦/空闲两条自动收起路都要压住，否则他还得重新点开）；
                    // ② 用**原生 Flyout** 弹确认（框架自带的描边/圆角/投影 + 点别处自动收）。
                    SuppressAutoCollapse = true;
                    var lines = hint.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var title = lines.Length > 0 ? lines[0].Trim() : hint;
                    var body = lines.Length > 1 ? lines[1].Trim() : null;

                    var confirmed = false;

                    // ⚠️ 不只是"待确认"能走到这儿：出错时也会有提示（例如"截屏启动失败，看日志"）。
                    //    那种没什么可确认的，给个「知道了」就行 —— 别摆红色危险键、也别去执行什么。
                    ShowConfirmFlyout(b, title, body, asks ? "确定关闭" : "知道了", () =>
                    {
                        if (!asks) return;                 // 纯提示：按掉就完事
                        confirmed = true;
                        SuppressAutoCollapse = false;
                        TeachingActions.ConfirmPending(m.Id);
                        Touch();
                    },
                    onClosed: () =>
                    {
                        // 面板没了：只有"没真执行"（取消 / 超时 / 点别处）才把焦点还给用户原来的窗口，
                        // 别打断讲课。真执行了就不还 —— 那些窗口刚被关掉，还给谁都不对。
                        if (!confirmed) TeachingActions.RefocusPrevious();
                    },
                    dangerStyle: asks);

                    // ⚠️ 这里**绝不能**再调 RefocusPrevious()：那会立刻把侧边栏踢下台，
                    //    刚弹出的面板当场被关掉 —— 这正是"前台有窗口时按了没反应"的直接原因。
                }
                else
                {
                    HideConfirmFlyout();
                    Collapse();
                }
                return;
            }

            // 贴边展开的常驻面板（音量 / 屏幕亮度）：挨着边条长一栏，不跳窗口、不开新窗
            if (m.Kind == SidebarModuleKinds.Panel)
            {
                if (m.Id == "brightness") ToggleBrightness();
                else ToggleVolume();
                return;
            }

            Collapse();                                        // 先把边条收起来，别挡着工具窗口
            if (m.Kind == SidebarModuleKinds.Page && m.Page is not null)
                App.MainWindow?.OpenToolSettings(m.Page);      // 拉出主窗口并跳到那一页
            else
                ToolPaletteWindow.ShowTool(m.Id);              // 小浮窗
        }
        catch (Exception ex)
        {
            Log("打开工具失败: " + ex.Message);
        }
    }


    // ── 贴边面板（音量 / 屏幕亮度）──────────────────────────────────────────

    /// <summary>
    /// 「音量」模块的入口：**边条不收**（浮窗是挨着边条长出来的一栏，边条留着才像一个整体），
    /// 直接把主音量浮窗开在边条内侧；浮窗里再点「展开」看合成器。
    /// ⚠️ 必须先把自动收起压住：浮窗一显形就抢焦点，边条会以为自己"失焦"当场缩回去。
    /// </summary>
    private void ToggleVolume()
    {
        SuppressAutoCollapse = true;
        SetFlyoutAnchor(this);          // ⚠️ 必须在这之前：浮窗的边/锚点都看它（点左条就贴左边弹）
        Log("点击「音量」模块 → 开/关主音量浮窗（锚点=" + _edge + "）");
        try
        {
            VolumeWindow.Toggle(VolumeWindow.Target.Volume);
            Log($"  音量浮窗现在 {(VolumeWindow.IsVisible ? "开着" : "关着")}"
                + (VolumeWindow.IsVisible ? "" : "（关着且刚才是点击开，说明浮窗没能显示，去看 snip.log）"));
        }
        catch (Exception ex)
        {
            Log("  音量浮窗调用异常: " + ex);
        }
    }

    /// <summary>
    /// 「屏幕亮度」模块：跟音量**同一个浮窗**（换成亮度那一栏：下面那颗键是自动亮度，没有二级浮窗）。
    /// 其余（挨着边条、边条不收）完全一样。
    /// </summary>
    private void ToggleBrightness()
    {
        SuppressAutoCollapse = true;
        SetFlyoutAnchor(this);          // 同音量：亮度的浮窗也必须贴在被点的那条边条上
        Log("点击「屏幕亮度」模块 → 开/关亮度栏（锚点=" + _edge + "）");
        try
        {
            VolumeWindow.Toggle(VolumeWindow.Target.Brightness);
            Log($"  亮度栏现在 {(VolumeWindow.IsVisible ? "开着" : "关着")}"
                + (VolumeWindow.IsVisible ? "" : "（关着且刚才是点击开，说明浮窗没能显示，去看 snip.log）"));
        }
        catch (Exception ex)
        {
            Log("  亮度浮窗调用异常: " + ex);
        }
    }

    private void EndHold(string id)
    {
        try { TeachingActions.End(id); }
        finally { SuppressAutoCollapse = false; }
    }

    // ── "要先问一句"的原生 Flyout ────────────────────────────
    //
    // 为什么是原生 Flyout（而不是自己开个小窗口）：自己开窗要自己管窗口边框/背景/置顶/圆角，
    // 结果被 Nick 一眼看穿"很丑、有奇妙的白色边框、还不置顶"。用框架的 Flyout：
    // 描边/圆角/投影/主题全归系统，点别处自动收（light dismiss），也不用管 z 序。
    //
    // ⚠️ 移植说明：原版那句 `ShouldConstrainToRootBounds = false`（不把 Flyout 裁在 92dip 宽的
    //    边条窗口里）在 Avalonia 没有 —— Avalonia 的 Popup/Flyout 天生就是独立弹层，不裁边。
    //    资源键 ButtonBackground* / ButtonForeground* / ButtonBorderBrush* 在 Avalonia Fluent
    //    主题里同名存在，红底危险键那套写法可以原样照搬。

    /// <summary>
    /// 在边条旁边弹一个原生确认 Flyout（红底确定键）。
    /// `onClosed`：面板消失后调（不管用户是按了红键、按取消、点别处还是超时自动收）。
    /// </summary>
    // ── 安全选项菜单（系统快捷三件套，自上游 dv1.1.3 移植）──────────────────────

    /// <summary>
    /// 「安全选项」弹出的小菜单：锁定 / 切换用户 / 注销。
    /// ⚠️ Ctrl+Alt+Del 的蓝色界面属系统安全桌面，普通程序调不起来 —— 这里给的是它上面的操作本身。
    /// </summary>
    private void ShowSecurityFlyout(Button anchor)
    {
        try
        {
            HideSecurityFlyout();
            HideConfirmFlyout();

            var panel = new StackPanel { Width = 216, Spacing = 1 };
            panel.Children.Add(new TextBlock
            {
                Text = "安全选项",
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(12, 8, 12, 6),
            });

            void Item(string glyph, string text, Action act)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                row.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 16,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
                row.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 14,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });

                // 看着像系统菜单项：透明底、无边框、内容靠左
                var item = new Button
                {
                    Content = row,
                    MinHeight = 40,
                    Padding = new Thickness(12, 6, 12, 6),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                };
                item.Click += (_, _) =>
                {
                    HideSecurityFlyout();
                    try { act(); }
                    catch (Exception ex) { Log("安全选项执行失败: " + ex.Message); }
                };
                panel.Children.Add(item);
            }

            // 锁定 / 切换用户：按下去就干，没什么可问的
            Item("\uE72E", "锁定电脑", () => TeachingActions.Begin("lock"));
            Item("\uE748", "切换用户", () => TeachingActions.Begin("switchuser"));
            // 注销：会把所有程序关掉、没保存的内容会丢 → 跟「关全部」一样先问一句
            Item("\uE7E8", "注销…", () => BeginLogout(anchor));

            var flyout = new Flyout
            {
                Placement = ConfirmPlacement(),
                Content = panel,
            };
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_securityFlyout, flyout)) _securityFlyout = null;

                // 菜单收掉 = 用户没在继续操作 → 恢复自动收起。
                // ⚠️ 例外：点「注销」会**紧接着**弹确认面板（那时 _confirmFlyout 已经挂上了），
                //    这种情况不能解除压制，否则确认面板当场被收起来的边条一起带走。
                if (_confirmFlyout is null) SuppressAutoCollapse = false;
            };

            SuppressAutoCollapse = true;                // 菜单挂着的时候边条不许自己收
            _securityFlyout = flyout;
            flyout.ShowAt(anchor);
            Log("安全选项：菜单已弹出");
        }
        catch (Exception ex)
        {
            Log("安全选项菜单弹出失败: " + ex.Message);
            SuppressAutoCollapse = false;
        }
    }

    private void HideSecurityFlyout()
    {
        try
        {
            var f = _securityFlyout;
            _securityFlyout = null;
            f?.Hide();
        }
        catch { /* 收不掉就算了，下一个会顶掉它 */ }
    }

    /// <summary>「注销」两段式：先弹确认面板（会关掉所有程序），用户点了才真注销。</summary>
    private void BeginLogout(Button anchor)
    {
        var hint = TeachingActions.Begin("logout");
        if (string.IsNullOrEmpty(hint)) return;

        SuppressAutoCollapse = true;
        var lines = hint.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var title = lines.Length > 0 ? lines[0].Trim() : hint;
        var body = lines.Length > 1 ? lines[1].Trim() : null;

        ShowConfirmFlyout(anchor, title, body, "确定注销",
            () => TeachingActions.ConfirmPending("logout"), dangerStyle: true);
    }

    private void ShowConfirmFlyout(Button? anchor, string title, string? body, string okText, Action onConfirm,                                   Action? onClosed = null, bool dangerStyle = true)
    {
        try
        {
            HideConfirmFlyout();

            var panel = new Grid { Width = 300 };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(body))
            {
                text.Children.Add(new TextBlock
                {
                    Text = body,
                    FontSize = 12,
                    Opacity = 0.78,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            Grid.SetRow(text, 0);
            panel.Children.Add(text);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            };

            // 纯提示（非危险动作）不给「取消」——那会和「知道了」语义重复
            if (dangerStyle)
            {
                var cancel = new Button { Content = "取消", FontSize = 13, MinWidth = 76, Padding = new Thickness(0, 6, 0, 6) };
                cancel.Click += (_, _) => HideConfirmFlyout();
                buttons.Children.Add(cancel);
            }

            var danger = new Button { Content = okText, FontSize = 13, MinWidth = 88, Padding = new Thickness(0, 6, 0, 6) };
            if (dangerStyle)
            {
                // 红底危险键：这条资源链上按钮的刷子全改红，否则一 hover/按下就变回主题灰
                var red = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
                var redHover = new SolidColorBrush(Color.FromArgb(255, 0xD1, 0x43, 0x35));
                var redPressed = new SolidColorBrush(Color.FromArgb(255, 0xA8, 0x22, 0x16));
                var white = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
                var clear = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                danger.Resources["ButtonBackground"] = red;
                danger.Resources["ButtonBackgroundPointerOver"] = redHover;
                danger.Resources["ButtonBackgroundPressed"] = redPressed;
                danger.Resources["ButtonBackgroundDisabled"] = redPressed;
                danger.Resources["ButtonForeground"] = white;
                danger.Resources["ButtonForegroundPointerOver"] = white;
                danger.Resources["ButtonForegroundPressed"] = white;
                danger.Resources["ButtonBorderBrush"] = clear;
                danger.Resources["ButtonBorderBrushPointerOver"] = clear;
                danger.Resources["ButtonBorderBrushPressed"] = clear;
            }
            buttons.Children.Add(danger);

            Grid.SetRow(buttons, 1);
            panel.Children.Add(buttons);

            var flyout = new Flyout
            {
                Placement = ConfirmPlacement(),
                Content = panel,
            };
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_confirmFlyout, flyout)) _confirmFlyout = null;
                SuppressAutoCollapse = false;            // 面板没了 → 恢复自动收起
                try { onClosed?.Invoke(); } catch (Exception ex) { Log("确认面板收尾失败: " + ex.Message); }
            };
            danger.Click += (_, _) =>
            {
                // ⚠️ 顺序要紧：先把"确认"做完（onConfirm 会把 confirmed 立起来），再收面板。
                //    反过来的话，Closed 里的收尾会误当成"用户取消了"而去抢焦点。
                try { onConfirm(); } catch (Exception ex) { Log("确认动作失败: " + ex.Message); }
                flyout.Hide();
            };

            _confirmFlyout = flyout;

            var target = (Control?)anchor ?? Root;
            flyout.ShowAt(target);
            Log($"确认面板：原生 Flyout 已弹出（标题={title} 锚点={(target as Control)?.Name} 方位={flyout.Placement}）");

            // 兜底：20 秒还没人理就自己收（原生 Flyout 不退的话会一直挂着挡点击）
            _ = System.Threading.Tasks.Task.Delay(20000).ContinueWith(_ =>
            {
                try
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_confirmFlyout, flyout)) HideConfirmFlyout();
                    });
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Log("确认面板弹出失败: " + ex.Message);
            SuppressAutoCollapse = false;            // 面板没弹出来 → 别把"禁止收起"这个旗一直立着
        }
    }

    private void HideConfirmFlyout()
    {
        try
        {
            var f = _confirmFlyout;
            _confirmFlyout = null;
            f?.Hide();
        }
        catch { }
    }

    /// <summary>边条贴哪条边 → Flyout 往哪边弹（贴左往右弹，依此类推）。</summary>
    private PlacementMode ConfirmPlacement()
    {
        try
        {
            if (CurrentRect is { } r)
            {
                var work = WorkAreaRect();
                if (r.X <= work.X + 8) return PlacementMode.Right;
                if (r.X + r.Width >= work.X + work.Width - 8) return PlacementMode.Left;
                if (r.Y <= work.Y + 8) return PlacementMode.Bottom;
                if (r.Y + r.Height >= work.Y + work.Height - 8) return PlacementMode.Top;
            }
        }
        catch { }
        return PlacementMode.Right;
    }

    // ── 收起状态：点一下展开 / 按住拖动 ──────────────────────

    // ── 收起状态：点一下就展开（拖拽已撤除，见字段区注释）──────────

    private void Strip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // ⚠️ 2026-10-04（第 17 轮）：拖拽整条撤除。这里只做三件事 ——
        //    掐掉还在跑的滑动动画、记下"按着"、把指针捕获住。
        //    ⚠️ 捕获必须留着：手指/鼠标按下后可能挪出收起条再抬起，
        //    没有捕获的话 PointerReleased 不会回到这里，那一"点"就丢了（＝点了没反应）。
        StopSlide();
        _pressed = true;
        _idle.Stop();                                    // 按住期间别自动收起

        // 2026-10-11 加"滑动展开"：只记起点，**不动窗口**。位移阈值判据见 Strip_PointerReleased。
        _pressPos = e.GetPosition(Root);
        _lastPos = _pressPos;
        _swipedOpen = false;

        Log($"收起条：按下 device={e.Pointer.Type}");
        // ⚠️⚠️ 2026-10-11 实测（便携版跑起来看 sidebar.log 才发现）：捕获目标**必须是 CollapsedView 自己**，
        //    首版照抄了原来的 Capture(Root)。Avalonia 的指针捕获会把后续事件**路由到被捕获的那个控件**
        //    （Root 是 CollapsedView 的祖先，事件只会从 Root 往上冒泡，永远回不到 CollapsedView）——
        //    结果 PointerMoved / PointerReleased 全都收不到，只有 PointerCaptureLost 会来。
        //      · 点击展开没坏：CaptureLost 那条路把它兜住了（日志里就是「捕获丢失 → 当成单击，展开」）；
        //      · **滑动展开则彻底失效**：它挂在 Strip_PointerMoved 上，而 Moved 根本不来。
        //    改捕获自己之后 Moved/Released 才真正到手，滑动展开才有意义。
        //    （SnipOverlayWindow 一直是"捕获谁就挂谁"的写法，就是这个道理；首版没跟上。）
        e.Pointer.Capture(CollapsedView);
        e.Handled = true;
    }

    /// <summary>
    /// 2026-10-11 加：**向外滑就地展开**（贴左边向右滑、贴右边向左滑，上下边同理）。
    ///
    /// <para>
    /// ⚠️ 只是给"点击"多开一条路，**绝不取代它**：抬手时位移不够阈值的一律还按点击处理
    ///    （见 Strip_PointerReleased）。这是 2026-10-04 那条教训的红线 ——
    ///    触摸屏上手指按下必带几像素抖动，只要"位移不够就什么都不做"，用户看到的就是"点了没反应"。
    /// </para>
    /// </summary>
    private void Strip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;

        try
        {
            _lastPos = e.GetPosition(Root);

            // 已经展开过就别再来一遍（快速连滑会反复触发）
            if (_swipedOpen || _expanded) return;

            if (OutwardDelta(_lastPos) >= SwipeThresholdDip)
            {
                _swipedOpen = true;
                _pressed = false;
                try { e.Pointer.Capture(null); } catch { }
                Log("收起条：向外滑动 → 展开");
                Expand();
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            Log("收起条移动处理失败: " + ex.Message);
        }
    }

    private void Strip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        try { e.Pointer.Capture(null); } catch { }

        if (_swipedOpen) { e.Handled = true; return; }    // 滑动那一路已经展开了

        // ⚠️ 判据只有一条"该不该展开"，**没有第三种"什么都不做"**：
        //      · 向外滑够了  → 展开（滑动那条路，正常在 Moved 里就走掉了）
        //      · 位移不够阈值 → 展开（＝点击，2026-10-04 定的老行为，原样保留）
        //      · 朝**屏幕里**滑够了 20dip → 不展开（这是唯一被挡下的一种，
        //        而且必须是"明确的、朝着屏幕里的滑动"，手指抖动到不了这个量级）
        //      · 其余（沿边滑、斜着滑但没有明确向内）→ 一律展开，宁可多展一次
        var outward = OutwardDelta(e.GetPosition(Root));
        if (outward <= -SwipeThresholdDip)
        {
            Log($"收起条：向内滑动 {outward:0.#}dip，不展开（要展开请点一下或朝屏幕外滑）");
            e.Handled = true;
            return;
        }

        Log("收起条：单击/外滑 → 展开");
        Expand();
        e.Handled = true;
    }

    private void Strip_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;

        if (_swipedOpen) return;

        // ⚠️ 2026-10-04（第 17 轮）：捕获丢失同样当成"点了一下"。
        //    未激活的工具窗口上，按下常常只换来一次捕获丢失（没有正常的 Released）——
        //    这里若不展开，那一下点击就彻底丢了。拖动已撤除，也不存在"把拖动误当点击"的问题。
        //    2026-10-11：只有"明确朝屏幕里滑"才挡（跟 Released 同一条判据）。
        if (OutwardDelta(_lastPos) <= -SwipeThresholdDip)
        {
            Log("收起条：捕获丢失，但刚才是向内滑动 → 不展开");
            return;
        }

        Log("收起条：捕获丢失 → 当成单击，展开");
        Expand();
    }

    /// <summary>
    /// 从按下点到 <paramref name="now"/> 沿**屏幕外**方向挪了多少 dip（正 = 朝屏幕外）。
    /// 贴左/右看 X、贴上/下看 Y —— 另一根轴的位移**不算数**（沿边上下拖不该影响展不展开）。
    /// </summary>
    private double OutwardDelta(Point now)
    {
        var dx = now.X - _pressPos.X;
        var dy = now.Y - _pressPos.Y;
        return _edge switch
        {
            "left" => dx,          // 往右 = 往屏幕外
            "right" => -dx,        // 往左 = 往屏幕外
            "top" => dy,           // 往下 = 往屏幕外
            _ => -dy               // 往上 = 往屏幕外
        };
    }

    /// <summary>收起条的悬停高亮（鼠标搭上去就亮一下，触屏不会触发）。</summary>
    private void Strip_PointerEntered(object? sender, PointerEventArgs e)
    {
        _hover = true;
        UpdateCollapsedLook();
    }

    private void Strip_PointerExited(object? sender, PointerEventArgs e)
    {
        // 按着的时候不熄：捕获还在手上，指针挪出条外是常事，这时熄掉看着像"闪一下"
        if (_pressed) return;
        _hover = false;
        UpdateCollapsedLook();
    }

    /// <summary>
    /// 收起条两种样式各自的悬停效果：抓手 = 那截小条亮一点；细竖线 = 整条换成强调色。
    /// ⚠️ 只动这两个视觉，**绝不碰 Panel 底色 / 窗口背景** —— 那套是防白闪的（见 ApplyPanelBrush）。
    /// </summary>
    private void UpdateCollapsedLook()
    {
        try
        {
            if (UseLineCollapsed)
            {
                // 细竖线：内芯已去掉，悬停时整条铺一层半透明强调色当反馈；熄掉时清掉本地画刷，回到主题那支
                LineHover.Opacity = _hover ? 1.0 : 0;
                if (_hover) LineHover.Background = new SolidColorBrush(AccentColor(), 0.35);
                else LineHover.ClearValue(Border.BackgroundProperty);
            }
            else
            {
                Grip.Opacity = _hover ? 1.0 : 0.6;
            }
        }
        catch (Exception ex)
        {
            Log("收起条悬停效果失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 松手时定位置：看窗口中心离哪条边最近就吸过去，沿边的位置按松手处记下来。
    ///   · 自由模式：左/右/上/下**四条边**都参与吸附，吸到哪条边存进 <c>SidebarFreeEdge</c>。
    ///   · 贴靠模式：**只吸左右两条** —— 松手时中心若更靠上下，也按左右就近归位（贴靠 = 左右模式）。
    ///     ⚠️ 双边模式（设置 = both）下**不许换边** —— 换边就等于把"两边都有"拆成单边了。
    /// </summary>
    private void DockToNearestEdge()
    {
        try
        {
            var work = WorkAreaRect();
            var size = WinSizePx();
            var pos = WinPos();

            double cx = pos.X + size.Width / 2.0;
            double cy = pos.Y + size.Height / 2.0;

            var dl = Math.Abs(cx - work.X);
            var dr = Math.Abs(work.X + work.Width - cx);

            string edge;
            if (IsDual)
            {
                edge = _edge;                            // 留在自己这条边，只挪上下
            }
            else
            {
                var dt = Math.Abs(cy - work.Y);
                var db = Math.Abs(work.Y + work.Height - cy);
                var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
                edge = min == dl ? "left" : min == dr ? "right" : min == dt ? "top" : "bottom";

                if (IsFreeMode)
                {
                    App.Settings.Current.SidebarFreeEdge = edge;    // 四条边都收
                }
                else
                {
                    // 贴靠模式没有上下边：更靠上/下时按左右就近归位
                    if (edge is "top" or "bottom") edge = dl <= dr ? "left" : "right";
                    App.Settings.Current.SidebarEdge = edge;
                }
            }

            // 沿边位置存成 0~1 的比例，这样收起/展开尺寸不一样时也能对得上
            var flat = edge is "top" or "bottom";
            var alongStart = flat ? work.X : work.Y;
            var alongLen = flat ? work.Width : work.Height;
            var at = flat ? pos.X : pos.Y;
            var myLen = flat ? size.Width : size.Height;
            var free = Math.Max(0, alongLen - myLen);
            App.Settings.Current.SidebarPosRatio =
                free <= 0 ? 0.5 : Math.Clamp((at - alongStart) / (double)free, 0, 1);

            App.Settings.Save();

            // ⚠️⚠️ 2026-09-28 修：单边模式换不了边（Nick 报的"拖了但固定不到上/下/左边"）。
            //   上次把单例改成"一条边一个实例"之后，_edge 变成**实例级只读**字段，
            //   这里只改设置再 SnapToSetting() 是没用的 —— 这条实例还按自己那条老边走，松手一贴就弹回原边。
            //   换边必须走 ApplySetting()：它按新设置重建实例集合（新的那条建出来、这条藏起来）再贴过去。
            if (edge != _edge)
            {
                Log("换边: " + _edge + " → " + edge);
                ApplySetting();
                return;
            }

            SnapToSetting();

            // 双边：比例共享，让对面那条也落回同一高度收尾
            Partner()?.SnapToSetting();
        }
        catch (Exception ex)
        {
            Log("换边失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 双边模式的位置联动：把自己当前的沿边位置算成 0~1 比例甩给对面那条，让它立刻贴到同一高度。
    ///
    /// 拖动中每帧都会调，所以**故意不落盘** —— 每帧写一次 settings.json 会顿。
    /// 落盘交给松手时的 <see cref="DockToNearestEdge"/> 统一做。
    /// 用 <c>verify: false</c> 挪对面：跟着走的东西要跟手，别一帧里读回位置纠偏好几次。
    /// </summary>
    private void FollowPartner(int myY, int myHeight)
    {
        var other = Partner();
        if (other is null) return;
        try
        {
            var work = WorkAreaRect();
            var free = Math.Max(0, work.Height - myHeight);
            App.Settings.Current.SidebarPosRatio =
                free <= 0 ? 0.5 : Math.Clamp((myY - work.Y) / (double)free, 0, 1);
            other.MoveToEdge(verify: false);
        }
        catch (Exception ex)
        {
            Log("联动对面失败: " + ex.Message);
        }
    }

    // ── 动画 ─────────────────────────────────────────────────

    /// <summary>
    /// 展开：**整个窗口**（连亚克力底、圆角、描边一起）从贴的那条边滑进去。
    /// 以前是"窗口先瞬间铺开成一大块 → 里面的内容才自己滑"，所以先闪一个灰框，很出戏。
    /// 窗口位置交给不了合成器，只能按帧 Move（16ms 一帧 ≈ 60fps）；
    /// 而且从"露出半块"起步，不从屏幕外起步 —— 完全挪到屏幕外的窗口 DWM 往往不给它刷帧，
    /// 滑进来的第一帧会发虚。半块起步肉眼就是"从边上滑进来"。
    /// </summary>
    private void SlideInFromEdge()
    {
        StopSlide();                                     // 上一次没滑完就再展开：作废重来

        var size = PlannedSize();                        // 展开尺寸（此处 _expanded 已经置为 true）
        var finalPos = EdgePosition(size.Width, size.Height);
        _restRect = new PixelRect(finalPos.X, finalPos.Y, size.Width, size.Height);   // 滑动期间 CurrentRect 返回它

        // 起步只推"半块"：保证窗口还有一半留在屏幕里。整块挪到屏幕外的窗口
        // DWM 常常不给它刷帧，那滑进来的第一帧会发虚（见方法注释）。
        var start = OutwardOffset(finalPos, (int)((IsFlat ? size.Height : size.Width) / 2.0));

        // 一次把"起点位置 + 展开尺寸"设下去。分成 Resize + Move 两次的话，
        // 中间那一帧会被系统画出来 —— 那就是"闪一下再滑"的来源（2026-09-26 优化）。
        try { MoveResizeWin(start, size.Width, size.Height); }
        catch (Exception ex) { Log("滑入落位失败: " + ex.Message); }

        TweenWindow(start, finalPos, 220);
    }

    /// <summary>按帧把窗口从 from 挪到 to（缓出）。⚠️ 状态一变（收起/拖动/再展开）这一波就作废，绝不许它回头改窗口。</summary>
    private void TweenWindow(PixelPoint from, PixelPoint to, double ms, Action? done = null)
    {
        StopSlide();

        var epoch = ++_slideEpoch;
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (epoch != _slideEpoch)                        // 状态已经变了：这一波到此为止
            {
                timer.Stop();
                return;
            }

            var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
            var e = 1 - Math.Pow(1 - t, 3);              // ease-out cubic
            MoveWin(new PixelPoint(
                (int)Math.Round(from.X + (to.X - from.X) * e),
                (int)Math.Round(from.Y + (to.Y - from.Y) * e)));

            if (t < 1) return;
            StopSlide();
            MoveWin(to);                                  // 最后一帧对到准确位置
            try { var sz = WinSizePx(); _restRect = new PixelRect(to.X, to.Y, sz.Width, sz.Height); } catch { }
            if (done is not null) done();                 // 滑完了再收尾（收起就是靠它）
        };

        _slideTimer = timer;
        timer.Start();
    }

    /// <summary>收起：整个窗口往贴着的那条边**滑出去**（跟展开滑进来同一条路子，方向相反），滑完再真收。</summary>
    private bool SlideOutToEdge(Action done)
    {
        StopSlide();

        var from = WinPos();
        var size = WinSizePx();

        // ⚠️ 必须滑到**整个窗口都在屏幕外**（推的距离 = 当前厚度 + 2px 余量）。
        //    以前只推"展开尺寸 − 收起尺寸"（92−20=72），滑完还剩 20dip 的展开态残片贴在屏幕边上，
        //    紧接着又被 Resize 成抓手 —— 肉眼看就是"没滑出去就突然变身"，很出戏（2026-09-26 修）。
        //    现在滑到底时屏幕边缘是干净的，"变身"那一下（92×450 → 20×110）用户在屏幕外看不到，
        //    抓手再淡入接上，就顺了。
        var thickness = IsFlat ? size.Height : size.Width;
        var to = OutwardOffset(from, thickness + 2);
        TweenWindow(from, to, 240, done);
        return true;
    }

    private void StopSlide()
    {
        _slideEpoch++;                                    // 让在跑的那一波作废（关键：竞态的根治）
        _slideTimer?.Stop();
        _slideTimer = null;
    }

    /// <summary>旧的内容滑入（只动 Panel 里的东西，窗口先铺开）——现在不用了，留着备用。
    /// ⚠️ 移植说明：合成器 Offset 动画 → RenderTransform 平移 + 透明度按帧过渡。</summary>
    private void PlaySlideIn()
    {
        try
        {
            var from = IsFlat
                ? new Point(0, Edge == "top" ? -56 : 56)
                : new Point(Edge == "left" ? -56 : 56, 0);

            var translate = new TranslateTransform(from.X, from.Y);
            Panel.RenderTransform = translate;
            Panel.Opacity = 0.4;

            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / 190.0, 0, 1);
                var e = 1 - Math.Pow(1 - t, 3);           // ease-out cubic（近似原版贝塞尔）
                translate.X = from.X * (1 - e);
                translate.Y = from.Y * (1 - e);
                Panel.Opacity = 0.4 + 0.6 * e;
                if (t >= 1)
                {
                    translate.X = 0;
                    translate.Y = 0;
                    Panel.Opacity = 1;
                    Panel.RenderTransform = null;
                    timer.Stop();
                }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log("滑入动画失败: " + ex.Message);
        }
    }

    // ── 排版 / 位置 / 尺寸 ───────────────────────────────────

    /// <summary>按贴的边决定排版：贴左右 = 一列；贴上/下 = 两行（工具一行、按钮一行）。</summary>
    private void ApplyEdgeLayout()
    {
        try
        {
            var edge = Edge;
            var flat = edge is "top" or "bottom";

            // 收起状态的抓手：竖条时竖着、横条时横着
            GripStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            Grip.Width = flat ? 48 : 5;
            Grip.Height = flat ? 5 : 48;

            // 收起状态的细竖线：内芯已去掉（2026-10-12），窗口本身即那条线，无需再量芯的尺寸

            // 展开面板：永远竖着排（贴上下边时就是两行）
            ExpandedStack.Orientation = Orientation.Vertical;
            TitleRow.IsVisible = !flat;

            ToolStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            ToolStack.Spacing = flat ? 8 : 3;
            ToolStack.HorizontalAlignment = flat ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch;

            Sep.Margin = flat ? new Thickness(10, 4, 10, 4) : new Thickness(2, 3, 2, 3);

            FooterStack.Orientation = flat ? Orientation.Horizontal : Orientation.Vertical;
            FooterStack.Spacing = flat ? FooterFlatSpacingDip : 2;
            FooterStack.HorizontalAlignment = flat ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch;

            // 底部那排按钮是**逐颗**开关（「侧边布局」页底部那五个开关，key 见 SidebarFooterKeys）。
            // 关掉的那颗用 Collapsed —— StackPanel 不吃折叠子项，整排自己跟着收；一颗都不剩时连分隔线一起收。
            // ⚠️ 但**别靠 Visibility 反推高度**：尺寸那几处都有独立的"可见颗数"分支
            //    （<see cref="PlannedSize"/> / <see cref="ExpandedLimits"/> / <see cref="ApplyScrollLimit"/>），
            //    靠 Visibility 算会在"重排前先量尺寸"的顺序上算错。
            var hiddenFooter = App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>();
            foreach (var (key, btn) in FooterItems())
                btn.IsVisible = !hiddenFooter.Contains(key);

            var footerCount = VisibleFooterCount();
            FooterStack.IsVisible = footerCount > 0;
            Sep.IsVisible = footerCount > 0;

            // 贴上下边时给按钮留出宽度，别挤成一坨
            foreach (var b in _toolButtons)
            {
                b.MinWidth = flat ? 64 : 0;
                b.MinHeight = flat ? 48 : 52;
                b.Padding = flat ? new Thickness(4, 6, 4, 6) : new Thickness(0, 8, 0, 8);
            }
            foreach (var (_, b) in FooterItems())
            {
                b.MinWidth = flat ? FooterFlatButtonWidthDip : 0;
                // 横条时矮一点，不然两行加起来超出面板高度，下面一排会被裁掉
                b.MinHeight = flat ? 32 : 44;
                b.Padding = flat ? new Thickness(6, 2, 6, 2) : new Thickness(8, 4, 8, 4);
                b.HorizontalContentAlignment = flat ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch;
            }

            ApplyFooterContent(flat);

            // 模块多了就让工具区自己滚动（贴上下边时改成左右滚）
            // ⚠️ WinUI 的 Vertical/HorizontalScrollMode（触摸平移开关）在 Avalonia 的 ScrollViewer
            //    上没有对应 API；滚动方向的启停已由下面两行 ScrollBarVisibility 完整控制，故降级丢弃。
            ToolsScroll.VerticalScrollBarVisibility = flat ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            ToolsScroll.HorizontalScrollBarVisibility = flat ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            ApplyScrollLimit();

            // ── 网格面板：顶条那四颗小键也按「侧边布局 → 侧边栏底部按钮」的开关逐颗显隐
            //    （两套展开样式共用同一份设置，免得"设置里关掉了、网格上还在"）。
            //    「打开应用」那颗没进来 —— 网格末尾固定补的「主页」格子干的就是同一件事。
            if (GridResetButton is not null) GridResetButton.IsVisible = !hiddenFooter.Contains(SidebarFooterKeys.Reset);
            if (GridHideButton is not null) GridHideButton.IsVisible = !hiddenFooter.Contains(SidebarFooterKeys.Hide);
            if (GridPinButton is not null) GridPinButton.IsVisible = !hiddenFooter.Contains(SidebarFooterKeys.Pin);
            if (GridFoldButton is not null) GridFoldButton.IsVisible = !hiddenFooter.Contains(SidebarFooterKeys.Fold);

            // 箭头 = "点了会往哪边收"：贴着哪条边就朝哪边收（网格顶条那颗同理）
            var dir = edge;
            var foldGlyph = dir switch
            {
                "left" => "\uE76C",      // 往左收
                "top" => "\uE70D",       // 往下收
                "bottom" => "\uE70E",    // 往上收
                _ => "\uE76B",           // 往右收
            };
            if (_foldIcon is not null) _foldIcon.Glyph = foldGlyph;
            if (_gridFoldIcon is not null) _gridFoldIcon.Glyph = foldGlyph;

            ApplyCornerRadius();
        }
        catch (Exception ex)
        {
            Log("排版失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 底下**五个**按钮（收起 / 常驻 / 位置复原 / 隐藏 / 打开应用）的内容：
    /// 贴左右边是**竖条** → 图标在上、文字在下（跟工具块一个样式）；
    /// 贴上/下边是**横条**（面板只有 112 高）→ 左图标、右文字，竖排会被裁掉。
    /// ⚠️ 「打开应用」那颗是**图片图标**（软件自己的图标），走 SetFooterImageButton，别跟字形混。
    /// </summary>
    private void ApplyFooterContent(bool flat)
    {
        // 网格面板顶条那两颗的图标：XAML 里只把 FontIcon 当成 Content 摆着，这里把实例抓出来存住。
        //
        // ⚠️ 2026-10-11 首版图省事，在 FontIcon 上写了 x:Name 指望字段自动赋值 —— **不成立**。
        //    Avalonia 的 x:Name 只往 XAML 编译出来的那个同名字段里写，跟我这里手声明的
        //    _gridFoldIcon / _gridPinIcon 不是一个东西，结果 CS0649：两个字段永远是 null，
        //    顶条的钉住状态、箭头方向就再也不会跟着变（按钮本身还能点，纯视觉不更新，最难发现的那种）。
        //    改成照经典那套的路子 —— 从内容里取实例（经典那套是 SetFooterButton 的 out 参数）。
        _gridFoldIcon = GridFoldButton?.Content as FontIcon;
        _gridPinIcon = GridPinButton?.Content as FontIcon;

        SetFooterButton(FoldButton, "\uE76C", "收起", flat, out _foldIcon);
        SetFooterButton(PinButton, "\uE718", "常驻", flat, out _pinIcon);
        SetFooterButton(ResetButton, "\uE777", "位置复原", flat, out _);
        SetFooterButton(HideButton, "\uED1A", "隐藏", flat, out _);
        SetFooterImageButton(OpenAppButton, "打开应用", flat);
        UpdatePinVisual();
    }

    /// <summary>
    /// 底排那颗「打开应用」：版式跟 SetFooterButton 一模一样，只是把字形换成**软件自己的图标**
    /// （Assets\AppIcon-512.png，内嵌资源；Nick 2026-09-26 要求：这颗不要用别的图标）。
    /// </summary>
    private static void SetFooterImageButton(Button b, string label, bool flat)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = flat ? 11 : 10.5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var img = new Image
        {
            Source = AppIconImage(),
            Width = flat ? 14 : 17,
            Height = flat ? 14 : 17,
            Stretch = Stretch.Uniform,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        if (flat)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(img);
            row.Children.Add(text);
            b.Content = row;
        }
        else
        {
            text.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(img);
            col.Children.Add(text);
            b.Content = col;
        }
    }

    private static IImage? _appIconImage;

    /// <summary>软件自己的图标（内嵌 AppIcon-512.png，只解一次、缓存住）。解不开就返回 null（那颗按钮只剩文字，不会崩）。</summary>
    private static IImage? AppIconImage()
    {
        if (_appIconImage is not null) return _appIconImage;
        try
        {
            var path = EmbeddedAssets.ExtractToCache("AppIcon-512.png", "AppIcon-512.png");
            if (string.IsNullOrEmpty(path)) return null;
            // ⚠️ 移植说明：原版用 BitmapImage.DecodePixelWidth=64 省内存；Avalonia 的 Bitmap 没有
            //    解码尺寸选项，直接整张解（PNG 512² 解一次就缓存，代价可接受）。
            using var fs = System.IO.File.OpenRead(path);
            _appIconImage = new Bitmap(fs);
        }
        catch (Exception ex)
        {
            Log("解应用图标失败: " + ex.Message);
        }
        return _appIconImage;
    }

    private static void SetFooterButton(Button b, string glyph, string label, bool flat, out FontIcon icon)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = flat ? 11 : 10.5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = flat ? 13 : 15,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        if (flat)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(icon);
            row.Children.Add(text);
            b.Content = row;
        }
        else
        {
            text.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(icon);
            col.Children.Add(text);
            b.Content = col;
        }
    }

    /// <summary>
    /// 底排五颗按钮 + 各自的设置 key（顺序 = 在侧边栏上的先后）。
    /// ⚠️ key 是**存档格式**的一部分（<see cref="SidebarFooterKeys"/>），别随手改字面量。
    /// </summary>
    private (string Key, Button Btn)[] FooterItems() => new (string, Button)[]
    {
        (SidebarFooterKeys.Fold, FoldButton),
        (SidebarFooterKeys.Pin, PinButton),
        (SidebarFooterKeys.Reset, ResetButton),
        (SidebarFooterKeys.Hide, HideButton),
        (SidebarFooterKeys.OpenApp, OpenAppButton),
    };

    /// <summary>
    /// 底排当前**可见**几颗（0~5）—— 按设置里"关掉了哪几颗"过滤。
    /// 尺寸处处用它，⛔ 别写死 5：逐颗开关之后，少一颗面板就得少一截。
    /// </summary>
    private static int VisibleFooterCount()
    {
        var hidden = App.Settings.Current.SidebarFooterHidden ?? Array.Empty<string>();
        return SidebarFooterKeys.All.Count(k => !hidden.Contains(k));
    }

    private double Scale() => RenderScaling > 0 ? RenderScaling : 1.0;

    /// <summary>当前状态下窗口该多大（dip → 物理像素）。尺寸会跟着模块数量走。</summary>
    private PixelSize PlannedSize()
    {
        var scale = Scale();
        // 模块数量：按钮 52 + 间距 3；竖着时面板高度 = 200 + 55×个数（少于 4 个也不缩太多，免得看着空）
        var count = Math.Max(1, _toolButtons.Count);
        // 底排现在能看见几颗（逐颗开关，见 VisibleFooterCount）：尺寸处处按它算，
        // 少一颗竖条矮一截、横条窄一截，一颗不剩时横条还会从两行变一行（薄一截）。
        var footerCount = VisibleFooterCount();
        int dipW, dipH;
        if (_expanded)
        {
            if (UseGridStyle)
            {
                // 图标网格：宽按"一页铺得开的列数"算死（不随模块数变，多了只加页），
                // 高按 顶条 + 网格 + 分页点(可选) + 快捷设置行 加起来，再夹到工作区内。
                // ⚠️ 这条分支**不走 ExpandedLimits** —— 那套是给竖排列表定的（横条被钉死 112 高），
                //    网格面板贴上下边时要自己的一套。
                var g = GridPanelDip();
                var sc = Scale();
                var work = WorkAreaRect();
                dipW = (int)Math.Min(g.W, work.Width / sc - 32);
                dipH = (int)Math.Min(g.H, work.Height / sc - 32);
            }
            else
            {
                var lim = ExpandedLimits();
                if (IsFlat)
                {
                    // 上/下边：竖着两行 —— 第一行工具、第二行底排按钮（一颗不剩就只剩第一行）。
                    // 宽度取**两行里更宽的那行**：底排是固定几颗（不被裁的硬要求），
                    // 工具行多到放不下时由 ToolsScroll 横向滚动兜底。
                    var toolsRow = count * 64 + (count - 1) * 8 + FlatRowPadDip;
                    var footerRow = footerCount > 0
                        ? footerCount * FooterFlatButtonWidthDip
                          + (footerCount - 1) * FooterFlatSpacingDip + FlatRowPadDip
                        : 0;

                    dipW = (int)Math.Min(Math.Max(Math.Max(PanelLengthFlatDip, toolsRow), footerRow), lim.W);
                    dipH = footerCount > 0 ? PanelThicknessFlatDip : PanelThicknessFlatBareDip;
                }
                else
                {
                    dipW = PanelThicknessDip;
                    // 底排最多五颗（收起/常驻/位置复原/隐藏/打开应用），增量按可见颗数摊（见 FooterDipFor）
                    dipH = (int)Math.Min(Math.Max(PanelLengthDip, 200 + 55 * count) + FooterDipFor(footerCount), lim.H);
                }
            }
        }
        else
        {
            return CollapsedSize();
        }

        return new PixelSize((int)Math.Round(dipW * scale), (int)Math.Round(dipH * scale));
    }

    /// <summary>
    /// 展开面板最多占多大（dip）。模块再多也不让它长满整屏 —— 超出的部分交给工具区滚动（ToolsScroll）。
    /// </summary>
    private (double W, double H) ExpandedLimits()
    {
        var scale = Scale();
        var work = WorkAreaRect();
        var workW = work.Width / scale - 32;          // 离屏幕两边留点空
        var workH = work.Height / scale - 32;
        var footerCount = VisibleFooterCount();

        if (IsFlat)
            return (Math.Min(workW, Math.Max(PanelLengthFlatDip, workW * 0.92)),
                    footerCount > 0 ? PanelThicknessFlatDip : PanelThicknessFlatBareDip);

        return (PanelThicknessDip,
                Math.Min(workH, Math.Max(PanelLengthDip + FooterDipFor(footerCount), workH * 0.85)));
    }

    /// <summary>工具区最多能占多高/多宽（面板高度 - 标题/分隔线/底排按钮）。</summary>
    private void ApplyScrollLimit()
    {
        try
        {
            if (!_expanded) return;

            // 图标网格那套不滚动：一页几列几行是定死的，模块多了只加页（分页点切），
            // 所以这里既不用算竖排那套 chrome，也不该把 ToolsScroll 的范围留成上一次的旧值。
            if (UseGridStyle)
            {
                ToolsScroll.MaxHeight = double.PositiveInfinity;
                ToolsScroll.MaxWidth = double.PositiveInfinity;
                return;
            }

            var scale = Scale();
            var work = WorkAreaRect();

            if (IsFlat)
            {
                var chrome = 12 + 24;                                    // 面板 padding + 间距余量
                ToolsScroll.MaxWidth = Math.Max(120, work.Width / scale - 32 - chrome);
                ToolsScroll.MaxHeight = double.PositiveInfinity;
            }
            else
            {
                var title = TitleRow.IsVisible ? 26 : 0;
                // 底排：可见 n 颗 → n×44 + (n−1)×2；一颗都不显示时这段是 0（工具区能多吃掉这份高度）
                var fn = VisibleFooterCount();
                var footer = fn > 0 ? fn * 44 + (fn - 1) * 2 : 0;
                var chrome = title + 7 + footer + 17 + 9;                // + 分隔线 + 面板 padding + 几个间距
                ToolsScroll.MaxHeight = Math.Max(120, ExpandedLimits().H - chrome);
                ToolsScroll.MaxWidth = double.PositiveInfinity;
            }
        }
        catch (Exception ex)
        {
            Log("算滚动范围失败: " + ex.Message);
        }
    }


    private void ApplySize()
    {
        try
        {
            ApplyScrollLimit();
            var size = PlannedSize();
            ResizeWin(size.Width, size.Height);
        }
        catch (Exception ex)
        {
            Log("改尺寸失败: " + ex.Message);
        }
    }

    /// <summary>收起态的窗口尺寸（跟 PlannedSize 的收起分支同一套算法，抽出来给动画算落位用）。</summary>
    private PixelSize CollapsedSize()
    {
        var scale = Scale();
        // ⚠️ 两种收起样式尺寸不一样（grip 20×110 / line 6×220），换算式都收在
        //    CollapsedDipW / CollapsedDipH 里 —— 圆角也是从那儿读的，改就一起改。
        return new PixelSize(
            (int)Math.Round(CollapsedDipW * scale),
            (int)Math.Round(CollapsedDipH * scale));
    }

    /// <summary>收起态离屏幕边的空隙（dip）。2026-10-12 用户要求细线样式"离屏幕边缘 8px"。
    /// ⚠️ 只对细线（line）样式生效 —— grip 抓手保持原版死贴边（2026-10-13 用户要求抓手"和之前一样"）。</summary>
    private const int CollapsedEdgeGapDip = 8;

    /// <summary>按当前贴的边算"贴边位置"（**纯计算，不动窗口**）。参数是窗口按哪套尺寸算。
    /// ⚠️ 收起态会往里让 <see cref="CollapsedEdgeGapDip"/>（展开态仍贴边）。</summary>
    private PixelPoint EdgePosition(int width, int height)
    {
        var work = WorkAreaRect();

        var edge = Edge;
        var flat = edge is "top" or "bottom";

        var alongLen = flat ? work.Width : work.Height;
        var myLen = flat ? width : height;
        var free = Math.Max(0, alongLen - myLen);

        var ratio = App.Settings.Current.SidebarPosRatio;
        var offset = ratio < 0 ? free / 2.0 : Math.Clamp(ratio * free, 0, free);

        // 收起态离边空隙（换算成物理像素）：只有细线样式让 8dip；grip 抓手原版贴边；展开面板贴边
        var gap = (_expanded || !UseLineCollapsed) ? 0 : (int)Math.Round(CollapsedEdgeGapDip * Scale());

        return edge switch
        {
            "left" => new PixelPoint(work.X + gap, (int)Math.Round(work.Y + offset)),
            "top" => new PixelPoint((int)Math.Round(work.X + offset), work.Y + gap),
            "bottom" => new PixelPoint((int)Math.Round(work.X + offset), work.Y + work.Height - height - gap),
            _ => new PixelPoint(work.X + work.Width - width - gap, (int)Math.Round(work.Y + offset))
        };
    }

    /// <summary>
    /// 把位置往"屏幕外"方向推开 dist 像素（贴右往右推、贴左往左推，上/下同理）。
    /// 纯计算，不动窗口；距离由调用方按用途给（滑入起步用半块、滑出收尾用整个厚度）。
    /// </summary>
    private PixelPoint OutwardOffset(PixelPoint p, int dist)
    {
        if (dist < 1) dist = 1;

        return Edge switch
        {
            "left" => new PixelPoint(p.X - dist, p.Y),
            "top" => new PixelPoint(p.X, p.Y - dist),
            "bottom" => new PixelPoint(p.X, p.Y + dist),
            _ => new PixelPoint(p.X + dist, p.Y)
        };
    }

    /// <summary>贴到设置里那条边；沿边的位置按 SidebarPosRatio（&lt;0 = 居中）。</summary>
    /// <param name="verify">
    /// false = 只挪一次，**不读回位置做纠偏**。纠偏最多会连挪 4 次窗口，放在动画前会把头几帧挤掉，
    /// 所以动画路径上用它（落位精度由动画自己的最后一帧保证）。
    /// </param>
    private void MoveToEdge(bool verify = true)
    {
        try
        {
            // ⚠️ 用自己的目标尺寸算，别读窗口实测尺寸 —— Resize 刚调完它还没更新，会按老尺寸贴边
            var size = PlannedSize();
            var pos = EdgePosition(size.Width, size.Height);
            var x = pos.X;
            var y = pos.Y;

            if (!verify)
            {
                MoveWin(pos);
                return;
            }

            // 移动有时不精确，量一下再纠偏几次
            var target = new PixelPoint(x, y);
            for (var i = 0; i < 4; i++)
            {
                MoveWin(target);
                var now = WinPos();
                var dx = x - now.X;
                var dy = y - now.Y;
                if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2) break;
                target = new PixelPoint(target.X + dx, target.Y + dy);
            }
        }
        catch (Exception ex)
        {
            Log("贴边失败: " + ex.Message);
        }
    }

    // ── Win32 窗口操作（原版 AppWindow 的等价物；无边框窗口外框 = 客户区）──

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private IntPtr Hwnd() => Backdrop.TryGetHwnd(this);

    private void MoveWin(PixelPoint p)
    {
        var hwnd = Hwnd();
        if (hwnd == IntPtr.Zero) { Position = p; return; }
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void ResizeWin(int w, int h)
    {
        var hwnd = Hwnd();
        if (hwnd == IntPtr.Zero) { ClientSize = new Size(w / Scale(), h / Scale()); return; }
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void MoveResizeWin(PixelPoint p, int w, int h)
    {
        var hwnd = Hwnd();
        if (hwnd == IntPtr.Zero) { Position = p; ClientSize = new Size(w / Scale(), h / Scale()); return; }
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private PixelPoint WinPos()
    {
        try
        {
            var hwnd = Hwnd();
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
                return new PixelPoint(r.Left, r.Top);
        }
        catch { }
        return Position;
    }

    private PixelSize WinSizePx()
    {
        try
        {
            var hwnd = Hwnd();
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
                return new PixelSize(r.Width, r.Height);
        }
        catch { }
        return new PixelSize((int)Math.Round(ClientSize.Width * Scale()), (int)Math.Round(ClientSize.Height * Scale()));
    }

    /// <summary>主显示器工作区（物理像素）= 原版 DisplayArea.Primary.WorkArea。
    /// ⚠️ Avalonia 的 Screens 挂在窗口实例上（原版 DisplayArea 是静态 API），所以这里只能做成实例方法。</summary>
    private PixelRect WorkAreaRect()
    {
        try
        {
            return Screens.Primary.WorkingArea;
        }
        catch
        {
            // 屏幕枚举不可用（极少见）：给个常见桌面大小的兜底，别让定位计算崩掉
            return new PixelRect(0, 0, 1920, 1040);
        }
    }

    // ── 日志 ─────────────────────────────────────────────────

    private static void Log(string message)
    {
        try
        {
            // 移植说明：上游 dv1.1.0 起日志统一走 Core.AppLog（带级别、写 logs\ 子目录）。
            Core.AppLog.Info("sidebar", message);
        }
        catch { }
    }
}
