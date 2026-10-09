using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 「在 Q 群中反馈」的图文流程窗：提醒浮窗里点完「复制」就摊开这一扇，五步走完 = 反馈已经提交到群相册。
///
/// = **标准窗口**（拖动 / 触屏拖动 / 阴影 / 圆角都交给 Windows）。
///
/// ⚠️ 这扇窗**故意留在任务栏和 Alt+Tab 里**（跟项目里其它辅助窗相反）——
///    用户要一边看它一边切到 QQ 里操作，切走了得能 Alt+Tab 摸回来。
///
/// ⚠️ 四张截图走**内嵌资源**（<c>Assets\feedback\qq-step-*.png</c>），跟反馈页那两张卡片图标同一条路，
///    单文件发布下也在（见 <see cref="EmbeddedAssets.ExtractToCache"/>）。
///
/// ⚠️ 移植说明：
///   · 原版「自绘标题栏」（ExtendsContentIntoTitleBar + SetTitleBar + 系统按钮配色）在 Avalonia +
///     Win7 的组合下按移植契约改为**保留系统标题栏**；原 AppTitleBar 那行降级成内容标识条。
///     因此 <c>UpdateCaptionButtonColors</c> 没有对应物（系统按钮外观归系统管），不再保留。
///   · 尺寸/居中：原版按物理像素手算（ResizeClient + Move）；Avalonia 一律 DIP，
///     用 Width/Height + WindowStartupLocation 达成同语义（夹进屏幕的活儿也在尺寸里做了）。
///   · Esc 的 handledEventsToo 路由：Avalonia 用 AddHandler + Tunnel 路由等价实现。
///   · 内联的 ShowWindow / SetForegroundWindow DllImport 按契约收进 Platform.NativeMethods。
/// </summary>
public sealed partial class QqFeedbackGuideWindow : Window
{
    private const int GuideWidthDip = 700;
    private const int GuideHeightDip = 720;

    private static QqFeedbackGuideWindow? _instance;

    /// <summary>要复制的那段反馈信息（标题 + 正文），由反馈页 / 提醒浮窗传进来。</summary>
    private string _copyText = "";

    private bool _imagesLoaded;
    private bool _visible;

    private QqFeedbackGuideWindow()
    {
        InitializeComponent();
        Configure();
    }

    /// <summary>这扇流程窗现在开着吗。
    /// ⚠️ Avalonia 的 Window 自带实例属性 IsVisible，这里用 new 隐藏 —— 外部只按静态方式调。</summary>
    public static new bool IsVisible => _instance?._visible == true;

    /// <summary>摊开这扇窗（已经开着就只更新要复制的文本，再把它调到前面）。</summary>
    public static void Show(string copyText)
    {
        try
        {
            _instance ??= new QqFeedbackGuideWindow();
            _instance._copyText = copyText;
            _instance.EnsureImages();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("Q群反馈流程窗显示失败: " + ex.Message);
        }
    }

    public static void CloseIfOpen() => _instance?.HideSelf();

    /// <summary>
    /// 退出应用时**真正销毁**实例（<see cref="CloseIfOpen"/> 只是藏起来）。
    /// ⛔ 同 <see cref="ToolPaletteWindow.CloseForExit"/>：Shutdown() 会漏窗口，
    ///    没关掉的窗口会让进程退不掉（2026-10-04 实测）。
    /// </summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        Core.AppLog.Info("exit", "QQ反馈窗实例已请求 Close");
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            // 原版：框架留着（拖动/阴影/圆角靠系统），标题栏内容自己画，不可拉伸、可最小化。
            // Avalonia 版保留系统标题栏，只继承「不可拉伸」这一条；最小化按钮系统默认就有。
            CanResize = false;

            Backdrop.Apply(this, Root, App.Settings.Current.Backdrop);
            ThemeCompat.Apply(Root);

            ApplySize();

            Root.ActualThemeVariantChanged += (_, _) => { /* 系统标题栏配色归系统管，见类注释 */ };

            // ⚠️ 原版走 handledEventsToo：焦点多半在里层 ScrollViewer 上，普通订阅收不到 Esc。
            //    Avalonia 的等价做法 = Tunnel 路由 + handledEventsToo。
            Root.AddHandler(InputElement.KeyDownEvent,
                new EventHandler<KeyEventArgs>((_, e) =>
                {
                    if (e.Key == Key.Escape) HideSelf();
                }), RoutingStrategies.Tunnel, true);

            // 关掉 = 收起来，别真销毁（下次 Show 直接复用，省掉重新解图那一趟）
            // ⛔ 应用正在退出时必须放行 —— 否则本窗会拦下 Shutdown() 的关窗、把整条退出流程
            //    掐断，进程留在任务管理器里（同 ToolPaletteWindow，2026-10-04 实测）。
            Closing += (_, args) =>
            {
                Core.AppLog.Info("exit", $"QQ反馈窗 Closing: IsExiting={App.IsExiting}");
                if (App.IsExiting) return;
                args.Cancel = true;
                HideSelf();
            };
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("Q群反馈流程窗初始化失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 按逻辑像素（dip）定客户区大小，并夹进当前屏幕 ——
    /// 教室机器上还有 1366×768，700×720 的固定尺寸会在那种屏上长出屏幕外。
    /// </summary>
    private void ApplySize()
    {
        try
        {
            var work = WorkArea();
            if (work.Width <= 0 || work.Height <= 0) return;

            // 工作区是物理像素，除回 dip；再留出标题栏 + 一点边距
            var scale = RenderScaling;
            var maxW = work.Width / scale - 60;
            var maxH = work.Height / scale - 100;

            Width = Math.Clamp(GuideWidthDip, 420, Math.Max(420, maxW));
            Height = Math.Clamp(GuideHeightDip, 380, Math.Max(380, maxH));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[qq-guide] 尺寸设置失败: " + ex.Message);
        }
    }

    /// <summary>主屏工作区（物理像素）。</summary>
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
        return default;
    }

    // ── 显示 / 收起 ───────────────────────────────────────────────────────

    private void BringUp()
    {
        _visible = true;
        try
        {
            Show();
            Activate();

            // 原版还补了一记 Win32 的 SetForegroundWindow —— 隐藏后复显的窗口在系统层
            // 可能只是「恢复显示」而没拿到前台，补一下保险（桌面场景真踩过）。
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd != IntPtr.Zero) NativeMethods.SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("Q群反馈流程窗激活失败: " + ex.Message);
        }
    }

    private void HideSelf()
    {
        _visible = false;
        try { Hide(); } catch { }
    }

    // ── 截图 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 四张操作截图从内嵌资源解到缓存目录再挂上去。**只解一次** ——
    /// 每次 Show 都重解一遍纯属白读几 MB 的 IO。
    /// </summary>
    private void EnsureImages()
    {
        if (_imagesLoaded) return;
        _imagesLoaded = true;

        Bind(StepImage1, "qq-step-album-panel.png");
        Bind(StepImage2, "qq-step-create-album.png");
        Bind(StepImage3, "qq-step-upload-button.png");
        Bind(StepImage4, "qq-step-upload-pick.png");
    }

    private static void Bind(Image target, string fileName)
    {
        try
        {
            var path = EmbeddedAssets.ExtractToCache(fileName, fileName);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            target.Source = new Bitmap(path);
        }
        catch (Exception ex)
        {
            // 图读不到就空着，别把整扇窗搞崩（同 FeedbackPage.LoadIcon 的处理）
            Debug.WriteLine("[qq-guide] 截图加载失败 " + fileName + ": " + ex.Message);
        }
    }

    // ── 交互 ─────────────────────────────────────────────────────────────

    /// <summary>剪贴板被别的东西覆盖了（在 QQ 里聊了几句就可能）→ 让她不用回反馈页也能再来一次。</summary>
    private void CopyAgain_Click(object? sender, RoutedEventArgs e)
    {
        if (_copyText.Length == 0) return;
        if (QqFeedback.Copy(_copyText))
            (sender as Button)!.Content = "已复制到剪贴板";
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => HideSelf();
}
