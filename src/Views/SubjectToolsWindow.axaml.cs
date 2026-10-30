using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 学科工具窗 —— 独立窗口，把函数图像 / 方程求解 / 化学方程式配平三件工具单拎出来。
/// （2026-10-22 自上游 dv1.2.0-insider1.3 移植，WinUI → Avalonia。）
///
/// <para>
/// 来由（Nick 2026-10-18）：这三件工具是"用完就走"的，但原来只能先进主界面再导航过去；
/// 用户只想算一道题时，打开整个应用太重。这扇窗让侧边栏点一下就直接用，
/// 主窗口可以一直收在托盘里。
/// </para>
///
/// <para>
/// ⚠️ 三个工具页都是纯 <see cref="Platform.PageBase"/>，不依赖 <c>ShellPage</c> / <c>Frame</c> / <c>App.MainWindow</c>
/// （已逐页核过），所以能直接塞进本窗右侧的 Frame 里。
/// </para>
///
/// <para>
/// ⚠️ 关掉 = **真销毁**（不像工具浮窗那种"收起来"）：学科窗没有留着复用的价值，
/// 真关最省事，也不会在退出链路上留下没关掉的窗口把进程吊住（§V）。
/// </para>
///
/// <para>⚠️ 移植说明（WinUI → Avalonia）：</para>
/// <list type="bullet">
/// <item>AppWindow / OverlappedPresenter / DisplayArea → Avalonia 的 Window 属性 + <c>Screens.Primary.WorkingArea</c>；
///       <c>ResizeClient(dip×scale)</c> → <c>ClientSize</c>（Avalonia 直接吃逻辑像素，不用再乘 DPI）；</item>
/// <item><c>ModernWindow.ApplyTitleBar</c>（客户区延伸 + 系统按钮）→ 仓储既有写法：Win10/11 用
///       <c>ExtendClientAreaToDecorationsHint</c> + <c>PreferSystemChrome</c>，Win7 用 <see cref="Core.SelfDrawnFrame"/>
///       自绘标题条（见 <see cref="ToolPaletteWindow"/> 的同一套安排）；</item>
/// <item>材质/圆角必须等窗口真显示之后再上（构造期拿不到 HWND），挪进了 <see cref="BringUp"/>；</item>
/// <item><c>Win32Interop.GetWindowIdFromWindow</c> / <c>WindowNative.GetWindowHandle</c> → <c>Backdrop.TryGetHwnd</c>；
///       收进 <c>Platform.NativeMethods</c>，不另写 DllImport。</item>
/// </list>
/// </summary>
public sealed partial class SubjectToolsWindow : Window
{
    private const int DefaultWidthDip = 1180;
    private const int DefaultHeightDip = 780;

    /// <summary>自绘标题栏高度（dip）。必须和 XAML 里 TitleBarHost 那一行的高度一致。</summary>
    private const int TitleBarHeightDip = 32;

    /// <summary>
    /// Win7 路线：自己接管窗口非客户区（方案与理由见 <see cref="ToolPaletteWindow"/> 的同类注释）。
    /// Win10/11 走客户区扩展 + 系统三大金刚。
    /// </summary>
    private static readonly bool UseExtendedTitleBar = !OsInfo.IsWindows7;

    /// <summary>三件工具（顺序 = 左侧紧凑栏从上到下的顺序）。</summary>
    private static readonly (string Id, string Name, Type Page)[] Tools =
    {
        ("function-plot", "函数图像", typeof(Pages.FunctionPlotPage)),
        ("equation", "方程求解", typeof(Pages.Tools.EquationPage)),
        ("chemistry", "化学方程式配平", typeof(Pages.Tools.ChemistryPage)),
    };

    private static SubjectToolsWindow? _instance;

    /// <summary>自绘窗口框架（只在 Win7 路线上非 null）。</summary>
    private SelfDrawnFrame? _frame;

    /// <summary>圆角：Win11 走 DWM，Win7/10 走窗口区域裁剪（见 <see cref="Core.RoundedCorners"/>）。</summary>
    private RoundedCorners? _corners;

    private bool _shownOnce;

    /// <summary>
    /// 程序化改选中项时的闸门。**初值就是 true**：
    /// NavigationView 在加载时会自己选中第一项，那一次不能当成"用户切换了工具"
    /// （否则会把上次用的工具覆盖成函数图像）。真正的选中在 Root.Loaded 里做。
    /// </summary>
    private bool _suppress = true;

    /// <summary>本次希望停在哪个工具（ShowTool 传入 / 存档里上次用的那个）。</summary>
    private string? _wantId;

    private SubjectToolsWindow()
    {
        InitializeComponent();

        // 本窗自带的 Frame 也要挂上导航生命周期（PageBase 的 OnNavigatedTo/From 靠它）。
        // ⚠️ 必须在第一次 Navigate 之前调用（见 Platform/Navigation 的注释）。
        Navigation.Attach(ContentFrame);

        Configure();

        // NavigationView 加载完会自选第一项（LegalWindow 的 SelectorBar 也踩过同样的坑）⇒
        // 这时按真正想的那个工具再拨一次；之前那次已经被 _suppress 挡掉了。
        Root.Loaded += (_, _) =>
        {
            _suppress = false;
            SelectTool(_wantId);
        };
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>摊开这扇窗；已经开着就切到指定工具，再把它调到前面。</summary>
    /// <param name="toolId">function-plot / equation / chemistry；null 或认不出 = 上次用的那个。</param>
    public static void ShowTool(string? toolId)
    {
        try
        {
            _instance ??= new SubjectToolsWindow();

            var id = Normalize(toolId) ?? Normalize(App.Settings.Current.SubjectToolsLastTool) ?? Tools[0].Id;
            _instance._wantId = id;
            _instance.SelectTool(id);
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("subject-tools", ex, "打开学科工具窗口");
        }
    }

    /// <summary>退出应用时真正销毁（见 <c>MainWindow</c> 的 FinishExit；由主控接线）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("subject-tools", "学科工具窗口实例已请求 Close");
    }

    // ── 选中工具 ─────────────────────────────────────────────────────────

    /// <summary>把左侧栏拨到指定工具并导航过去；会把这次选择记进存档（下次打开停在这儿）。</summary>
    private void SelectTool(string? toolId)
    {
        var id = Normalize(toolId) ?? Tools[0].Id;
        var index = Array.FindIndex(Tools, t => t.Id == id);
        if (index < 0) index = 0;

        var was = _suppress;
        _suppress = true;
        try
        {
            // ⚠️ 不直接 Nav.MenuItems[index]：FA 的 MenuItems 是 IList<object>，按项类型取更稳
            var items = Nav.MenuItems;
            if (index < items.Count) Nav.SelectedItem = items[index];
        }
        finally
        {
            _suppress = was;
        }

        NavigateTo(Tools[index].Page, Tools[index].Id);
        Remember(Tools[index].Id);
    }

    private void Nav_SelectionChanged(object? sender, FluentAvalonia.UI.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppress) return;
        if (args.SelectedItem is not FluentAvalonia.UI.Controls.NavigationViewItem { Tag: string id }) return;

        var index = Array.FindIndex(Tools, t => t.Id == id);
        if (index < 0) return;

        NavigateTo(Tools[index].Page, Tools[index].Id);
        Remember(Tools[index].Id);
    }

    private void NavigateTo(Type nativePage, string? toolId)
    {
        // Web 优先：这个工具有网页版、且本机跑得动 → 交给宿主页（与主页同一套渲染）；
        // 否则走原生页。不弹任何提示：降级对用户是无感的（与 ToolHostPage 一致）。
        if (toolId is not null && Services.ToolCarrier.RequestFor(toolId, nativePage, null) is { } request)
        {
            // 已经在网页版上就别重建（否则会把用户刚画的东西清掉）
            if (ContentFrame.Content is Pages.ToolHostPage) return;
            ContentFrame.Navigate(typeof(Pages.ToolHostPage), request);
            return;
        }

        // 已经停在这一页就别再 Navigate 一次（会白重建一遍页面、把用户刚输入的内容清掉）
        if (ContentFrame.Content?.GetType() == nativePage) return;
        ContentFrame.Navigate(nativePage);
    }

    private static void Remember(string id)
    {
        try
        {
            if (App.Settings.Current.SubjectToolsLastTool == id) return;
            App.Settings.Current.SubjectToolsLastTool = id;
            App.Settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warning("subject-tools", "记录上次使用的工具失败：" + ex.Message);
        }
    }

    private static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var key = id.Trim();
        return Array.Exists(Tools, t => t.Id == key) ? key : null;
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            Title = "学科工具";
            ShowInTaskbar = true;
            CanResize = true;
            MinWidth = 960;                 // 函数图像那页是三栏铺满，再窄会压变形
            MinHeight = 560;

            try
            {
                if (UseExtendedTitleBar)
                {
                    ExtendClientAreaToDecorationsHint = true;
                    ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
                    ExtendClientAreaTitleBarHeightHint = TitleBarHeightDip;
                    // 系统三大金刚已经有了，自绘那颗收起来（否则右上角两颗 ×）
                    if (CaptionCloseButton is not null) CaptionCloseButton.IsVisible = false;
                }
                else
                {
                    ExtendClientAreaToDecorationsHint = false;
                    AttachSelfDrawnFrame();     // Win7：系统标题栏/边框一个像素都不留
                }
            }
            catch (Exception ex2)
            {
                AppLog.Debug("subject-tools", "标题栏定制不可用: " + ex2.Message);
            }

            ThemeCompat.Apply(Root);

            ApplySize();
            CenterOnScreen();

            // 真关掉 ⇒ 清掉单例，下次 Show 重新建
            Closed += (_, _) => { _instance = null; };
        }
        catch (Exception ex)
        {
            AppLog.Exception("subject-tools", ex, "学科工具窗口初始化");
        }
    }

    /// <summary>
    /// Win7 路线：自己接管窗口的非客户区，把系统那圈标题栏/边框吃掉。
    /// 挂载失败时窗口就是一个普普通通带原生标题栏的窗口 —— 退回原生样式是**完整可用**的。
    /// </summary>
    private void AttachSelfDrawnFrame()
    {
        try
        {
            _frame = SelfDrawnFrame.Attach(this);
            _frame.ResizeEnabled = true;
            _frame.TitleBarHeight = TitleBarHeightDip;
            _frame.InteractiveRegions = TitleBarInteractiveRegions;
            _frame.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(Root);
        }
        catch (Exception ex)
        {
            _frame = null;
            AppLog.Debug("subject-tools", "自绘窗口框架挂载失败，退回原生标题栏: " + ex.Message);
        }
    }

    /// <summary>
    /// 标题栏里「点了不该拖动窗口」的区域（dip，窗口坐标）。
    /// 不挖出来的话，点关闭键会被系统当成拖窗口 —— 点击直接丢失。
    /// </summary>
    private System.Collections.Generic.IReadOnlyList<Rect> TitleBarInteractiveRegions()
    {
        var list = new System.Collections.Generic.List<Rect>(1);
        try
        {
            var button = CaptionCloseButton;
            if (button is not null && button.IsVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0)
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

        return list;
    }

    /// <summary>自绘的关闭键。学科窗没有"收起来"的语义，× = 真关闭（销毁实例）。</summary>
    private void CaptionClose_Click(object? sender, RoutedEventArgs e)
    {
        try { Close(); } catch { }
    }

    // ── 尺寸 / 定位 ──────────────────────────────────────────────────────

    private double Scale() => RenderScaling > 0 ? RenderScaling : 1.0;

    /// <summary>按逻辑像素（dip）定客户区大小，并夹进当前屏幕（小屏上别长出屏幕外）。</summary>
    private void ApplySize()
    {
        try
        {
            var scale = Scale();
            var work = Screens.Primary?.WorkingArea;
            if (work is null) return;

            var maxW = work.Value.Width / scale - 40;
            var maxH = work.Value.Height / scale - 60;

            // 函数图像那页是三栏铺满（左 272 + 中 ≥300 + 右 332），太窄会把它压变形
            var w = Math.Clamp(DefaultWidthDip, 960, Math.Max(960, maxW));
            var h = Math.Clamp(DefaultHeightDip, 560, Math.Max(560, maxH));

            ClientSize = new Size(w, h);
        }
        catch (Exception ex)
        {
            AppLog.Warn("subject-tools", "学科工具窗尺寸设置失败：" + ex.Message);
        }
    }

    private void CenterOnScreen()
    {
        try
        {
            var work = Screens.Primary?.WorkingArea;
            if (work is null) return;

            var scale = Scale();
            var sizeW = (int)Math.Round(ClientSize.Width * scale);
            var sizeH = (int)Math.Round(ClientSize.Height * scale);
            var area = work.Value;

            Position = new PixelPoint(
                area.X + Math.Max(0, (area.Width - sizeW) / 2),
                area.Y + Math.Max(0, (area.Height - sizeH) / 2));
        }
        catch (Exception ex)
        {
            AppLog.Warn("subject-tools", "学科工具窗定位失败：" + ex.Message);
        }
    }

    private void BringUp()
    {
        try
        {
            if (!_shownOnce)
            {
                _shownOnce = true;
                Show();                                     // Avalonia 首次显示（原版 Activate）

                // ⚠️ 主题/材质/圆角都要等窗口真显示之后再补一遍（构造期拿不到 HWND / 主题尚未传播）
                ThemeCompat.Apply(Root);
                _corners = RoundedCorners.Attach(this);
                _corners.Refresh();
                BackdropTheme.Apply(this, Root, App.Settings.Current.Backdrop);

                ApplySize();
                CenterOnScreen();
            }
            else
            {
                var hwnd = Backdrop.TryGetHwnd(this);
                try { NativeMethods.ShowWindow(hwnd, SW_SHOW); } catch { }
                try { NativeMethods.SetForegroundWindow(hwnd); } catch { }
                Activate();
            }

            Activate();
        }
        catch (Exception ex)
        {
            AppLog.Exception("subject-tools", ex, "激活学科工具窗口");
        }
    }

    private const int SW_SHOW = 5;
}
