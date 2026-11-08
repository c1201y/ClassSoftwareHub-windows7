using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 放映时长按屏幕弹出的菜单（2026-11-03，自上游 dv1.2.0-insider1.9 移植）。
///
/// <para>
/// 视觉走本仓库既有的**自绘菜单卡片**（见 <see cref="TrayMenuWindow"/>）：卡片底 + 1px 描边 +
/// 34dip 行高 + 悬停淡底，深色/浅色由代码刷成品色。原版用的是「1×1 宿主窗口 + 系统 MenuFlyout」，
/// Win7 上没有逐像素透明、系统浮层的行为也不可控（原版注释自己记着「铺满全屏会把幻灯片盖黑」），
/// 所以改成独立的自绘小窗。
/// </para>
///
/// <para>
/// ⚠️ 三条规矩（和托盘菜单一致，都是踩过的）：
///   1. 只有一个实例，重复长按先收掉旧的再重摆；
///   2. 窗口**从不 Close**，只 Hide —— 关掉就得重建整棵树；
///   3. 收盘时如果用户是点空白处关的（而不是点了菜单项），要把前台还给 PowerPoint，
///      否则放映窗口会一直停在「被别的窗口盖住」的状态。
/// </para>
///
/// <para>
/// 与原版的一处形态差异：二级菜单（「指针选项」）原本是 MenuFlyout 的子菜单，
/// 这里做成同一张卡片里的第二列（向右展开）。语义完全一致（点开列出五项、点一项执行），
/// 但少了一层独立窗口 —— 钩子那边「点在菜单上就放行」的判定只需要认本进程的窗口，
/// 两层窗口反而多一处可能挡住幻灯片的地方。
/// </para>
/// </summary>
public sealed partial class PptMenuWindow : Window
{
    // ── 尺寸（全部是 dip，和 TrayMenuWindow 同一套口径）────────────────────

    private const double RowDip = 34;
    private const double SeparatorBlockDip = 9;     // 1px 线 + 上下各 4
    private const double PanelPaddingDip = 4;
    private const double BorderDip = 1;
    private const double MainWidthDip = 208;
    private const double SubWidthDip = 168;
    private const double DividerBlockDip = 9;       // 1px 竖线 + 左右各 4

    /// <summary>离工作区边缘留的空隙（物理像素），别让卡片贴着屏幕边像被切掉。</summary>
    private const int EdgeGapPx = 8;

    private static PptMenuWindow? _instance;

    private SelfDrawnFrame? _frame;
    private RoundedCorners? _corners;
    private DispatcherTimer? _watchdog;

    private readonly List<Button> _mainButtons = new();
    private readonly List<Button> _subButtons = new();

    private Button? _selected;
    private PixelPoint _anchor;
    private bool _submenuOpen;
    private bool _visible;
    private bool _closing;

    private PptMenuWindow()
    {
        InitializeComponent();

        Title = "放映菜单";
        ShowInTaskbar = false;          // 不进任务栏、不进 Alt+Tab
        CanResize = false;
        Topmost = true;                 // ⚠️ 必须：放映窗口是全屏的，不置顶就看不见

        // 悬浮件：跟「外部组件外观」（分体关着时跟主界面），与常用工具浮窗同一口径
        ThemeCompat.Apply(Root, external: true);
        Root.ActualThemeVariantChanged += (_, _) => ApplyThemeColors();

        ConfigureFrame();

        Deactivated += OnDeactivated;
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// 自绘窗口框架：保留 <see cref="SelfDrawnFrame"/> 那套（吃掉非客户区、按底色擦背景），
    /// 但**不报标题栏、不报缩放边** —— 菜单不该能拖、能拉。
    ///
    /// <para>
    /// 原版走 <c>AppWindow.Presenter.SetBorderAndTitleBar(false,false)</c> 去掉系统边框；
    /// 移植版在 XAML 里用 <c>SystemDecorations="None"</c> 达成同一件事，
    /// 再挂框架补 DWM 非客户区策略与 Win7 的擦背景（无合成系统上否则会留黑边）。
    /// </para>
    /// </summary>
    private void ConfigureFrame()
    {
        try
        {
            _frame = SelfDrawnFrame.Attach(this);
            _frame.ResizeEnabled = false;
            _frame.TitleBarHeight = 0;
            _frame.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(Root);
        }
        catch (Exception ex)
        {
            _frame = null;
            AppLog.Catch("pptmenu", "挂载自绘窗口框架（已退回纯无边框窗口）", ex, LogLevel.Debug);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  对外入口
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 在屏幕坐标 (x, y) 处弹出菜单。坐标是**物理像素**，直接从钩子拿。
    /// 任何一步失败都只记日志，不抛给钩子线程。
    /// </summary>
    public static void Show(int x, int y)
    {
        try
        {
            if (App.IsExiting) return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                ShowCore(x, y);
                return;
            }

            // 钩子线程叫过来的：切到界面线程再建窗/显示（与上游 TryEnqueue 同义）
            Dispatcher.UIThread.Post(() =>
            {
                try { ShowCore(x, y); }
                catch (Exception ex)
                {
                    AppLog.Exception("pptmenu", ex, "弹出长按菜单");
                    PptMenu.IsOpen = false;
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("pptmenu", ex, "弹出长按菜单");
            PptMenu.IsOpen = false;
        }
    }

    private static void ShowCore(int x, int y)
    {
        try
        {
            _instance ??= new PptMenuWindow();
            _instance.Present(x, y);
        }
        catch (Exception ex)
        {
            AppLog.Exception("pptmenu", ex, "弹出长按菜单");
            PptMenu.IsOpen = false;
        }
    }

    /// <summary>
    /// 从钩子线程叫过来：把菜单收起来。用在「点在菜单外面」那一下。
    /// 真正的收尾（隐藏窗口、把前台还给 PowerPoint）在 <see cref="Dismiss"/> 里。
    /// </summary>
    public static void HideMenu()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _instance?.Dismiss(fromItem: false);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try { _instance?.Dismiss(fromItem: false); }
            catch (Exception ex) { AppLog.Catch("pptmenu", "收起长按菜单", ex, LogLevel.Debug); }
        });
    }

    /// <summary>
    /// 退出应用时**真正销毁**实例 —— 平时「收起」只是 <see cref="Dismiss"/>（Hide），不 Close。
    ///
    /// <para>
    /// ⚠️ 为什么退出流程必须单独叫一声：本窗一旦创建过就一直挂在 Avalonia 的窗口列表里
    /// （即使 Hide 了也算「开着」），而退出流程的 <c>desktop.Shutdown()</c> 实测会漏窗口，
    /// 漏掉的那个会把消息循环撑住，进程赖在任务管理器里不走。
    /// </para>
    /// </summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("window", "窗口关闭请求｜窗口=放映菜单｜触发=程序化 Close");
    }

    // ══════════════════════════════════════════════════════════════════
    //  弹出
    // ══════════════════════════════════════════════════════════════════

    private void Present(int x, int y)
    {
        _anchor = new PixelPoint(x, y);

        // 上一次还开着（连按两下）：先按「收起」处理，再重新铺一遍。
        // ⚠️ 不走 Dismiss —— 那会把前台还给 PowerPoint，紧接着又抢回来，中间会闪。
        _closing = true;
        try { CloseSubmenu(); } catch { }
        _closing = false;

        BuildMenu();

        PptMenu.IsOpen = true;
        PptMenu.OpenedTick = Environment.TickCount64;

        ApplyLayout();

        // 显示前先把成品底色刷上，防首帧「白闪」（Win7 关 Aero 时尤其明显）
        ApplyThemeColors();

        if (!_visible)
        {
            _visible = true;
            Show();
            ApplyChrome();
            PositionAtAnchor();
        }
        else
        {
            ApplyChrome();
            PositionAtAnchor();
        }

        Activate();
        EnsureForeground();
        try { Root.Focus(); } catch { }

        ArmWatchdog();

        AppLog.Info("pptmenu", $"长按菜单已弹出｜位置={_anchor.X},{_anchor.Y}"
                              + $"｜项数={_mainButtons.Count}｜子菜单={(_submenuOpen ? "开" : "关")}");
    }

    /// <summary>
    /// 生成菜单。顺序与内容全部来自 <see cref="PptMenu.Build"/>（唯一真源），
    /// 这里只负责铺成控件。
    /// </summary>
    private void BuildMenu()
    {
        ItemsHost.Children.Clear();
        _mainButtons.Clear();
        _selected = null;

        foreach (var item in PptMenu.Build(PptTouchConfig.Load().MenuExtras))
            AddRow(ItemsHost, item, _mainButtons);
    }

    private void OpenSubmenu(PptMenuItem parent)
    {
        if (parent.Children is not { Count: > 0 } children) return;

        SubHost.Children.Clear();
        _subButtons.Clear();
        _selected = null;

        foreach (var child in children) AddRow(SubHost, child, _subButtons);

        _submenuOpen = true;
        SubHost.IsVisible = true;
        SubDivider.IsVisible = true;

        ApplyLayout();
        PositionAtAnchor();
        AppLog.Info("pptmenu", "二级菜单已展开：" + parent.Text);
    }

    private void CloseSubmenu()
    {
        if (!_submenuOpen) return;

        _submenuOpen = false;
        _selected = null;
        SubHost.Children.Clear();
        _subButtons.Clear();
        SubHost.IsVisible = false;
        SubDivider.IsVisible = false;
    }

    /// <summary>
    /// 把菜单模型铺成按钮。分隔线、子菜单都各自成型（对应原版
    /// <c>MenuFlyoutItem</c> / <c>MenuFlyoutSeparator</c> / <c>MenuFlyoutSubItem</c>）。
    /// </summary>
    private void AddRow(StackPanel host, PptMenuItem item, List<Button> sink)
    {
        if (item.IsSeparator)
        {
            var sep = new Border();
            sep.Classes.Add("pptSeparator");
            sep.PointerEntered += (_, _) => Select(null);   // 挪到分隔线上 → 不高亮任何一项
            host.Children.Add(sep);
            return;
        }

        var btn = new Button
        {
            Content = BuildRow(item),
            Tag = item,
            Focusable = false,          // 焦点留在窗口上（菜单不是表单），键盘选中由代码统一切 class
        };

        if (Resources["PptMenuItemTheme"] is ControlTheme theme) btn.Theme = theme;
        Avalonia.Automation.AutomationProperties.SetName(btn, item.Text);

        btn.PointerEntered += (_, _) => Select(btn);
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            OnItem(item);
        };

        host.Children.Add(btn);
        sink.Add(btn);
    }

    private static Control BuildRow(PptMenuItem item)
    {
        // 固定 18dip 图标列，没有图标的项文字也跟有图标的对齐
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto") };

        if (!string.IsNullOrEmpty(item.Glyph))
        {
            row.Children.Add(new FontIcon
            {
                Glyph = item.Glyph!,
                FontSize = 14,
                Opacity = 0.9,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }

        var text = new TextBlock
        {
            Text = item.Text,
            FontSize = 13,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        // 有子菜单的给一颗右向 chevron（对应 MenuFlyoutSubItem 自带的那颗）
        if (item.Children is { Count: > 0 })
        {
            var chevron = new FontIcon
            {
                Glyph = "\uE76C",
                FontSize = 10,
                Opacity = 0.6,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            Grid.SetColumn(chevron, 2);
            row.Children.Add(chevron);
        }

        return row;
    }

    private void OnItem(PptMenuItem item)
    {
        if (_closing) return;

        // 子菜单项：展开第二列，等用户再点具体一项
        if (item.Children is { Count: > 0 })
        {
            OpenSubmenu(item);
            return;
        }

        Pick(item);
    }

    /// <summary>
    /// 执行一个菜单项：**先收菜单再执行** —— 执行时要把前台还给 PowerPoint，菜单不能压在上面。
    /// </summary>
    private void Pick(PptMenuItem item)
    {
        if (_closing) return;
        _closing = true;

        try { CloseSubmenu(); } catch { }
        StopWatchdog();

        try { Hide(); } catch (Exception ex) { AppLog.Catch("pptmenu", "收起长按菜单", ex, LogLevel.Debug); }
        _visible = false;

        // 「点了菜单项」的标记：Menu_Closed 那条路径据此不把前台还回去（马上要自己切）
        PptMenu.IsOpen = false;

        _closing = false;

        try { PptMenu.Invoke(item.Id); }
        catch (Exception ex) { AppLog.Exception("pptmenu", ex, "执行菜单项 " + item.Id); }
    }

    // ══════════════════════════════════════════════════════════════════
    //  布局 / 定位
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 按当前内容算窗口尺寸（dip）。尺寸必须在 <c>Show</c> 之前定好 ——
    /// 菜单是贴着手指摆的，先显示再改尺寸会看到它跳一下。
    /// </summary>
    private void ApplyLayout()
    {
        var mainH = ContentHeight(ItemsHost);
        var subH = _submenuOpen ? ContentHeight(SubHost) : 0;

        Height = Math.Max(mainH, subH) + PanelPaddingDip * 2 + BorderDip * 2;
        Width = MainWidthDip
                + (_submenuOpen ? DividerBlockDip + SubWidthDip : 0)
                + PanelPaddingDip * 2 + BorderDip * 2;
    }

    private static double ContentHeight(StackPanel host)
    {
        double h = 0;

        foreach (var child in host.Children)
        {
            if (child is Border b && b.Classes.Contains("pptSeparator")) h += SeparatorBlockDip;
            else h += RowDip;
        }

        return h;
    }

    /// <summary>
    /// 贴着手指摆卡片：默认左上角落在手指点，放不下就整体往左/往上推，最后夹进工作区。
    /// （原版是 <c>AppWindow.MoveAndResize(x, y, 1, 1)</c>，菜单浮层自己找位置；
    /// 这里窗口本身就是要显示的内容，得自己算。）
    /// </summary>
    private void PositionAtAnchor()
    {
        try
        {
            var screen = ScreenAt(_anchor);
            var scale = screen?.Scaling ?? (RenderScaling > 0 ? RenderScaling : 1.0);
            if (scale <= 0) scale = 1.0;

            int w = (int)Math.Round(Width * scale);
            int h = (int)Math.Round(Height * scale);

            Position = ComputePosition(_anchor, w, h, screen);

            // 真实像素尺寸可能与按屏幕缩放预算的不一致（便携版启动器拉起时见过），
            // 用 HWND 实测尺寸再摆一次。
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var actual) &&
                (actual.Width != w || actual.Height != h))
            {
                Position = ComputePosition(_anchor, actual.Width, actual.Height, screen);
            }
        }
        catch (Exception ex)
        {
            AppLog.Catch("pptmenu", "定位长按菜单", ex, LogLevel.Debug);
        }
    }

    private static PixelPoint ComputePosition(PixelPoint anchor, int w, int h, Screen? screen)
    {
        int x = anchor.X;
        int y = anchor.Y;

        if (screen is { } sc)
        {
            var work = sc.WorkingArea;

            // 右侧 / 下方放不下 → 往左 / 往上翻，让菜单主体落在手指的另一侧
            if (x + w > work.Right - EdgeGapPx) x = anchor.X - w;
            if (y + h > work.Bottom - EdgeGapPx) y = anchor.Y - h;

            x = Math.Clamp(x, work.X + EdgeGapPx, Math.Max(work.X + EdgeGapPx, work.Right - w - EdgeGapPx));
            y = Math.Clamp(y, work.Y + EdgeGapPx, Math.Max(work.Y + EdgeGapPx, work.Bottom - h - EdgeGapPx));
        }

        return new PixelPoint(x, y);
    }

    private Screen? ScreenAt(PixelPoint p)
    {
        try { return Screens.ScreenFromPoint(p); }
        catch { return null; }
    }

    // ══════════════════════════════════════════════════════════════════
    //  外观
    // ══════════════════════════════════════════════════════════════════

    /// <summary>显示之后才能调（HWND 这时才存在）：刷深浅色 + 按真实尺寸裁圆角。</summary>
    private void ApplyChrome()
    {
        ApplyThemeColors();

        try
        {
            // 无边框浮窗在 Win11 上 DWM 也不给圆角，只能区域裁剪 → force
            _corners ??= RoundedCorners.Attach(this, RoundedCorners.DefaultCornerRadiusDip, force: true);
            _corners.Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Catch("pptmenu", "长按菜单圆角", ex, LogLevel.Debug);
        }

        try { _frame?.RefreshFrame("长按菜单显示后"); } catch { }
    }

    /// <summary>
    /// 卡片配色。不用半透明的 CardBackgroundFillColorDefaultBrush：Win7 上没有逐像素透明，
    /// 半透明底会透出窗口自己的底色而发灰。直接给成品色，口径跟 <see cref="TrayMenuWindow"/> 一致。
    /// </summary>
    private void ApplyThemeColors()
    {
        var dark = Root.ActualThemeVariant == ThemeVariant.Dark;
        var bg = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xFC, 0xFC, 0xFC));
        var border = new SolidColorBrush(dark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE1, 0xE1, 0xE1));
        var sep = new SolidColorBrush(dark ? Color.FromRgb(0x33, 0x33, 0x33) : Color.FromRgb(0xE8, 0xE8, 0xE8));

        Background = bg;          // 显示前先定成品色，防「白闪」（Win7 关 Aero 时尤其明显）
        Card.Background = bg;
        Card.BorderBrush = border;
        SubDivider.Background = sep;

        foreach (var host in new StackPanel[] { ItemsHost, SubHost })
        {
            foreach (var child in host.Children)
            {
                if (child is Border b && b.Classes.Contains("pptSeparator"))
                    b.Background = sep;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  收起
    // ══════════════════════════════════════════════════════════════════

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 假失焦：前台其实还是自己（Win7 上 Topmost 小窗偶尔会收到这种），不收起
        if (ForegroundIsMe()) return;
        Dismiss(fromItem: false);
    }

    /// <summary>
    /// 收起菜单。<paramref name="fromItem"/> = 用户点了某一项（那条路自己会切前台、发按键），
    /// false = 点空白处 / 按 Esc / 失焦 —— 这时要把前台还给放映窗口。
    /// </summary>
    private void Dismiss(bool fromItem)
    {
        if (!_visible || _closing) return;
        _closing = true;

        try
        {
            CloseSubmenu();
            StopWatchdog();
            Hide();
        }
        catch (Exception ex)
        {
            AppLog.Catch("pptmenu", "收起长按菜单", ex, LogLevel.Debug);
        }

        _visible = false;
        PptMenu.IsOpen = false;
        _closing = false;

        if (fromItem) return;

        // 无论是点窗外还是按 Esc 收的，接下来那一下抬起都别再补动作
        PptMenu.MarkDismissed();

        // 点空白处关掉的：把前台还给放映窗口，不然 PowerPoint 一直是被盖住的状态，
        // 下一次触摸就会被我们的钩子判成「前台不是放映窗口」而整个丢掉。
        try { PptTouch.RestoreSlideshowForeground(); }
        catch (Exception ex) { AppLog.Catch("pptmenu", "把前台还给放映窗口", ex, LogLevel.Debug); }
    }

    /// <summary>
    /// 兜底：菜单开着一分钟还没收，就自己收掉。
    ///
    /// <para>
    /// 本窗口是**置顶**的 —— 万一它因为任何原因没能正常关闭，那张卡片会一直盖在
    /// PowerPoint 上面，放映整个点不动。宁肯菜单意外消失一次，也不能让屏幕被一张浮窗挡住。
    /// （<see cref="PptMenu.IsBlocking"/> 里那条 60 秒上限是同一思路的第二道保险。）
    /// </para>
    /// </summary>
    private void ArmWatchdog()
    {
        if (_watchdog is not null) return;

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _watchdog.Tick += (_, _) =>
        {
            // 菜单已经正常收起来了，定时器可以撤了
            if (!_visible) { DisarmWatchdog(); return; }

            if (unchecked(Environment.TickCount64 - PptMenu.OpenedTick) < 60_000) return;

            AppLog.Warning("pptmenu", "菜单开了 60 秒没收，强制收起");
            Dismiss(fromItem: false);
            DisarmWatchdog();
        };
        _watchdog.Start();
    }

    private void StopWatchdog() => DisarmWatchdog();

    private void DisarmWatchdog()
    {
        if (_watchdog is null) return;
        try { _watchdog.Stop(); } catch { }
        _watchdog = null;
    }

    private bool ForegroundIsMe()
    {
        try
        {
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd == IntPtr.Zero) return false;
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return true;      // 问不到就当是自己，宁可不收
            return fg == hwnd;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把前台抢过来（跟 FlyoutChrome / TrayMenuWindow 同一套：裸 SetForegroundWindow 会被
    /// 前台锁静默拒绝，跟前台线程 AttachThreadInput 搭桥后再设一次）。
    /// </summary>
    private void EnsureForeground()
    {
        try
        {
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd == IntPtr.Zero) return;

            if (NativeMethods.GetForegroundWindow() == hwnd) return;

            _ = NativeMethods.SetForegroundWindow(hwnd);
            if (NativeMethods.GetForegroundWindow() == hwnd) return;

            var fg = NativeMethods.GetForegroundWindow();
            var fgThread = fg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0;
            var myThread = NativeMethods.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != myThread)
            {
                _ = NativeMethods.AttachThreadInput(myThread, fgThread, true);
                _ = NativeMethods.SetForegroundWindow(hwnd);
                _ = NativeMethods.AttachThreadInput(myThread, fgThread, false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Catch("pptmenu", "长按菜单抢前台", ex, LogLevel.Debug);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  键盘
    // ══════════════════════════════════════════════════════════════════

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                // 有子菜单先收子菜单，再按一次才收整张菜单（和系统菜单一致）
                if (_submenuOpen)
                {
                    CloseSubmenu();
                    ApplyLayout();
                    PositionAtAnchor();
                }
                else
                {
                    Dismiss(fromItem: false);
                }
                break;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;

            case Key.Enter:
            case Key.Space:
                if (_selected?.Tag is PptMenuItem picked)
                {
                    e.Handled = true;
                    OnItem(picked);
                }
                break;
        }
    }

    /// <summary>当前高亮行。鼠标移进哪行、上下键走到哪行都归这里，同一时刻只亮一行。</summary>
    private void Select(Button? button)
    {
        if (ReferenceEquals(button, _selected)) return;

        if (_selected is not null) _selected.Classes.Remove("pptSelected");
        _selected = button;
        if (_selected is not null) _selected.Classes.Add("pptSelected");
    }

    private void MoveSelection(int delta)
    {
        // 子菜单展开时，方向键只在子菜单里走（和系统菜单的口径一致）
        var list = _submenuOpen && _subButtons.Count > 0 ? _subButtons : _mainButtons;
        if (list.Count == 0) return;

        var index = _selected is null ? -1 : list.IndexOf(_selected);
        var next = index < 0
            ? (delta > 0 ? 0 : list.Count - 1)
            : (index + delta + list.Count) % list.Count;

        Select(list[next]);
    }
}
