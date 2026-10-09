using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 常用工具窗口：里面塞五个简版工具（随机抽号 / 课堂计时 / 秒表计时 / 全屏时钟 / 桌面留言）。
/// 不需要开主界面，从托盘就能点出来。
///
/// <para>
/// ── 窗口边框方案（2026-09-24 改过一次，2026-10-02 又改回来）──────────────
/// 2026-09-24 曾退回**标准窗口**（系统标题栏 + 原生边框），理由是"自绘那套在触屏上拖不动"。
/// 但那个"拖不动"是**旧实现**的问题（自己用指针事件算位移），不是这个方案固有的：
/// 现在整条 32px 标题栏报 <c>HTCAPTION</c>，拖动由**系统的 modal move loop** 执行 ——
/// 鼠标、触屏、贴边、双击全都跟原生窗口一模一样。
/// </para>
///
/// <para>
/// ⚠️ 为什么要改回来：用户实机反馈「内置工具窗口还有原生边框」——
///    之前是「系统标题栏 + 应用内 32px 自绘标题栏」**两条标题栏叠着**，很难看。
/// </para>
///
/// 用法：<c>ToolPaletteWindow.ShowTool("timer")</c> / <c>HidePalette()</c> / <c>TogglePalette()</c>。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · AppWindow / OverlappedPresenter → Avalonia 窗口属性（ShowInTaskbar / CanResize / Topmost），
///     WS_EX_TOOLWINDOW 由 ShowInTaskbar=false 在 Windows 上代为设置，不再单独 P/Invoke；
///   · ResizeClient(dip×scale) → ClientSize（Avalonia 直接吃逻辑像素，不用再乘 DPI）；
///   · 材质/圆角必须等窗口真显示之后再上（构造期拿不到 HWND），挪进了 ShowPalette；
///   · 标题栏方案：Win10/11 用客户区扩展（系统画三大金刚）；Win7 用 <see cref="Core.SelfDrawnFrame"/>
///     自己接管非客户区，关闭键由 AppTitleBar 里那个 Button.caption 承担；
///   · UpdateCaptionButtonColors 砍掉了：PreferSystemChrome 的系统按钮颜色 Avalonia 没有对应 API。
/// </summary>
public sealed partial class ToolPaletteWindow : Window
{
    private const int PaletteWidthDip = 400;      // 客户区逻辑像素（dip）
    private const int PaletteHeightDip = 360;

    /// <summary>自绘标题栏高度（dip）。必须和 XAML 里 AppTitleBar 的 Height 一致。</summary>
    private const int TitleBarHeightDip = 32;

    /// <summary>位置记忆的版本号。改了默认位置算法就 +1：老版本存的坐标会被忽略，重新按新默认摆一次。</summary>
    private const int PositionVersion = 2;

    /// <summary>
    /// Win7 路线：自己接管窗口非客户区（方案与理由见 <see cref="MainWindow.ConfigureTitleBar"/> 的长注释）。
    /// </summary>
    private static readonly bool UseExtendedTitleBar = !OsInfo.IsWindows7;

    /// <summary>自绘窗口框架（只在 Win7 路线上非 null）。</summary>
    private SelfDrawnFrame? _frame;

    /// <summary>圆角：Win11 走 DWM，Win7/10 走窗口区域裁剪（见 Core/RoundedCorners）。</summary>
    private RoundedCorners? _corners;

    private static ToolPaletteWindow? _instance;

    private string _tool = "pick-number";
    private bool _shownOnce;
    private bool _visible;
    private int _fadeEpoch;                              // 淡入/淡出的代次：淡出播完别回头把刚显示的窗藏掉

    /// <summary>构造期算好的背景偏好（等窗口显示后再交给 Backdrop.Apply）。</summary>
    private readonly string _backdropPrefer;

    public new bool IsVisible => _visible;

    private ToolPaletteWindow()
    {
        InitializeComponent();

        var want = App.Settings.Current.Backdrop;
        // 浮窗**优先亚克力**（小浮窗用云母会把字糊在壁纸上，读不清），用户选"纯色"就纯色
        _backdropPrefer = string.Equals(want, "mica", StringComparison.OrdinalIgnoreCase) ? "acrylic" : want;

        Configure();
        ApplySelection();
    }

    /// <summary>
    /// 标题栏那颗按钮：当前这个工具在主界面里的完整页面（全屏时钟没有页面，直接开全屏）。
    /// ⚠️ 移植说明（WinUI → Avalonia）：
    ///   · 元素主题判断 ElementTheme.Dark → Root.ActualThemeVariant == ThemeVariant.Dark；
    ///   · 其余照搬（切页/收起浮窗、打开对应工具页）。
    /// </summary>
    private void OpenPage_Click(object? sender, RoutedEventArgs e)
    {
        switch (_tool)
        {
            case "timer":
            case "stopwatch":
                HidePalette();
                App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.TimerToolPage));
                break;

            case "clock":
                HidePalette();   // 全屏前面不能挡着浮窗（照 MiniClock 全屏的做法）
                try { ClockFullscreenWindow.Show(null, Root.ActualThemeVariant == ThemeVariant.Dark); } catch { }
                break;

            case "message":
                // 留言的「完整页面」就是工具页（内容/底色/全屏倍数都在那儿调），照实打开
                HidePalette();
                App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.MessageToolPage));
                break;

            default:             // 随机抽号
                HidePalette();
                App.MainWindow?.OpenToolSettings(typeof(Pages.Tools.PickNumberToolPage));
                break;
        }
    }

    // ── 对外入口 ─────────────────────────────────────────────

    /// <summary>显示工具窗口；<paramref name="toolId"/> 非空就切到那个工具。</summary>
    public static void ShowTool(string? toolId = null)
    {
        var win = _instance ??= new ToolPaletteWindow();
        win.ShowPalette(toolId);
    }

    public static void HidePalette() => _instance?.Collapse();

    /// <summary>
    /// 退出应用时**真正销毁**实例 —— 和平时"关掉=收起来"（<see cref="HidePalette"/>）是两回事。
    /// ⛔ 为什么必须有：desktop.Shutdown() / Application.Exit() 会漏窗口，浮窗没被关掉就会把消息循环撑住、
    ///    让进程留在任务管理器里（2026-10-04 实测，见 MainWindow.FinishExit 的注释）。
    /// </summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        Core.AppLog.Info("exit", "浮窗实例已请求 Close");
    }

    public static void TogglePalette()
    {
        if (_instance is { _visible: true }) _instance.Collapse();
        else ShowTool();
    }

    public static bool IsPaletteVisible => _instance is { _visible: true };

    /// <summary>窗口置顶开关变了之后同步一下。</summary>
    public static void ApplyOnTopSetting()
    {
        // 原版走 OverlappedPresenter.IsAlwaysOnTop；Avalonia 对应 Topmost
        if (_instance is { } w) w.Topmost = App.Settings.Current.PaletteOnTop;
    }

    // ── 窗口本身 ─────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            Title = "常用工具";
            ShowInTaskbar = false;          // 不进任务栏、不进 Alt+Tab（入口是**托盘上它自己的图标**）
            // ⚠️ 2026-10-03（用户第 14 轮）：放开缩放 —— SelfDrawnFrame 的 ResizeEnabled 一起开，
            //    六条边都能拖（见 AttachSelfDrawnFrame）。内容区是弹性布局，拉大跟着铺开。
            CanResize = true;
            MinWidth = 300;                 // 别让用户缩到工具挤烂
            MinHeight = 240;
            Topmost = App.Settings.Current.PaletteOnTop;

            try
            {
                if (UseExtendedTitleBar)
                {
                    ExtendClientAreaToDecorationsHint = true;
                    ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
                    ExtendClientAreaTitleBarHeightHint = TitleBarHeightDip;   // 跟 AppTitleBar 一样高
                }
                else
                {
                    ExtendClientAreaToDecorationsHint = false;
                    AttachSelfDrawnFrame();     // Win7：系统标题栏/边框一个像素都不留
                }
            }
            catch (Exception ex2)
            {
                Debug.WriteLine("[toolwin] 标题栏定制不可用: " + ex2.Message);
            }

            // 圆角：Win11 用 DWM 的 CORNER_PREFERENCE，Win7/10 那个属性不认，得靠 SetWindowRgn 裁剪。
            // ⚠️ 2026-10-03 起窗口可缩放：区域只裁掉四角外侧的弧，缩放边（6dip，在窗口矩形内侧）仍能命中；
            //    只有四角最尖上那一小条弧内的角落抓手感会打折 —— 换来的圆角值这个价。
            try { _corners = RoundedCorners.Attach(this); } catch { }

            Root.ActualThemeVariantChanged += (_, _) => ApplySelection();   // 换主题时按钮配色跟着变
            ThemeCompat.Apply(Root);                        // 跟「设置」里的深浅色走
            ClientSize = new Size(PaletteWidthDip, PaletteHeightDip);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[toolwin] 初始化失败: " + ex.Message);
        }

        Core.AppLog.Info("exit", "浮窗实例已建");
        Closed += (_, _) => Core.AppLog.Info("exit", "浮窗 Closed");

        // 关掉 = 收起来，别真销毁。
        // ⛔ 但**应用正在退出时必须放行**：desktop.Shutdown() 逐个关窗，撞上被取消的关闭会中止整条退出流程
        //    → 只要本窗开着，托盘「退出」后进程就会赖在任务管理器里不走（2026-10-04 实测复现）。
        //    App.IsExiting 由 MainWindow.ExitApp / 主窗真的关闭时置位。
        Closing += (_, args) =>
        {
            Core.AppLog.Info("exit", $"浮窗 Closing: IsExiting={App.IsExiting}");
            if (App.IsExiting) return;
            args.Cancel = true;
            Core.AppLog.Info("exit", "浮窗 Closing -> 取消（收起来）");
            HidePalette();
        };
    }

    /// <summary>
    /// Win7 路线：自己接管窗口的非客户区，把系统那圈标题栏/边框吃掉。
    ///
    /// <para>
    /// 和主窗口是**同一个方案**（见 <see cref="Core.SelfDrawnFrame"/>）：一个字节的窗口样式都不改，
    /// 只在消息层声明「非客户区 0 像素」+ 自己报鼠标命中。所以：
    /// 拖整条 32px 标题栏 = 系统的原生拖动（含贴边、双击、触屏）；边缘不报缩放（本窗口不可缩放）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 挂载失败时窗口就是一个普普通通带原生标题栏的窗口 —— 退回原生样式是**完整可用**的，
    ///    不会出现"既没有系统按钮、又没有自绘按钮"的空窗。
    /// </para>
    /// </summary>
    private void AttachSelfDrawnFrame()
    {
        try
        {
            _frame = SelfDrawnFrame.Attach(this);
            // ⚠️ 2026-10-03（用户第 14 轮）：开边缘缩放 —— 六条边在 NCHITTEST 报 HT*，系统模态缩放循环接管
            _frame.ResizeEnabled = true;
            _frame.TitleBarHeight = TitleBarHeightDip; // 32dip 以内返回 HTCAPTION，拖动交给系统
            _frame.InteractiveRegions = TitleBarInteractiveRegions;
            _frame.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(Root);

            PortLog.Step("ToolPaletteWindow: 已接管非客户区（自绘标题栏 32dip，拖动仍由系统执行）");
        }
        catch (Exception ex)
        {
            _frame = null;
            Debug.WriteLine("[toolwin] 自绘窗口框架挂载失败，退回原生标题栏: " + ex.Message);
            PortLog.Step("ToolPaletteWindow: 自绘窗口框架挂载失败，退回原生标题栏 - " + ex.Message);
        }
    }

    /// <summary>
    /// 标题栏里「点了不该拖动窗口」的区域（dip，窗口坐标）。
    /// 不挖出来的话，点关闭键会被系统当成拖窗口 —— 点击直接丢失。
    /// </summary>
    private System.Collections.Generic.IReadOnlyList<Rect> TitleBarInteractiveRegions()
    {
        var list = new System.Collections.Generic.List<Rect>(2);

        // ⚠️「打开页面」和「关闭」都要挖出来：不挖的话点它们会被系统当成拖窗 —— 点击直接丢失。
        AddInteractiveRegion(list, OpenPageButton);
        AddInteractiveRegion(list, CaptionCloseButton);

        return list;
    }

    private void AddInteractiveRegion(System.Collections.Generic.List<Rect> list, Control button)
    {
        try
        {
            if (button.IsVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0)
            {
                var origin = button.TranslatePoint(new Point(0, 0), this);
                if (origin is { } p)
                    list.Add(new Rect(p.X, p.Y, button.Bounds.Width, button.Bounds.Height));
            }
        }
        catch
        {
            // 控件树正在重建时可能取不到坐标：跳过就行，顶多那一下点不动
        }
    }

    /// <summary>自绘的关闭键。语义跟 Esc 一致：**收起来**（不是退出程序）。</summary>
    private void CaptionClose_Click(object? sender, RoutedEventArgs e) => HidePalette();

    private void ShowPalette(string? toolId)
    {
        if (!string.IsNullOrWhiteSpace(toolId)) _tool = toolId!;
        SelectPane(_tool, reload: true);

        // 淡入：先压透明再露脸，然后淡到不透明（系统 Flyout 那种"浮现"而不是"啪"一下）。
        // ⚠️ 压透明必须在 ShowWindow/Activate **之前**：否则先闪一帧满不透明，再被我压 0 重淡，等于闪了一下。
        _fadeEpoch++;                            // 作废还在跑的淡出（别让它回头把窗藏掉）
        FlyoutFade.Prepare(Root);

        try
        {
            Topmost = App.Settings.Current.PaletteOnTop;

            var hwnd = Backdrop.TryGetHwnd(this);
            if (!_shownOnce)
            {
                _shownOnce = true;
                Show();                                     // Avalonia 首次显示（原版 Activate）

                // ⚠️ 2026-10-06（用户「首次启动悬浮窗白底白字」同因同修）：构造期那次
                //    ThemeCompat.Apply 发生在窗口显示之前，主题变体未必传播到位；
                //    显示后补一次主题 + 按钮配色，再 Dispatcher 补一拍兜底。
                ThemeCompat.Apply(Root);
                ApplySelection();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ThemeCompat.Apply(Root);
                    ApplySelection();
                }, Avalonia.Threading.DispatcherPriority.Loaded);

                // ⚠️ 扩展客户区会把窗口圆角偏好按回 Default（Win11 上渲染出来是**直角**），
                //    所以显示之后必须再要一次圆角。Win7 上这个调用不认，改由 RoundedCorners
                //    用 SetWindowRgn 裁剪窗口形状（见 Core/RoundedCorners）。
                _corners?.Refresh();
                // ⚠️ 材质必须等窗口真显示出来之后再上（构造期拿不到 HWND，上了也是纯色兜底）
                Backdrop.Apply(this, Root, _backdropPrefer);
                ApplyPosition();
            }
            else
            {
                NativeMethods.ShowWindow(hwnd, SW_SHOW);
                NativeMethods.SetForegroundWindow(hwnd);
                Activate();
                ApplyPosition();
                _corners?.Refresh();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[toolwin] 显示失败: " + ex.Message);
        }

        _visible = true;
        ApplySelection();
        try { Root.Focus(); } catch { }     // Esc 收起

        FlyoutFade.In(Root, 150);

        // 里面的表该走的走起来
        try { ClockView.Resume(); TimerView.Resume(); StopwatchView.Resume(); } catch { }
    }

    /// <summary>
    /// 收起来 = 藏到托盘（托盘里那个"常用工具"图标一直在，点一下就回来）。
    /// </summary>
    private void Collapse()
    {
        if (!_visible) return;
        _visible = false;
        SavePosition();

        // 收起 = 没人看：停掉里面的表，顺手把内存还给系统
        try { ClockView.Pause(); TimerView.Pause(); StopwatchView.Pause(); } catch { }

        // 先淡出，播完再藏 —— 别"啪"一下消失（系统 Flyout 收起也是淡出）
        var epoch = ++_fadeEpoch;
        FlyoutFade.Out(Root, 150, () =>
        {
            if (epoch != _fadeEpoch) return;             // 淡出期间又点开了：别把刚显示的窗藏掉
            try { NativeMethods.ShowWindow(Backdrop.TryGetHwnd(this), SW_HIDE); }
            catch (Exception ex) { Debug.WriteLine("[toolwin] 收起失败: " + ex.Message); }

            MemoryTrimmer.Trim();
        });
    }

    // ── 五个工具的切换 ───────────────────────────────────────

    private void SelectPane(string tool, bool reload)
    {
        PanePick.IsVisible = tool == "pick-number";
        PaneTimer.IsVisible = tool == "timer";
        PaneStopwatch.IsVisible = tool == "stopwatch";
        PaneClock.IsVisible = tool == "clock";
        PaneMessage.IsVisible = tool == "message";

        App.Settings.Current.PaletteTool = tool;
        App.Settings.Save();

        if (!reload) return;
        if (tool == "pick-number") PickView.Reload();
        if (tool == "message") MessageView.Reload();   // 存档可能刚在工具页里被改过
    }

    private void ApplySelection()
    {
        var accent = Services.ThemeBrush.Get(Root, "AccentFillColorDefaultBrush");
        var textOnAccent = Services.ThemeBrush.Get(Root, "TextOnAccentFillColorPrimaryBrush");

        foreach (var (chip, tag) in new[]
                 {
                     (ChipPick, "pick-number"),
                     (ChipTimer, "timer"),
                     (ChipStopwatch, "stopwatch"),
                     (ChipClock, "clock"),
                     (ChipMessage, "message"),
                 })
        {
            var on = tag == _tool;
            chip.BorderThickness = new Thickness(1);

            if (on)
            {
                chip.Background = accent;
                chip.Foreground = textOnAccent;
                chip.BorderBrush = accent;
            }
            else
            {
                // ⚠️ 没选中的交给 XAML 里那套 {DynamicResource ...}：主题一变它自己就跟着变，最稳
                chip.ClearValue(Button.BackgroundProperty);
                chip.ClearValue(Button.ForegroundProperty);
                chip.ClearValue(Button.BorderBrushProperty);
            }
        }
    }

    private void Chip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            _tool = tag;
            SelectPane(tag, reload: true);
            ApplySelection();
        }
    }

    private void Root_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HidePalette();
    }

    // ── 位置 / 尺寸 ──────────────────────────────────────────

    private static string LogPath => System.IO.Path.Combine(SettingsStore.Dir, "palette.log");

    private static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Dir);
            System.IO.File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    private double Scale() => RenderScaling > 0 ? RenderScaling : 1.0;

    private int ClientWidthPx() => (int)Math.Round(ClientSize.Width * Scale());
    private int ClientHeightPx() => (int)Math.Round(ClientSize.Height * Scale());

    /// <summary>
    /// 位置 + 尺寸：用户动过就用他放的（并保证还在屏幕里）；没动过就摆**屏幕正中间**。
    /// ⚠️ 老版本存在右下角的旧坐标会在 PositionVersion 升级后被忽略一次，重新居中。
    /// ⚠️ 2026-10-03（用户第 14 轮「内置工具窗口可以拖动调整大小」）：窗口可缩放了，
    ///    尺寸（dip）跟位置一起记忆；没存过就用默认的 400×360。
    /// </summary>
    private void ApplyPosition()
    {
        try
        {
            var s = App.Settings.Current;

            // 先恢复尺寸（拖边缩放过的尺寸要活过"收起再打开"）
            var wDip = s.PaletteW > 0 ? Math.Max(MinWidth, s.PaletteW) : PaletteWidthDip;
            var hDip = s.PaletteH > 0 ? Math.Max(MinHeight, s.PaletteH) : PaletteHeightDip;
            ClientSize = new Size(wDip, hDip);

            var work = Screens.Primary.WorkingArea;    // 物理像素（= 原版 DisplayArea.Primary.WorkArea）
            var sizeW = ClientWidthPx();
            var sizeH = ClientHeightPx();

            var hasSaved = s.PalettePosVersion >= PositionVersion && s.PaletteX > -10000 && s.PaletteY > -10000;

            int x, y;
            if (hasSaved)
            {
                x = Math.Clamp(s.PaletteX, work.X, Math.Max(work.X, work.X + work.Width - sizeW));
                y = Math.Clamp(s.PaletteY, work.Y, Math.Max(work.Y, work.Y + work.Height - sizeH));
            }
            else
            {
                x = work.X + (work.Width - sizeW) / 2;
                y = work.Y + (work.Height - sizeH) / 2;
            }

            // Move 同样是物理像素；迭代几次是为了兜住"换算/贴边被系统挪"的情况，正常一遍就到位
            var target = new PixelPoint(x, y);
            for (var i = 0; i < 4; i++)
            {
                Position = target;
                var now = Position;
                var dx = x - now.X;
                var dy = y - now.Y;
                if (Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2) break;
                target = new PixelPoint(target.X + dx, target.Y + dy);
            }

            var actual = Position;
            Log($"几何：请求 {x},{y} 尺寸 {sizeW}x{sizeH} → 实际 {actual.X},{actual.Y}（scale={Scale():0.##}，用存档={hasSaved}）");
        }
        catch (Exception ex)
        {
            Log("定位失败: " + ex.Message);
        }
    }

    /// <summary>收起之前把当前位置和尺寸记住（用户是用系统标题栏拖的，所以直接读实测位置）。</summary>
    private void SavePosition()
    {
        try
        {
            var pos = Position;
            App.Settings.Current.PaletteX = pos.X;
            App.Settings.Current.PaletteY = pos.Y;
            App.Settings.Current.PaletteW = (int)Math.Round(ClientSize.Width);
            App.Settings.Current.PaletteH = (int)Math.Round(ClientSize.Height);
            App.Settings.Current.PalettePosVersion = PositionVersion;
            App.Settings.Save();
        }
        catch { }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
}
