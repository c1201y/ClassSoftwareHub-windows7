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
/// 自绘虚拟键盘的窗口（2026-09-30 从零重写那版删掉之后，2026-10-01 按新方向重做）。
///
/// <b>设计原则（Nick 定的）</b>：
///   · **不是"模仿"原生，是直接用系统控件** —— 键帽一律是真的 <see cref="Button"/> /
///     <see cref="ToggleButton"/>，**不设 <c>Style</c>、不设任何颜色**，模板 / 圆角 / 悬停 / 按下 /
///     焦点全交给系统；窗口底色走主题画刷。上一版自己叠 <c>Border</c> + 写死 RGB + 手搓下沉，
///     就是"粗糙劣质的网页感 / 分层乱七八糟"的字面来源。
///   · **零侵入**：不杀任何系统进程、不装常驻钩子（钩子归 <see cref="TouchWatcher"/> 管，
///     只在功能启用期间活着）；注册表只在用户显式打开「接管系统触摸键盘」时才动。
///
/// <b>焦点机制</b>（⛔ 这一段是实测换来的，别改）：
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

    /// <summary>这一轮显示是用户主动要的（不是触摸自动弹的）——主动开的不自动收。</summary>
    public static bool ShownByUser { get; private set; }

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

        ApplyTheme();
        BuildKeys();
        RefreshToolbar();
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
        // ⚠️ 幂等：自动弹出有两条来路（落点判定 / 用户在别的输入框再点一下），常前后脚到 ——
        //    再来一次 ShowWindow 会闪一下。
        if (_visible)
        {
            if (userInitiated) ShownByUser = true;
            return;
        }

        ApplyBounds();

        var hwnd = HwndNow();
        _ = Show(hwnd);                              // SW_SHOWNOACTIVATE：显示但不抢前台

        // ⛔ 去白边 / 圆角必须在**显示之后**再调一次：DWM 首帧会按系统主题重刷一遍非客户区。
        if (hwnd != IntPtr.Zero)
            WindowChrome.RemoveBorder(hwnd, rounded: true,
                dark: Root.ActualThemeVariant == ThemeVariant.Dark);

        // 圆角区域是按当时的窗口尺寸算死的，而键盘宽度/字号在设置里能改 —— 挂上自动重算，
        // 否则改了宽度之后四角会被按旧尺寸裁歪（见 Core/RoundedCorners）。
        try { _corners ??= RoundedCorners.Attach(this); } catch { }
        _corners?.Refresh();

        _visible = true;
        ShownByUser = userInitiated;

        TrackTarget();
        _targetTimer.Start();

        VkbdLog.Write($"键盘显示（{(userInitiated ? "用户主动" : "触摸自动")}）");
        if (VkbdLog.Verbose) LogRects();
    }

    /// <summary>SW_SHOWNOACTIVATE 显示（首次走 Avalonia Show() 建内容层，随后切到不激活显示）。</summary>
    private bool Show(IntPtr hwnd)
    {
        if (!_shown)
        {
            _shown = true;
            Show();
        }
        if (hwnd == IntPtr.Zero) return false;
        return NativeMethods.ShowWindow(hwnd, NativeMethodsUtil.SW_SHOWNOACTIVATE);
    }

    private bool _shown;

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
        // ⚠️ Avalonia 里 RequestedThemeVariant 只在 TopLevel / ThemeVariantScope 上生效，
        //    这里直接设在窗口（TopLevel）上，语义与原版 Root.RequestedTheme 相同。
        RequestedThemeVariant = ThemeCompat.Map(App.Settings.Current.VirtualKeyboardTheme);
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

        // ⚠️ 先自检列宽：合计不对会让某一行静默留空/错位，肉眼在 15 列里看不出来。
        //    只在有问题时说话（正常情况不刷日志）。
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
            else
            {
                x = work.X + (work.Width - w) / 2;
                y = work.Y + work.Height - h;      // ⚠️ 工作区矩形没有 Bottom，得自己加
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
            // ⚠️ GetDpiForWindow 是 Win10 1607+ 的入口点，Win7/8 上 user32 里**根本没有这个函数**，
            //    直接调会抛 EntryPointNotFoundException。这里必须按系统版本挡一道，否则
            //    虚拟键盘窗在 Win7 上一开就崩（这也是"点了没反应"的常见来源）。
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
        // ⚠️ 顺序不能反：点我们这一下已经把前台抢过来了（没有 NOACTIVATE 就躲不掉），
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

    private void Toolbar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;

        var hwnd = HwndNow();

        // ⛔ 拖动交给系统合成（ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION），
        //    自己算 PointerMoved + Move 会"卡卡的、乱飘"。
        _ = NativeMethods.ReleaseCapture();
        _ = NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCLBUTTONDOWN,
            new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);

        // ⚠️ SendMessage 返回时已经拖完了 —— 新位置要从窗口**读回来**再存。
        var pos = PixelPoint.Origin;
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
            pos = new PixelPoint(r.Left, r.Top);

        var settings = App.Settings.Current;
        var wasDocked = !settings.VirtualKeyboardFloating;

        settings.VirtualKeyboardFloating = true;
        settings.VirtualKeyboardFloatX = pos.X;
        settings.VirtualKeyboardFloatY = pos.Y;
        App.Settings.Save();

        if (wasDocked)
        {
            // 本来就贴着底边，拖一下自然就成"自由摆位"了 —— 不用先去点悬浮按钮。
            VkbdLog.Write($"键盘从贴靠拖成悬浮（{pos.X},{pos.Y}）");
        }

        RefreshToolbar();
        e.Handled = true;
    }

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
        RefreshToolbar();
    }

    private void Hide_Click(object? sender, RoutedEventArgs e) => HideInternal();

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
    /// ⚠️ Avalonia 没有 WPF 那个 ButtonBase（ToggleButton 直接继承 Button），字段类型用 Button。
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

            // ⛔ 系统 Button 默认 MinWidth 很大。键帽是 Star 分列的，不清零整盘会被撑爆。
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
