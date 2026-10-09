using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Audio;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 单滑块浮窗：点侧边栏「音量」/「屏幕亮度」模块弹出，**挨着边条**长出来一栏（边条不收起，见 VolumeFlyoutGroup）。
///
/// ⚠️ 类名是历史遗留（一开始只伺候音量）—— 现在**音量 + 屏幕亮度共用这一个窗口**，
///    靠 <see cref="Target"/> 区分：文案、数值来源、下面那颗键的含义都不一样。
///    两个模块共用一个窗口还有个好处：同一时刻只可能开一栏，不会两块都贴在边条上。
///
/// 从上到下（贴左/右时）= 数值 + 垂直滑块 + 第一颗键（音量：静音 / 亮度：自动亮度）+ 第二颗键（仅音量：展开合成器）；
/// 贴上/下时自动换成**横版**（从左到右一排），见 <see cref="ApplyOrientation"/>。
///
/// 窗口外壳 / 材质（跟边条同一套）/ 贴边几何都在 <see cref="FlyoutChrome"/> + <see cref="EdgeGeometry"/> 里，
/// 本文件只管「长什么样、报什么数」。
///
/// ⚠️ **音量那一路和亮度那一路的读取代价差了三个数量级**（2026-09-27 修）：
///    音量 = 内存里的 Core Audio 调用（微秒级），1.5 秒轮一次没问题；
///    亮度 = WMI 查一次十几到几十毫秒，**自动亮度还要起一个 powercfg 进程**（WaitForExit 最长 4 秒）。
///    两条路都按 1.5 秒压在 UI 线程上 → 浮窗开着就周期性卡顿、白烧 CPU 和 IO。
///    现在：亮度改 3 秒 + **后台线程读**（见 <see cref="KickBrightnessRead"/>），
///    自动亮度状态只在**开窗那次**和**自己改过之后**读（那状态不会自己变）。
///
/// ⚠️ 移植说明：DispatcherQueueTimer → Avalonia <see cref="DispatcherTimer"/>；
///    AppWindow（Title/Size/Position）→ FlyoutChrome 的对应封装；WorkArea → Screens.Primary.WorkingArea。
/// </summary>
public sealed partial class VolumeWindow : Window
{
    /// <summary>这一栏现在在伺候谁。</summary>
    public enum Target
    {
        /// <summary>系统主音量（下面第一颗键 = 静音）。</summary>
        Volume,
        /// <summary>屏幕亮度（下面第一颗键 = 自动亮度；**没有**二级浮窗）。</summary>
        Brightness,
    }

    // 卡片尺寸（dip）：**宽度固定 300** = 滑块 272 + 左右各 14 内边距；高度按内容实测（约 128）。
    private const int CardWidthDip = 300;
    private const int CardHeightDip = 140;      // 量不出来时的兜底高度

    // 再挤也别小于这个（不然圆角/描边会把内容吃掉）
    private const int MinPanelWidthDip = 240;
    private const int MinPanelHeightDip = 108;

    private static VolumeWindow? _instance;

    private readonly FlyoutChrome _chrome;
    private readonly FlyoutSlider _slide = new();

    private DispatcherTimer? _poll;
    private bool _visible;
    private bool _started;
    private bool _syncing;
    private bool _dragging;             // 滑块正被拖着（音量和亮度共用这一个标志）
    private Target _target = Target.Volume;

    // ── 亮度这一路的缓存（读数很重，见类注释）──
    private bool? _adaptiveOn;              // 自动亮度状态；只在开窗那次和自己改过之后读
    private int _brightnessPercent = -1;    // 亮度缓存（-1 = 还没读到）
    private bool _brightnessReadable = true;
    private bool _brightnessReading;        // 后台读正在进行，别叠着再发一个

    private VolumeWindow()
    {
        InitializeComponent();
        _chrome = new FlyoutChrome(this, Root, Panel, "音量");
        Configure();
    }

    /// <summary>开 / 关（再点一次同一个模块就收起来；点另一个模块就换成那一栏）。</summary>
    public static void Toggle(Target target)
    {
        try
        {
            ScreenCapture.Log($"Toggle({target}) 进来：instance={(_instance is not null)} visible={_instance?._visible}");
            _instance ??= new VolumeWindow();
            var w = _instance;

            if (w._visible && w._target == target)
            {
                ScreenCapture.Log("Toggle → 同目标已开着 → HideSelf");
                w.HideSelf();
                return;
            }

            var switched = w._target != target;
            w._target = target;
            if (switched) w.ApplyTarget();

            if (w._visible) w.Reposition();      // 已经开着：按新内容重新量一遍尺寸挪一下（不重播滑入）
            else w.ShowSelf();
        }
        catch (Exception ex)
        {
            // ⚠️ 这里原来是个空 catch —— 整段异常全吞掉，"点了没反应"就完全查不出来。
            //    项目其他地方一律走 ScreenCapture.Log，这里跟着统一。
            ScreenCapture.Log("浮窗开关失败: " + ex.Message);
        }
    }

    public static void CloseIfOpen()
    {
        if (_instance?._visible == true) _instance.HideSelf();
    }

    /// <summary>主音量浮窗现在开着吗。</summary>
    public static new bool IsVisible => _instance?._visible == true;

    /// <summary>
    /// 用户正在进行"操作态"：拖着滑块，或者在拖着整个浮窗。
    /// 鼠标监视据此绝不在操作中途收窗。
    /// </summary>
    public static bool IsDragging => _instance?._dragging == true || _instance?._chrome.IsDragging == true;


    /// <summary>
    /// 浮窗贴屏幕的哪条边。滑入方向、合成器排哪儿全靠它。
    ///
    /// ⚠️⚠️ 2026-10-02 改（用户反馈「在左侧打开音量调节会在右侧显示」）：
    ///    以前这里**只从设置里读**，而且把「左右两边同时显示 = both」强行归到 <c>right</c> ——
    ///    于是"点左边那条边条的音量 → 浮窗跑到屏幕右边"。
    ///    现在优先取**刚被点击的那条边条实际贴的边**（<see cref="ToolSidebarWindow.AnchorEdge"/>），
    ///    取不到（边条没显示）才退回读设置。上下边（自由模式）也一并支持。
    /// </summary>
    public static string Edge => ToolSidebarWindow.AnchorEdge
        ?? (App.Settings.Current.SidebarEdge is "left" or "top" or "bottom"
                ? App.Settings.Current.SidebarEdge
                : "right");

    /// <summary>主音量浮窗当前在屏幕上的矩形（给合成器浮窗「贴着它排」用）。</summary>
    public static PixelRect? CurrentRect => _instance?._chrome.CurrentRect;

    /// <summary>主音量浮窗本次落定的**最终**矩形（滑入动画的目标位置，不是半路上的实时位置）。
    /// ⚠️ 合成器必须锚这里：锚实时位置的话，主音量还在滑入时点「展开」，
    ///    合成器就按半路位置落座，主音量随后滑到位正好压进合成器（2026-10-01 修「三个窗叠一起」）。</summary>
    public static PixelRect? AnchorRect => _instance?._finalRect ?? CurrentRect;

    /// <summary>ShowSelf/Reposition 里算出的最终落点（滑入动画的目标）。</summary>
    private PixelRect? _finalRect;

    private void Configure()
    {
        try
        {
            // ⚠️ mainScope: false（2026-10-08，Nick 截图反馈：分体深色下音量/亮度浮窗还是白的）：
            //    口径变更 —— 音量/亮度小浮窗改跟「外部组件」的作用域走：
            //      · 「外部组件单独设置外观」开着 → 跟外部组件外观（深色就是深色浮窗）；
            //      · 没开 → 跟主界面的颜色模式（与原行为一致）。
            //    旧的"永远跟主界面"（mainScope: true，2026-10-03）就此废弃；合成器浮窗同一口径。
            ThemeCompat.Apply(Root);
            VolumeFlyoutGroup.MainHwnd = _chrome.Hwnd;

            // ⚠️ handledEventsToo: true —— Slider 内部会把 PointerPressed 标成 handled，
            //    普通 += 订阅收不到，"_dragging" 就永远是 false，
            //    轮询一到就把滑块值按真实值写回去（拖到一半被"拽回去"就是这个）。
            //（WinUI 的 UIElement 路由事件 → Avalonia 挂在 InputElement 上，与 SidebarLayoutPage 同一套写法）
            MasterSlider.AddHandler(InputElement.PointerPressedEvent,
                new EventHandler<PointerPressedEventArgs>((_, _) => _dragging = true),
                RoutingStrategies.Bubble, true);
            MasterSlider.AddHandler(InputElement.PointerReleasedEvent,
                new EventHandler<PointerReleasedEventArgs>((_, _) => EndDrag()),
                RoutingStrategies.Bubble, true);
            MasterSlider.AddHandler(InputElement.PointerCaptureLostEvent,
                new EventHandler<PointerCaptureLostEventArgs>((_, _) => EndDrag()),
                RoutingStrategies.Bubble, true);

            // ── 拖动：**已全部撤掉**（2026-10-03，用户第 16 轮原话：
            //    「悬浮窗还是无法点击展开，去掉拖动调整位置吧」）──────────────────────────
            // 历史三阶段：① Avalonia 层 EnableDrag —— 真机的假抬起/捕获丢失让它时灵时不灵；
            //             ② 第 14 轮换 GripDragFrame 系统级 HTCAPTION —— 拖动本身好使了，
            //                但把手行成了**非客户区**，鼠标从把手进浮窗不再触发激活，
            //                滑块/按钮的第一下就被吃掉（用户感受＝"还是无法点击展开"）；
            //             ③ 本轮按用户取舍收尾：**要能点，不要拖**。
            // 浮窗位置一律由 Reposition() 贴回侧边条旁，不再由用户摆放；
            // 顺带把「鼠标监视会不会误判拖动」这个不确定性也一并去掉。

            Deactivated += (_, _) =>
            {
                // ⚠️ 这里**不再**用失焦判据收窗（那正是"弹出 1 秒就自己没了"的根因）。
                //    收窗统一走 VolumeFlyoutGroup 的鼠标监视；失焦时重新确保它在跑即可。
                VolumeFlyoutGroup.EnsureMouseWatch();
            };

            Root.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) VolumeFlyoutGroup.CloseAll();
            };
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("音量浮窗初始化失败: " + ex.Message);
        }
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Refresh();
    }

    /// <summary>
    /// 「按下的这个东西该不该自己处理」—— 落在滑块 / 按钮上就**不算**拖浮窗，交回给控件。
    ///
    /// <para>
    /// 从命中的元素沿视觉树往上走到窗口根，途中只要碰到交互控件就拦住。
    /// 不拦的话，点滑块会被当成"拖窗口"，音量就调不了了（<c>Root</c> 全窗都在拖动范围内，
    /// 这是"无标题栏窗口还能拖"必须付的代价，靠这一条守住）。
    /// </para>
    /// </summary>
    // ── 尺寸 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 卡片尺寸 = **宽固定 300 + 高按内容实测**。
    ///
    /// ⚠️ 2026-10-02 重做：旧版按「贴左/右 = 竖版 / 贴上/下 = 横版」两套布局切来切去，
    ///    竖版那套是 72×300 的窄条 —— 屏幕边上一根又细又长的东西，观感差、拖拽行程也短。
    ///    现在**只有横版这一种**（滑块 272 dip，拖拽行程是原来的近两倍），
    ///    所以方向那套逻辑整段删掉了，尺寸只剩一个变量：高度。
    /// </summary>
    private Point SizeDip()
    {
        var saved = MasterPercentText.Text;
        try
        {
            MasterPercentText.Text = "100%";                 // 按最宽的数字量，99%→100% 不会跳宽度
            ContentHost.Measure(Size.Infinity);

            var h = Math.Max(ContentHost.DesiredSize.Height, MinPanelHeightDip);
            return new Point(Math.Max(CardWidthDip, MinPanelWidthDip), Math.Ceiling(h) + 2);   // +2 = 1px 描边 ×2
        }
        catch
        {
            return new Point(CardWidthDip, CardHeightDip);
        }
        finally
        {
            MasterPercentText.Text = saved;
        }
    }

    // ── 显示 / 收起 ───────────────────────────────────────────────────────

    private PixelRect WorkArea()
    {
        try { return Screens.Primary.WorkingArea; }
        catch { return new PixelRect(0, 0, 1920, 1040); }
    }

    private void ShowSelf()
    {
        try
        {
            ApplyTarget();                              // 文案 / 图标 / 第二颗键的显隐，先按当前 target 摆好

            var scale = _chrome.Scale;
            var work = WorkArea();

            Start();                                    // 先把内容刷好（数字先摆对），再按内容量面板尺寸
            var dip = SizeDip();
            var w = (int)Math.Round(dip.X * scale);
            var h = (int)Math.Round(dip.Y * scale);

            // 锚点 = 边条（挨着它长）；边条拿不到就当成贴屏幕边
            var anchor = ToolSidebarWindow.CurrentRect ?? EdgeGeometry.EdgeBar(Edge, work);
            var (start, final) = EdgeGeometry.BesideAnchor(Edge, anchor, work, w, h, scale);
            _finalRect = new PixelRect(final.X, final.Y, w, h);   // 给合成器当锚（别让它锚半路上的实时位置）

            _chrome.Present(start, w, h);
            _visible = true;

            _chrome.Finish(Root.ActualThemeVariant == ThemeVariant.Dark);   // ⚠️ 必须在显示之后调

            FlyoutFade.In(ContentHost, 160);
            _slide.Run(_chrome.Hwnd, start, final, FlyoutSlider.SlideInMs);

            VolumeFlyoutGroup.HoldSidebar();             // 边条按住，别让它缩回去
            VolumeFlyoutGroup.EnsureMouseWatch();        // 鼠标移开就收起（不用失焦判据，见 VolumeFlyoutGroup 注释）
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("音量浮窗显示失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        ScreenCapture.Log($"HideSelf：visible={_visible}（谁叫的看调用方日志）");
        if (!_visible) return;
        _visible = false;
        Stop();

        try
        {
            var scale = _chrome.Scale;
            var size = _chrome.SizePx;
            var work = WorkArea();

            var from = _chrome.Position;
            var to = EdgeGeometry.FullyOut(from, Edge, EdgeGeometry.Thickness(Edge, size.Width, size.Height));

            _slide.Run(_chrome.Hwnd, from, to, FlyoutSlider.SlideOutMs, () =>
            {
                _chrome.HideDirect();
                VolumeFlyoutGroup.OnAnyHidden();          // 都收完了才放开边条
            }, easeIn: true);
        }
        catch
        {
            _chrome.HideDirect();
            VolumeFlyoutGroup.OnAnyHidden();
        }
    }

    /// <summary>
    /// 已经开着的时候换 target（音量 ↔ 亮度）：面板高度可能不一样（亮度少一颗键），
    /// 所以按新内容重新量一遍尺寸再挪过去 —— 不重播滑入动画，免得看着像"闪了两下"。
    /// </summary>
    private void Reposition()
    {
        try
        {
            var scale = _chrome.Scale;
            var work = WorkArea();

            var dip = SizeDip();
            var w = (int)Math.Round(dip.X * scale);
            var h = (int)Math.Round(dip.Y * scale);

            // 锚点 = 边条（挨着它长）；边条拿不到就当成贴屏幕边。
            // ⚠️ 钉住机制已删（2026-10-03）：换目标时一律贴回边条旁边。
            var anchor = ToolSidebarWindow.CurrentRect ?? EdgeGeometry.EdgeBar(Edge, work);
            var (_, final) = EdgeGeometry.BesideAnchor(Edge, anchor, work, w, h, scale);
            _finalRect = new PixelRect(final.X, final.Y, w, h);

            _chrome.Present(final, w, h);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("浮窗改尺寸失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 按当前 target 摆「文案 / 图标 / 按钮」：
    ///   音量 → 第一颗键是静音（\uE74F），第二颗键（展开合成器）露面；
    ///   亮度 → 第一颗键是自动亮度（\uE706），第二颗键收起来（亮度没有二级浮窗）。
    /// </summary>
    private void ApplyTarget()
    {
        try
        {
            var volume = _target == Target.Volume;

            ActionToggle.IsChecked = false;
            _chrome.Title = volume ? "音量" : "屏幕亮度";
            _adaptiveOn = null;                          // 换了目标：自动亮度状态重新读一遍

            // 图标 / 文字都跟着目标换（2026-10-02 重做：按钮里现在是「图标 + 文字」并排，
            // 不再是过去那种只有一个字形、看不出含义的方块）
            MasterIcon.Glyph = volume ? "\uE767" : "\uE706";
            ActionIcon.Glyph = volume ? "\uE74F" : "\uE706";
            ActionText.Text = volume ? "静音" : "自动亮度";

            UpdateActionTooltip();

            ExpandToggle.IsVisible = volume;
            ApplyPollInterval();

            // ⚠️ 亮度**没有**二级浮窗：切到亮度时要把已经开着的合成器收掉。
            //    不然面板显示亮度、旁边还挂着一个列各应用音量的窗，
            //    而且那个窗的位置是按旧尺寸算的（2026-09-27 修）。
            if (!volume) VolumeMixerWindow.CloseIfOpen();

            // ⚠️ 切目标后**立刻**把数值刷成新目标的（2026-10-02 实机截图发现）：
            //    ApplyTarget 只改了图标和按钮，标题/百分比还留在上一个目标上，
            //    要等下一次轮询才纠正 —— 亮度那条轮询是 3 秒一次，用户会看到
            //    "点的是亮度、面板上写着主音量 100%"整整三秒。开着窗切目标时补刷一次。
            if (_started) Refresh();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("浮窗换目标失败: " + ex.Message);
        }
    }

    /// <summary>第一颗键的悬停提示：音量固定一句话；亮度按「有没有传感器 / 上次有没有改失败」说清楚。</summary>
    private void UpdateActionTooltip(string? error = null)
    {
        if (_target == Target.Volume)
        {
            ToolTip.SetTip(ActionToggle, "静音 / 取消静音");
            return;
        }

        ToolTip.SetTip(ActionToggle, error ?? (BrightnessService.AdaptiveSupported
            ? "自动亮度（跟随环境光调节）"
            : "自动亮度不可用：设备不具备环境光传感器"));
    }

    /// <summary>两个 ToggleButton 按真实状态点亮（静音中 / 合成器开着）。</summary>
    private void SyncToggles()
    {
        if (AudioService.TryGetMaster(out _, out var muted)) ActionToggle.IsChecked = muted;
        ExpandToggle.IsChecked = VolumeMixerWindow.IsVisible;
    }

    // ── 轮询 ──────────────────────────────────────────────────────────────

    private void Start()
    {
        _started = true;
        Refresh();

        _poll ??= new DispatcherTimer();
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();

        ApplyPollInterval();
    }

    /// <summary>
    /// ⚠️ 两条路的节奏必须分开（2026-09-27）：音量是 Core Audio 内存调用，1.5 秒轮一次很便宜；
    ///    亮度要过 WMI、自动亮度还要起 powercfg 进程，按 1.5 秒压在 UI 线程上就是周期性卡顿。
    /// </summary>
    private void ApplyPollInterval()
    {
        if (_poll is null) return;
        _poll.Interval = _target == Target.Volume
            ? TimeSpan.FromMilliseconds(1500)
            : TimeSpan.FromMilliseconds(3000);
    }

    private void Stop()
    {
        _started = false;
        _poll?.Stop();
        _brightnessReading = false;      // 万一半路收窗，别把"正在读"的闸门永久卡住
    }

    private void OnPoll(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (!_started || _dragging) return;
        if (_target == Target.Volume) RefreshVolume();
        else RefreshBrightness();
    }

    /// <summary>主音量：读不到设备（没声卡 / 音频服务停了）就**如实说**，别装成 0%。</summary>
    private void RefreshVolume()
    {
        if (!AudioService.TryGetMaster(out var percent, out var muted))
        {
            MasterSlider.IsEnabled = false;
            ActionToggle.IsEnabled = false;
            MasterPercentText.Text = "—";
            MasterCaptionText.Text = "主音量 · 无设备";
            ToolTip.SetTip(MasterCaptionText, "未检测到音量设备（无音频设备或音频服务未启动）");
            ActionToggle.IsChecked = false;
            return;
        }

        MasterSlider.IsEnabled = true;
        ActionToggle.IsEnabled = true;
        MasterCaptionText.Text = "主音量";
        ToolTip.SetTip(MasterCaptionText, null);

        SetSliderSilently(percent);
        MasterPercentText.Text = percent + "%";
        MasterPercentText.Opacity = muted ? 0.45 : 1.0;

        SyncToggles();
    }

    /// <summary>
    /// 屏幕亮度：笔记本内屏能调，**外接显示器基本调不了**（系统亮度接口不管它）→ 读不到就如实说。
    /// 读数本身在后台线程（<see cref="KickBrightnessRead"/>），这里只负责把已有结果刷到界面上。
    /// </summary>
    private void RefreshBrightness()
    {
        ApplyBrightnessUi();
        KickBrightnessRead();
    }

    /// <summary>把缓存的亮度值刷到界面（读不到就明确写「读不到设备」，绝不假装 0%）。</summary>
    private void ApplyBrightnessUi()
    {
        if (!_brightnessReadable || _brightnessPercent < 0)
        {
            MasterSlider.IsEnabled = false;
            ActionToggle.IsEnabled = false;
            MasterPercentText.Text = "—";
            MasterCaptionText.Text = "屏幕亮度 · 不可调";
            ToolTip.SetTip(MasterCaptionText,
                "这台机器调不了屏幕亮度：显示器不受系统管理（外接屏 / 虚拟机 / 远程桌面），软件调光也不可用");
            ActionToggle.IsChecked = false;
            return;
        }

        MasterSlider.IsEnabled = true;
        ActionToggle.IsEnabled = BrightnessService.AdaptiveSupported;

        // ⚠️ 物理接口读不到、走的是软件 gamma 时如实说明（2026-10-02）：
        //    用户按着滑块看到画面变暗了，得知道那是"软件压暗"而不是背光真的降了，
        //    否则换台机器发现效果不一样会更困惑。
        var software = !BrightnessService.PhysicalAvailable;
        MasterCaptionText.Text = software ? "屏幕亮度（软件调节）" : "屏幕亮度";
        ToolTip.SetTip(MasterCaptionText, software
            ? "该显示器亮度不受系统管理，这里通过显卡 gamma 压暗画面（非背光调节，亮部会一起变暗）"
            : null);

        SetSliderSilently(_brightnessPercent);
        MasterPercentText.Text = _brightnessPercent + "%";
        MasterPercentText.Opacity = 1.0;

        // 状态还不知道时别去动开关：先写个 false 再改回来会看到"闪一下"
        if (_adaptiveOn.HasValue) ActionToggle.IsChecked = _adaptiveOn.Value;
    }

    /// <summary>
    /// 后台读一次「当前亮度」+「自动亮度开关状态」，读完回 UI 线程刷。
    /// 自动亮度只在**还不知道**的时候读 —— 那状态不会自己变，没必要每轮都去起一个 powercfg 进程。
    /// </summary>
    private void KickBrightnessRead()
    {
        if (_brightnessReading) return;
        _brightnessReading = true;

        var needAdaptive = !_adaptiveOn.HasValue;

        _ = Task.Run(() =>
        {
            var ok = BrightnessService.TryGet(out var percent);
            var adaptive = needAdaptive && BrightnessService.TryGetAdaptive(out var on) ? (bool?)on : null;

            // ⚠️ 原版 DispatcherQueue.TryEnqueue → Avalonia 的 Post 没有“投递失败”返回值，语义等价
            Dispatcher.UIThread.Post(() =>
            {
                _brightnessReading = false;
                if (!_started || _target != Target.Brightness) return;

                _brightnessReadable = ok;
                if (ok) _brightnessPercent = percent;
                if (adaptive.HasValue) _adaptiveOn = adaptive;

                ApplyBrightnessUi();
            });
        });
    }

    private void SetSliderSilently(int value)
    {
        if (Math.Abs(MasterSlider.Value - value) < 0.5) return;
        _syncing = true;
        try { MasterSlider.Value = value; }
        finally { _syncing = false; }
    }

    // ── 交互 ──────────────────────────────────────────────────────────────

    private void MasterSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        var percent = (int)Math.Round(e.NewValue);
        MasterPercentText.Text = percent + "%";

        if (_target == Target.Volume)
        {
            AudioService.SetMasterPercent(percent);
            return;
        }

        // ⚠️ 亮度写下去是异步的（50ms 合并窗口 + WMI 延迟），下一次后台读还可能读回旧值；
        //    所以先把缓存改成用户刚设的值，界面就不会"自己弹回去"。
        _brightnessPercent = percent;
        BrightnessService.SetPercent(percent);
    }

    /// <summary>
    /// 第一颗键：音量模式 = 静音开关；亮度模式 = 自动亮度开关。
    /// 改不动（比如电源设置不让改）就把开关弹回原样 —— 界面不能显示一个假的状态。
    /// </summary>
    private void Action_Click(object? sender, RoutedEventArgs e)
    {
        if (_target == Target.Volume)
        {
            if (!AudioService.TryGetMaster(out _, out _)) return;
            AudioService.SetMasterMute(ActionToggle.IsChecked == true);
            Refresh();
            return;
        }

        if (!BrightnessService.AdaptiveSupported)
        {
            ActionToggle.IsChecked = false;
            return;
        }

        var want = ActionToggle.IsChecked == true;
        if (BrightnessService.SetAdaptive(want))
        {
            _adaptiveOn = want;                  // SetAdaptive 内部已经复核过，直接记住
            UpdateActionTooltip();               // ⚠️ 把上次失败留下的那条提示清掉，否则会一直挂着
        }
        else
        {
            ActionToggle.IsChecked = !want;
            UpdateActionTooltip("无法修改自动亮度（可能需要管理员权限，或设备不支持）");
        }
    }

    /// <summary>展开 / 收起合成器浮窗（它贴着本窗往屏幕里侧排）。亮度模式下这颗键藏着，点不到。</summary>
    private void Expand_Click(object? sender, RoutedEventArgs e)
    {
        VolumeMixerWindow.Toggle();
        ExpandToggle.IsChecked = VolumeMixerWindow.IsVisible;
    }
}
