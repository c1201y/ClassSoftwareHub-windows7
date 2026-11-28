using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.VirtualKeyboard;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 自绘虚拟键盘的窗口。
///
/// <b>设计原则</b>：
///   · **不是"模仿"原生，是直接用系统控件** —— 键帽一律是真的 <see cref="Button"/> /
///     <see cref="ToggleButton"/>，**不设 <c>Style</c>、不设任何颜色**，模板 / 圆角 / 悬停 / 按下 /
///     焦点全交给系统；窗口底色走主题画刷。不自行叠加样式与配色，保持系统控件观感。
///   · **零侵入**：不杀任何系统进程、不装常驻钩子（钩子归 <see cref="TouchWatcher"/> 管，
///     只在功能启用期间活着）；注册表只在用户显式打开「接管系统触摸键盘」时才动。
///
/// <b>焦点机制</b>（硬性约束）：
///   键盘窗口**绝不能加 <c>WS_EX_NOACTIVATE</c>** —— 窗口未激活时部分框架**完全不派发指针事件**，
///   键帽收不到"按下"（能显示、命中测试也对，就是不响应）。
///   所以：只加 <c>WS_EX_TOOLWINDOW</c>，显示走 <c>SW_SHOWNOACTIVATE</c>；
///   用户点键必然把前台抢过来，于是**送键之前先把前台还给用户最近在用的那个窗口**
///   （见 <see cref="_target"/>）。
///
/// ⚠️ 移植说明：原版内联的 Win32 DllImport 按契约收进 Platform（NativeMethods /
/// NativeMethodsEx / NativeMethodsUtil）；窗口摆位用 SetWindowPos 按物理像素执行，
/// 与原版 AppWindow.MoveAndResize 等价。对外静态契约（ShowKeyboard / HideKeyboard /
/// Toggle / ApplySettings / RefreshLook / IsPointInside / ShownByUser / IsVisibleNow）逐一保留。
/// </summary>
public sealed partial class VirtualKeyboardWindow : Window
{
    // ── 尺寸（DIP）──────────────────────────────────────────
    private const double ToolbarDip = 34;
    private const double KeyHeightDip = 44;
    private const double KeyGapDip = 4;
    private const double KeyAreaBottomDip = 8;
    private const double KeyCornerDip = 4;
    private const double CaptionFontDip = 15;
    private const double TopFontDip = 10;

    private static VirtualKeyboardWindow? _instance;

    private readonly List<KeyPad> _pads = new();
    private readonly DispatcherTimer _targetTimer;

    private string _mode = KeyboardLayouts.ModeFull;
    private string _layer = KeyboardLayouts.Letters;

    private bool _shift;
    private bool _caps;
    private ushort _stickyMod;

    private bool _visible;

    /// <summary>圆角（Win11 走 DWM，Win7/10 走窗口区域裁剪）。键盘宽度/字号会变 → 挂在 Resized 上跟着重算。</summary>
    private RoundedCorners? _corners;

    /// <summary>用户「最近在用的那个别人的窗口」—— 送键前把前台还给它。</summary>
    private IntPtr _target;

    private int _targetWarnings;
    private int _restoreWarnings;

    /// <summary>本轮显示由用户主动打开（非触摸自动弹出）——主动打开的不自动收起。</summary>
    public static bool ShownByUser { get; private set; }

    /// <summary>本次显示已经记过一条"指针按下"诊断（每轮只记一条，免得刷日志）。</summary>
    private bool _pressLogged;

    /// <summary>
    /// 自绘窗口框架：顶栏当标题栏（系统拖动）、四周 6dip 当缩放带。
    /// 有了它就不用再自己 SendMessage(WM_NCLBUTTONDOWN) —— 那条路会漏掉 PointerReleased、
    /// 把 Avalonia 的指针捕获留在工具栏上（2026-11-27 排查的"键帽全失效"）。
    /// </summary>
    private SelfDrawnFrame? _frame;

    /// <summary>拖动/缩放结束后把新的位置与尺寸写回设置（防抖，别每帧写盘）。</summary>
    private DispatcherTimer? _persistTimer;

    public static bool IsVisibleNow => _instance is not null && _instance._visible;

    public static IntPtr Handle => _instance?.HwndNow() ?? IntPtr.Zero;

    private VirtualKeyboardWindow()
    {
        InitializeComponent();

        var settings = App.Settings.Current;
        _mode = KeyboardLayouts.NormalizeMode(settings.VirtualKeyboardMode);
        _layer = settings.VirtualKeyboardLayer is KeyboardLayouts.Symbols or KeyboardLayouts.Numpad
            ? settings.VirtualKeyboardLayer
            : KeyboardLayouts.Letters;

        ShowActivated = false;                          // 显示这一下**不抢**用户的前台

        _targetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _targetTimer.Tick += (_, _) => TrackTarget();

        // Alt+F4 / 任务栏关闭会真的销毁平台窗口：单例若仍指向已销毁对象，
        //    之后每次显示都打在不存在的句柄上，键盘将无法再弹出（只能重启应用）。
        //    关闭时把单例放掉，下一次 Ensure() 重新建一个。
        Closed += (_, _) =>
        {
            _targetTimer.Stop();
            _visible = false;
            ShownByUser = false;
            if (ReferenceEquals(_instance, this)) _instance = null;
        };

        ApplyTheme();
        BuildKeys();
        RefreshToolbar();

        AttachFrame();

        // 拖动 / 缩放走的是**系统**循环（WM_NCLBUTTONDOWN / HTxxx），结束时既没有
        // Avalonia 的 PointerReleased 也没有"拖完了"回调 —— 只能靠窗口自己报：
        //   PositionChanged（拖）与 Resized（缩放）各自防抖一次，落盘位置与尺寸。
        _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _persistTimer.Tick += (_, _) => { _persistTimer?.Stop(); PersistGeometry(); };
        try
        {
            PositionChanged += (_, _) => QueuePersist();
            Resized += (_, _) => QueuePersist();
        }
        catch { }

        // 诊断（隧道阶段，先于键帽拿到事件）：能区分「窗口根本没收到鼠标」和
        // 「收到了但没触发键」。每轮显示只记一条，正常使用不会刷日志。
        try
        {
            Root.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (_pressLogged) return;
                _pressLogged = true;
                try
                {
                    var p = e.GetPosition(Root);
                    VkbdLog.Write($"收到指针按下｜{p.X:0},{p.Y:0}｜指针={e.Pointer.Type}｜窗口={HwndNow()}");
                }
                catch { }
            }, RoutingStrategies.Tunnel);
        }
        catch { }
    }

    /// <summary>当前原生句柄（窗口没 Show 之前也可能拿得到，拿不到给 Zero）。</summary>
    private IntPtr HwndNow() => Backdrop.TryGetHwnd(this);

    // ── 对外入口 ────────────────────────────────────────────

    /// <summary>把键盘摆出来（已经在显示就什么都不做 —— 自动弹出有两条来路，常前后脚到）。</summary>
    public static void ShowKeyboard(bool userInitiated)
    {
        try
        {
            Ensure().ShowInternal(userInitiated);
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 显示键盘失败：" + ex.Message);
        }
    }

    public static void HideKeyboard()
    {
        try
        {
            _instance?.HideInternal();
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 收起键盘失败：" + ex.Message);
        }
    }

    /// <summary>侧边栏「打开键盘」按一下：显示中就收，收起中就开。</summary>
    public static void Toggle()
    {
        if (IsVisibleNow) HideKeyboard();
        else ShowKeyboard(userInitiated: true);
    }

    /// <summary>用户在设置里改了外观 / 布局 —— 立刻刷一遍。</summary>
    public static void ApplySettings()
    {
        var inst = _instance;
        if (inst is null || !inst._visible) return;

        try
        {
            var settings = App.Settings.Current;
            inst._mode = KeyboardLayouts.NormalizeMode(settings.VirtualKeyboardMode);
            inst._shift = false;
            inst._caps = false;
            inst._stickyMod = 0;

            inst.ApplyTheme();
            inst.BuildKeys();
            inst.RefreshToolbar();
            inst.ApplyBounds();
            var hwnd = inst.HwndNow();
            if (hwnd != IntPtr.Zero)
                WindowChrome.RemoveBorder(hwnd, rounded: true,
                    dark: inst.Root.ActualThemeVariant == ThemeVariant.Dark);
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 刷键盘外观失败：" + ex.Message);
        }
    }

    /// <summary>
    /// **只刷外观**：主题 / 缩放 / 字号 / 宽度 —— 不重建键帽。
    /// 设置页拖滑块走这条（拖一次会触发几十次回调，重建六十多个按钮会明显卡）。
    /// </summary>
    public static void RefreshLook()
    {
        var inst = _instance;
        if (inst is null || !inst._visible) return;

        try
        {
            inst.ApplyTheme();
            inst.ApplyScale();
            inst.RefreshCaptions();
            inst.ApplyBounds();
            var hwnd = inst.HwndNow();
            if (hwnd != IntPtr.Zero)
                WindowChrome.RemoveBorder(hwnd, rounded: true,
                    dark: inst.Root.ActualThemeVariant == ThemeVariant.Dark);
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 刷键盘外观失败：" + ex.Message);
        }
    }

    /// <summary>这个物理像素点是不是落在键盘窗口身上（落点判定要排除它，否则一按就自收）。</summary>
    public static bool IsPointInside(int x, int y)
    {
        var inst = _instance;
        if (inst is null || !inst._visible) return false;

        try
        {
            var hwnd = inst.HwndNow();
            if (hwnd == IntPtr.Zero) return false;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
            return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
        }
        catch
        {
            return false;
        }
    }

    // ── 显示 / 收起 ─────────────────────────────────────────

    private static VirtualKeyboardWindow Ensure()
    {
        if (_instance is null) _instance = new VirtualKeyboardWindow();
        return _instance;
    }

    private void ShowInternal(bool userInitiated)
    {
        // 幂等：自动弹出有两条来路（落点判定 / 用户在别的输入框再点一下），常前后脚到，
        //    重复 ShowWindow 会闪一下。
        if (_visible)
        {
            if (userInitiated) ShownByUser = true;
            return;
        }

        ApplyBounds();

        var hwnd = HwndNow();
        _ = Show(hwnd);                              // SW_SHOWNOACTIVATE：显示但不抢前台

        // 首次 Show() 之前窗口还没创建，句柄是 Zero —— 圆角与置顶都得在**显示之后**重取句柄。
        hwnd = HwndNow();

        // 去白边 / 圆角必须在**显示之后**再调一次：DWM 首帧会按系统主题重刷一遍非客户区。
        if (hwnd != IntPtr.Zero)
            WindowChrome.RemoveBorder(hwnd, rounded: true,
                dark: Root.ActualThemeVariant == ThemeVariant.Dark);

        // 逐像素透明：面板底色本身带 alpha（见 ApplyTheme 的 Shell.Background），
        //    窗口得允许"内容 alpha"才能真的透出后面的画面。
        //    Win7 没有透明窗口能力，BackdropTheme 会自己退回实色 —— 不影响可用性。
        try { BackdropTheme.Apply(this, Root, "acrylic", alphaCorners: true); } catch { }
        _frame?.RefreshFrame("虚拟键盘显示");

        // 显示走 SW_SHOWNOACTIVATE（不激活也不置前），摆位用 SetWindowPos(SWP_NOZORDER)（不动 Z 序），
        //    两者叠加时键盘会停在原 Z 序位置上，被全屏窗口盖住。
        //    XAML 的 Topmost 在 Win7 上有时来不及生效（Avalonia 在窗口创建那一刻应用该属性），
        //    这里在显示之后钉一次：TOPMOST + 不移动 / 不改尺寸 / **不激活**。
        //    与"不抢前台"的设计不冲突 —— 置顶管的是 Z 序，激活是另一回事。
        if (hwnd != IntPtr.Zero)
            _ = NativeMethods.SetWindowPos(hwnd, NativeMethodsUtil.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethodsUtil.SWP_NOMOVE | NativeMethodsUtil.SWP_NOSIZE | NativeMethodsUtil.SWP_NOACTIVATE);

        // 圆角区域是按当时的窗口尺寸算死的，而键盘宽度/字号在设置里能改 —— 挂上自动重算，
        // 否则改了宽度之后四角会被按旧尺寸裁歪（见 Core/RoundedCorners）。
        // force=true：Win11 上无边框浮窗 DWM 拒绝给圆角（见 Core/RoundedCorners 类注释）。
        try { _corners ??= RoundedCorners.Attach(this, RoundedCorners.DefaultCornerRadiusDip, force: true); } catch { }
        _corners?.Refresh();

        _visible = true;
        ShownByUser = userInitiated;
        _pressLogged = false;

        TrackTarget();
        _targetTimer.Start();

        VkbdLog.Write($"键盘显示（{(userInitiated ? "用户主动" : "触摸自动")}）");
        if (VkbdLog.Verbose) LogRects();
    }

    /// <summary>SW_SHOWNOACTIVATE 显示（首次走 Avalonia Show() 建内容层，随后切到不激活显示）。</summary>
    private bool Show(IntPtr hwnd)
    {
        // 判断依据必须是 Avalonia 的可见性，不能是"只置真、永不复位"的 _shown：
        //    Hide() 之后 Avalonia 认为窗口不可见（渲染 / 布局停了），此时只发 Win32 ShowWindow
        //    会显示一个不刷新的空壳；Alt+F4 时平台窗口已销毁，ShowWindow 直接打空。
        //    每次交给 Avalonia Show() 自己同步状态，Closed 事件负责放掉单例重建。
        if (!IsVisible)
        {
            try { Show(); }
            catch (InvalidOperationException) { /* 平台窗口已销毁 —— 下面取句柄会拿到 Zero，调用方自然跳过 */ }
        }
        if (hwnd == IntPtr.Zero) return false;
        return NativeMethods.ShowWindow(hwnd, NativeMethodsUtil.SW_SHOWNOACTIVATE);
    }

    private void HideInternal()
    {
        if (!_visible) return;

        _visible = false;
        ShownByUser = false;
        _targetTimer.Stop();

        try { Hide(); } catch { }
        VkbdLog.Write("键盘收起");
    }

    // ── 外观 ────────────────────────────────────────────────

    private void ApplyTheme()
    {
        // Avalonia 里 RequestedThemeVariant 只在 TopLevel / ThemeVariantScope 上生效，
        //    这里直接设在窗口（TopLevel）上，语义与原版 Root.RequestedTheme 相同。
        RequestedThemeVariant = ThemeCompat.Map(App.Settings.Current.VirtualKeyboardTheme);

        // 面板底色：按「键盘透明度」给半透明刷子（2026-11-27 用户要的"透明键盘"）。
        // 键帽保持系统实色 —— 整块都调透明度会把字糊掉，看不清更没法用。
        try
        {
            var opacity = Math.Clamp(App.Settings.Current.VirtualKeyboardOpacity, 0.2, 1.0);
            // 底色直接按深浅色给：读主题画刷要走资源树，深色下取错会变成白雾，不值当。
            var dark = ActualThemeVariant == ThemeVariant.Dark;
            var baseColor = dark ? Color.FromArgb(255, 0x20, 0x20, 0x20) : Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
            Shell.Background = new SolidColorBrush(baseColor, opacity);
        }
        catch { }
    }

    private void RefreshToolbar()
    {
        LayerHint.Text = $"{KeyboardLayouts.ModeName(_mode)} · {KeyboardLayouts.LayerName(_layer)}";

        // 图标表示"点一下会变成什么样"：现在贴靠 → 画"浮出来"；现在悬浮 → 画"吸回边缘"。
        FloatIcon.Glyph = App.Settings.Current.VirtualKeyboardFloating ? "\uE8A0" : "\uE8A7";
    }

    /// <summary>按当前模式 + 层重建整盘键帽。</summary>
    private void BuildKeys()
    {
        var layout = KeyboardLayouts.Get(_mode, _layer);
        var fontScale = App.Settings.Current.VirtualKeyboardFontScale;

        // 先自检列宽：合计不对会让某一行静默留空/错位，肉眼在 15 列里看不出来。
        //    只在有问题时写日志（正常情况不刷）。
        var problems = KeyboardLayouts.Validate();
        if (problems.Count > 0) VkbdLog.Write("⚠️ 布局列宽异常：" + string.Join("；", problems));

        KeyArea.Children.Clear();
        KeyArea.RowDefinitions.Clear();
        KeyArea.ColumnDefinitions.Clear();
        _pads.Clear();

        for (var r = 0; r < layout.RowCount; r++)
            KeyArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyHeightDip * ScaleSetting) });

        for (var c = 0; c < layout.Columns; c++)
            KeyArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var r = 0; r < layout.RowCount; r++)
        {
            var column = 0;
            foreach (var def in layout.Rows[r])
            {
                var span = Math.Max(1, def.Width);

                if (def.Role != KeyRole.Gap)
                {
                    var pad = new KeyPad(this, def, fontScale, KeyHeightDip * ScaleSetting);
                    Grid.SetRow(pad.Element, r);
                    Grid.SetColumn(pad.Element, column);
                    Grid.SetColumnSpan(pad.Element, span);
                    KeyArea.Children.Add(pad.Element);
                    _pads.Add(pad);
                }

                column += span;
            }
        }

        RefreshCaptions();
    }

    private void RefreshCaptions()
    {
        var fontScale = App.Settings.Current.VirtualKeyboardFontScale;
        foreach (var pad in _pads) pad.ApplyState(_shift, _caps, _stickyMod, fontScale);
    }

    /// <summary>只改行高（缩放变了），不重建键帽。</summary>
    private void ApplyScale()
    {
        var height = new GridLength(KeyHeightDip * ScaleSetting);
        foreach (var row in KeyArea.RowDefinitions) row.Height = height;

        // 键帽自身高度也要跟着改（Avalonia 没有 Grid 间距，键帽 Height 是写死的，见 KeyPad）
        var keyHeight = KeyHeightDip * ScaleSetting;
        foreach (var pad in _pads) pad.SetKeyHeight(keyHeight);
    }

    /// <summary>按设置算窗口尺寸和位置（物理像素，与原版 AppWindow.MoveAndResize 同语义）。</summary>
    private void ApplyBounds()
    {
        try
        {
            // 用户正在拖边缘缩放 / 拖顶栏移动时别插手：系统的模态循环结束时窗口才是最终值，
            //    这中间调 SetWindowPos 会把缩放打断（表现为"拉到一半被弹回原尺寸"）。
            if (_frame?.InSizeMove == true) return;

            var settings = App.Settings.Current;
            var layout = KeyboardLayouts.Get(_mode, _layer);
            var scale = ScaleSetting;
            var dpi = DpiScale;

            var keyHeight = KeyHeightDip * scale;
            var heightDip = ToolbarDip
                + layout.RowCount * keyHeight
                + (layout.RowCount - 1) * KeyGapDip
                + KeyAreaBottomDip;

            var work = WorkArea();

            var widthDip = work.Width / dpi * settings.VirtualKeyboardWidthRatio;
            var w = (int)Math.Round(widthDip * dpi);
            var h = (int)Math.Round(heightDip * dpi);

            w = Math.Clamp(w, 320, Math.Max(320, work.Width));
            h = Math.Clamp(h, 120, Math.Max(120, work.Height));

            int x, y;
            if (settings.VirtualKeyboardFloating
                && settings.VirtualKeyboardFloatX >= 0
                && settings.VirtualKeyboardFloatY >= 0)
            {
                x = (int)Math.Round(settings.VirtualKeyboardFloatX);
                y = (int)Math.Round(settings.VirtualKeyboardFloatY);
            }
            else if (settings.VirtualKeyboardFloating)
            {
                // ⚠️ 2026-11-27：**悬浮但坐标没存成**（-1 / 负数，历史存档里就有 FloatX=-3 这种）。
                //    老代码在这一支里默默退回"贴底居中"，于是设置里显示是悬浮、键盘却贴着底边不动 ——
                //    用户以为"脱离锁定了"，其实位置根本没变。现在改成：按窗口**当前所在位置**当悬浮点，
                //    并把坐标补写回设置，下一次就是真正按悬浮位置摆。
                var cur = HwndNow();
                if (cur != IntPtr.Zero && NativeMethods.GetWindowRect(cur, out var cr))
                {
                    x = cr.Left;
                    y = cr.Top;
                    settings.VirtualKeyboardFloatX = x;
                    settings.VirtualKeyboardFloatY = y;
                    App.Settings.Save();
                    VkbdLog.Write($"悬浮坐标缺失，按当前位置补记（{x},{y}）");
                }
                else
                {
                    x = work.X + (work.Width - w) / 2;
                    y = work.Y + work.Height - h;
                }
            }
            else
            {
                x = work.X + (work.Width - w) / 2;
                y = work.Y + work.Height - h;      // 工作区矩形没有 Bottom，得自己加
            }

            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - w));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - h));

            var hwnd = HwndNow();
            if (hwnd != IntPtr.Zero)
                _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                    NativeMethodsUtil.SWP_NOZORDER);

            VkbdLog.Detail($"键盘窗口 屏幕rect={x},{y} {w}x{h}");
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 摆键盘位置失败：" + ex.Message);
        }
    }

    /// <summary>键盘所在屏幕的工作区（物理像素）；拿不到时退主屏 / 虚拟屏。</summary>
    private PixelRect WorkArea()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this)
                         ?? Screens.All.FirstOrDefault(s => s.IsPrimary)
                         ?? Screens.All.FirstOrDefault();
            if (screen is not null) return screen.WorkingArea;
        }
        catch { }

        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new PixelRect(vx, vy, Math.Max(1, vw), Math.Max(1, vh));
    }

    private double ScaleSetting => Math.Clamp(App.Settings.Current.VirtualKeyboardScale, 0.7, 1.6);

    private double DpiScale
    {
        get
        {
            try
            {
                if (RenderScaling > 0) return RenderScaling;
            }
            catch { }
            // GetDpiForWindow 是 Win10 1607+ 的入口点，Win7/8 的 user32 里**没有这个函数**，
            //    直接调用会抛 EntryPointNotFoundException。必须按系统版本挡一道，
            //    否则虚拟键盘窗在 Win7 上一开就崩。
            if (Platform.OsInfo.IsWindows10OrLater)
            {
                try
                {
                    var dpi = NativeMethodsUtil.GetDpiForWindow(HwndNow());
                    if (dpi > 0) return dpi / 96.0;
                }
                catch { }
            }
            return 1.0;
        }
    }

    // ── 按键 ────────────────────────────────────────────────

    internal void HandleKey(KeyDef def)
    {
        switch (def.Role)
        {
            case KeyRole.Layer:
                _layer = def.Target;
                App.Settings.Current.VirtualKeyboardLayer = _layer;
                App.Settings.Save();
                BuildKeys();
                RefreshToolbar();
                ApplyBounds();
                return;

            case KeyRole.Hide:
                HideInternal();
                return;

            case KeyRole.Shift:
                _shift = !_shift;
                RefreshCaptions();
                return;

            case KeyRole.Caps:
                _caps = !_caps;
                RefreshCaptions();
                return;

            case KeyRole.Modifier:
                _stickyMod = _stickyMod == def.Vk ? (ushort)0 : def.Vk;
                RefreshCaptions();
                return;
        }

        // 真要送字了。
        // 顺序不能反：点我们这一下已经把前台抢过来了（没有 NOACTIVATE 就躲不掉），
        //    必须先把前台还给用户原来那个窗口，紧接着送出的键才会落到它的输入框里。
        if (!RestoreTarget() && _restoreWarnings++ == 0)
            VkbdLog.Write("⚠️ 还前台没成功，这一下可能没送进输入框");

        var shifted = _shift || (_caps && KeyCode.IsLetter(def.Vk)) || def.Shifted;
        var modifier = _stickyMod;

        if (modifier != 0) KeyInjector.Chord(modifier, def.Vk, shifted);
        else KeyInjector.Tap(def.Vk, shifted);

        VkbdLog.Detail($"送键 {def.Accessible} vk=0x{def.Vk:X2} shift={shifted} mod=0x{modifier:X2}");

        // 一次性状态用掉就清
        _stickyMod = 0;
        _shift = false;
        RefreshCaptions();
    }

    // ── 前台目标跟踪 ────────────────────────────────────────

    private void TrackTarget()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;
        if (foreground == HwndNow()) return;             // 键盘自己不算
        if (!NativeMethods.IsWindowVisible(foreground)) return;   // 收进托盘的主窗口也不算

        _target = foreground;
    }

    private bool RestoreTarget()
    {
        if (_target == IntPtr.Zero || _target == HwndNow()) return false;
        if (!NativeMethodsUtil.IsWindow(_target)) return false;

        if (NativeMethods.GetForegroundWindow() == _target) return true;
        if (NativeMethods.SetForegroundWindow(_target)) return true;

        if (_targetWarnings++ == 0) VkbdLog.Write("⚠️ 还没有可还原的目标窗口");
        return false;
    }

    // ── 顶栏交互 ────────────────────────────────────────────

    /// <summary>
    /// 挂自绘窗口框架：顶栏当标题栏（系统拖动 + 双击/贴边）、四周 6dip 当缩放带。
    /// <para>
    /// ⚠️ 2026-11-27：以前是自己 <c>SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)</c> 手搓拖动，
    ///    系统模态循环结束后 Avalonia 收不到 PointerReleased，指针捕获留在工具栏上 →
    ///    之后整块键盘的点击都被路由回工具栏（键帽看起来"全部失效"）。
    ///    交给框架后走的是系统的非客户区命中，Avalonia 的指针状态始终是干净的。
    /// </para>
    /// </summary>
    private void AttachFrame()
    {
        try
        {
            _frame = SelfDrawnFrame.Attach(this);
            _frame.ResizeEnabled = true;
            _frame.ResizeBorder = 6;
            _frame.TitleBarHeight = ToolbarDip;          // 34dip：整条顶栏都能拖
            _frame.InteractiveRegions = ToolbarInteractiveRegions;
            _frame.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(Root);
        }
        catch (Exception ex)
        {
            _frame = null;
            VkbdLog.Write("⚠️ 挂自绘窗口框架失败（退回无拖动/无缩放）：" + ex.Message);
        }
    }

    /// <summary>顶栏上的两颗图标按钮要报给框架，否则它们会被当成标题栏（点了变拖窗口）。</summary>
    private IReadOnlyList<Rect> ToolbarInteractiveRegions()
    {
        var list = new List<Rect>(2);
        AddRegion(list, FloatButton);
        AddRegion(list, HideButton);
        return list;
    }

    private void AddRegion(List<Rect> list, Control button)
    {
        try
        {
            if (button.IsVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0
                && button.TranslatePoint(new Point(0, 0), this) is { } p)
                list.Add(new Rect(p.X, p.Y, button.Bounds.Width, button.Bounds.Height));
        }
        catch { }
    }

    /// <summary>拖动/缩放中会连续触发 —— 防抖，停下来 400ms 再落盘一次。</summary>
    private void QueuePersist()
    {
        try
        {
            _persistTimer?.Stop();
            _persistTimer?.Start();
        }
        catch { }
    }

    /// <summary>
    /// 把窗口当前的真实位置/尺寸折算回设置：
    ///   高度 → 键高缩放（VirtualKeyboardScale）；宽度 → 占工作区比例（VirtualKeyboardWidthRatio）；
    ///   悬浮时顺手记坐标。用户拖边缘缩放手感是"自由缩放"，落盘后下次打开还是这个大小。
    /// </summary>
    private void PersistGeometry()
    {
        try
        {
            var hwnd = HwndNow();
            if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var r)) return;
            if (r.Width <= 0 || r.Height <= 0) return;

            var settings = App.Settings.Current;
            var work = WorkArea();
            var dpi = DpiScale;
            var layout = KeyboardLayouts.Get(_mode, _layer);

            var ratio = Math.Clamp((double)r.Width / Math.Max(1, work.Width), 0.35, 1.0);

            var heightDip = r.Height / dpi;
            var inner = heightDip - ToolbarDip - KeyAreaBottomDip - (layout.RowCount - 1) * KeyGapDip;
            var keyHeight = layout.RowCount > 0 ? inner / layout.RowCount : KeyHeightDip;
            var scale = Math.Clamp(keyHeight / KeyHeightDip, 0.7, 1.6);

            settings.VirtualKeyboardWidthRatio = Math.Round(ratio, 3);
            settings.VirtualKeyboardScale = Math.Round(scale, 2);

            // 贴靠状态下把键盘拖走了 = 用户想自由摆位（老行为：拖一下即成悬浮）。
            if (!settings.VirtualKeyboardFloating)
            {
                var dockedY = work.Y + work.Height - r.Height;
                var dockedX = work.X + (work.Width - r.Width) / 2;
                if (Math.Abs(r.Top - dockedY) > 8 || Math.Abs(r.Left - dockedX) > 8)
                {
                    settings.VirtualKeyboardFloating = true;
                    VkbdLog.Write($"键盘从贴靠拖成悬浮（{r.Left},{r.Top}）");
                }
            }

            if (settings.VirtualKeyboardFloating)
            {
                settings.VirtualKeyboardFloatX = r.Left;
                settings.VirtualKeyboardFloatY = r.Top;
            }

            App.Settings.Save();
            ApplyScale();
            RefreshCaptions();
            VkbdLog.Write($"键盘尺寸已记：{r.Width}x{r.Height}px｜缩放={settings.VirtualKeyboardScale}｜宽度比={settings.VirtualKeyboardWidthRatio}");
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 记录键盘位置/尺寸失败：" + ex.Message);
        }
    }

    private void Hide_Click(object? sender, RoutedEventArgs e) => HideInternal();

    private void Float_Click(object? sender, RoutedEventArgs e)
    {
        var settings = App.Settings.Current;
        settings.VirtualKeyboardFloating = !settings.VirtualKeyboardFloating;

        var hwnd = HwndNow();
        if (settings.VirtualKeyboardFloating)
        {
            var pos = PixelPoint.Origin;
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
                pos = new PixelPoint(r.Left, r.Top);
            settings.VirtualKeyboardFloatX = pos.X;
            settings.VirtualKeyboardFloatY = pos.Y;
            VkbdLog.Write($"键盘切到悬浮（{pos.X},{pos.Y}）");
        }
        else
        {
            VkbdLog.Write("键盘切到贴靠底边");
        }

        App.Settings.Save();
        ApplyBounds();

        // 切完模式把"窗口外观"重新声明一遍：位置/尺寸变了，圆角和置顶都可能被系统重置，
        //    而这一步以前只在"显示"时做过 —— 悬浮之后变直角 / 被别的窗口压住就是这个原因。
        var hwndAfter = HwndNow();
        if (hwndAfter != IntPtr.Zero)
        {
            try
            {
                WindowChrome.RemoveBorder(hwndAfter, rounded: true,
                    dark: Root.ActualThemeVariant == ThemeVariant.Dark);
            }
            catch { }
            _corners?.Refresh();
            _ = NativeMethods.SetWindowPos(hwndAfter, NativeMethodsUtil.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethodsUtil.SWP_NOMOVE | NativeMethodsUtil.SWP_NOSIZE | NativeMethodsUtil.SWP_NOACTIVATE);
        }
        try { Root.InvalidateVisual(); } catch { }
        RefreshToolbar();
    }

    /// <summary>排障用：把每个键帽的屏幕位置打出来（自动验证脚本靠它反推点击坐标）。</summary>
    private void LogRects()
    {
        try
        {
            var hwnd = HwndNow();
            if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var wr)) return;
            var origin = new PixelPoint(wr.Left, wr.Top);
            var scale = DpiScale;
            foreach (var pad in _pads)
            {
                var fe = pad.Element;
                if (fe.Bounds.Width <= 0) continue;

                var matrix = fe.TransformToVisual(Root);
                var t = (matrix ?? Matrix.Identity).Transform(new Point(0, 0));
                VkbdLog.Detail($"键 '{pad.Def.Accessible}' 屏幕rect=" +
                    $"{origin.X + (int)Math.Round(t.X * scale)},{origin.Y + (int)Math.Round(t.Y * scale)} " +
                    $"{(int)Math.Round(fe.Bounds.Width * scale)}x{(int)Math.Round(fe.Bounds.Height * scale)}");
            }
        }
        catch (Exception ex)
        {
            VkbdLog.Write("⚠️ 打印键帽位置失败：" + ex.Message);
        }
    }

    // ── 键帽 ────────────────────────────────────────────────

    /// <summary>
    /// 一个键帽。**就是一颗真的系统按钮**（可锁定的用 <see cref="ToggleButton"/>）——
    /// 不设 <c>Style</c>、不设任何颜色，模板 / 圆角 / 悬停 / 按下 / 焦点全由系统给。
    /// Avalonia 没有 WPF 那个 ButtonBase（ToggleButton 直接继承 Button），字段类型用 Button。
    /// </summary>
    private sealed class KeyPad
    {
        private readonly VirtualKeyboardWindow _owner;
        private readonly Button _button;
        private readonly TextBlock _caption;
        private readonly TextBlock? _top;

        public KeyDef Def { get; }

        public Control Element => _button;

        public KeyPad(VirtualKeyboardWindow owner, KeyDef def, double fontScale, double keyHeight)
        {
            _owner = owner;
            Def = def;

            var content = new Grid();

            if (def.Top.Length > 0)
            {
                // 精简模式把数字印在字母键的上方 —— 系统那个键盘就是这么干的。
                _top = new TextBlock
                {
                    Text = def.Top,
                    FontSize = TopFontDip * fontScale,
                    Opacity = 0.6,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 0, 0),
                    IsHitTestVisible = false,
                };
                content.Children.Add(_top);
            }

            _caption = new TextBlock
            {
                Text = def.Caption(false),
                FontSize = CaptionFontDip * fontScale,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            content.Children.Add(_caption);

            var latchable = def.Role is KeyRole.Modifier or KeyRole.Shift or KeyRole.Caps;
            _button = latchable ? new ToggleButton() : new Button();

            // 系统 Button 默认 MinWidth 很大。键帽是 Star 分列的，不清零整盘会被撑爆。
            _button.MinWidth = 0;
            _button.MinHeight = 0;
            _button.Padding = new Thickness(0);
            _button.Margin = new Thickness(0, 0, KeyGapDip, KeyGapDip);   // 代替原版 Grid 的行/列间距
            _button.Height = keyHeight;
            _button.CornerRadius = new CornerRadius(KeyCornerDip);
            _button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            _button.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            _button.Content = content;
            _button.Click += OnClick;

            // 无障碍名 —— 自动验证脚本靠它认键。
            Avalonia.Automation.AutomationProperties.SetName(_button, def.Accessible);
            Avalonia.Automation.AutomationProperties.SetAutomationId(_button, "kbd-" + def.Accessible);
            ToolTip.SetTip(_button, def.Accessible);
        }

        /// <summary>行高变化时同步键帽自身高度（ApplyScale 调）。</summary>
        public void SetKeyHeight(double height) => _button.Height = height;

        private void OnClick(object? sender, RoutedEventArgs e) => _owner.HandleKey(Def);

        /// <summary>按当前上档 / 粘滞状态刷新显示。可锁定的键帽顺便把点亮态同步给系统模板。</summary>
        public void ApplyState(bool shiftOn, bool capsOn, ushort stickyMod, double fontScale)
        {
            _caption.FontSize = CaptionFontDip * fontScale;
            if (_top is not null) _top.FontSize = TopFontDip * fontScale;

            var upperNow = shiftOn || (capsOn && KeyCode.IsLetter(Def.Vk));
            _caption.Text = Def.Caption(upperNow);

            if (_button is ToggleButton toggle)
            {
                toggle.IsChecked = Def.Role switch
                {
                    KeyRole.Shift => shiftOn,
                    KeyRole.Caps => capsOn,
                    KeyRole.Modifier => stickyMod == Def.Vk,
                    _ => false,
                };
            }
        }
    }
}
