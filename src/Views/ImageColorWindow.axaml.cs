using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Pages.Tools;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 图片取色浮窗（自上游 dv1.2.0-insider1.10 移植）。
///
/// <para>
/// 侧边栏「取色」那颗按钮开的就是这扇窗。以前它是 <c>Kind = Page</c>，点一下会把整个主界面
/// 拉出来再跳到「内置工具 → 图片取色」；讲课时从课件里取个配色，为这件事调出整个应用太重。
/// 现在跟哈希校验、查阅单词一致：单开一扇小窗，不碰主界面。
/// </para>
///
/// <para>
/// ⚠️ 内容不是重画的一份界面，就是 <see cref="ImageColorToolPage"/> 本体（<c>embedded: true</c>）。
///    两个入口共用同一份取色算法与版式，只在紧凑参数上有差别 —— 见那个构造函数的注释。
/// </para>
///
/// <para>
/// ⚠️ 关掉 = **真销毁**（跟哈希校验窗一致）：这扇窗没有"留着复用"的价值，
///    真关最省事，也不会在退出链路上留一扇没关的窗把进程吊住。
/// </para>
///
/// <para>⚠️ 移植说明（WinUI → Avalonia）：</para>
/// <list type="bullet">
/// <item><c>AppWindow</c> / <c>DisplayArea</c> → <c>ClientSize</c> + <c>Screens.Primary.WorkingArea</c>（不用再乘 DPI）；</item>
/// <item><c>ModernWindow.ApplyTitleBar</c> → <c>SelfDrawnFrame</c> 接管非客户区、关闭键自己画；</item>
/// <item><c>WindowNative.GetWindowHandle</c> → 页面里的选图走 <see cref="FilePickerCompat"/>，
///       由页面自己从 <c>TopLevel</c> 取选择器，本窗不再需要把句柄传进去。</item>
/// </list>
/// </summary>
public sealed partial class ImageColorWindow : Window
{
    private const int WidthDip = 520;

    /// <summary>
    /// 没选图片时的高度：32 标题栏 + 页面内边距 + 130 拖放区 ≈ 215。
    /// 原来定 260，实测底下空出一大截（2026-11-04 上游截图确认）。
    /// </summary>
    private const int EmptyHeightDip = 215;

    /// <summary>出配色之后的高度：拖放区 + 种子色 + 7 档色块（4 列 2 行）+ HSL + 操作行。</summary>
    private const int PaletteHeightDip = 660;

    /// <summary>自绘标题栏高度（dip）。必须和 XAML 里那一行的高度一致。</summary>
    private const int TitleBarHeightDip = 32;

    private static ImageColorWindow? _instance;

    private SelfDrawnFrame? _frame;
    private RoundedCorners? _corners;

    private readonly string _backdropPrefer;

    private ImageColorToolPage? _page;

    private ImageColorWindow()
    {
        InitializeComponent();

        _backdropPrefer = NormalizeBackdrop(App.Settings.Current.Backdrop);
        Configure();
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 摊开这扇窗；已经开着就调到前面（不重建、不丢已经取好的配色）。
    /// </summary>
    public static void Show()
    {
        try
        {
            _instance ??= new ImageColorWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("image-color", ex, "打开图片取色窗口");
        }
    }

    /// <summary>退出应用时真正销毁（由 MainWindow 的退出链路调用；没开过就什么都不做）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
    }

    // ── 窗口配置 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            Title = "图片取色";
            ShowInTaskbar = true;
            CanResize = true;               // 上游 IsResizable = true
                                            // ⚠️ Avalonia 的 Window 没有 CanMaximize / CanMinimize
                                            //    （那两颗按钮已由自绘标题栏去掉，只画关闭键）。
            MinWidth = 420;
            MinHeight = 200;

            ExtendClientAreaToDecorationsHint = false;
            AttachSelfDrawnFrame();

            try { _corners = RoundedCorners.Attach(this); } catch { }

            // 跟应用主题（⛔ 不传 external：分体只作用于侧边栏与常用工具浮窗）
            ThemeCompat.Apply(Root);

            // 内容：整页本体，切紧凑版式
            _page = new ImageColorToolPage(embedded: true)
            {
                ContentStateChanged = OnContentStateChanged,
            };
            ContentHost.Content = _page;

            ApplyHeight(EmptyHeightDip, center: true);

            Closed += (_, _) =>
            {
                _instance = null;
                _page = null;
            };
        }
        catch (Exception ex)
        {
            AppLog.Exception("image-color", ex, "图片取色窗口初始化");
        }
    }

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
            AppLog.Catch("image-color", "挂载自绘窗口框架（已退回原生标题栏）", ex, LogLevel.Debug);
        }
    }

    /// <summary>
    /// 标题栏里「点了不该拖动窗口」的区域（dip，窗口坐标）。
    /// 不挖出来的话，点关闭键会被系统当成拖窗口 —— 点击直接丢失。
    /// </summary>
    private IReadOnlyList<Rect> TitleBarInteractiveRegions()
    {
        var list = new List<Rect>(1);
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

    private void CaptionClose_Click(object? sender, RoutedEventArgs e)
    {
        try { Close(); } catch { }
    }

    /// <summary>页面告诉我们"有图了 / 又空了"，窗口跟着长高或收短。</summary>
    private void OnContentStateChanged(bool hasPalette)
    {
        // 没取到图（比如格式不支持）时也会走 false，这时窗口停在空态高度，用户能看到报错
        ApplyHeight(hasPalette ? PaletteHeightDip : EmptyHeightDip, center: false);
    }

    /// <summary>
    /// 设置窗口高度（宽度固定）。
    /// <para>
    /// <paramref name="center"/> = true 时居中摆放（首次出现用）；
    /// false = 保持当前位置，只在底边出屏时往上顶 —— 用户可能已经把窗拖到顺手的位置，
    /// 一出图就跳回屏幕中间会让人烦。
    /// </para>
    /// </summary>
    private void ApplyHeight(int heightDip, bool center)
    {
        try
        {
            var work = Screens.Primary?.WorkingArea;
            if (work is null) return;

            var scale = Scale();
            var maxW = work.Value.Width / scale - 40;
            var maxH = work.Value.Height / scale - 60;

            var w = Math.Clamp(WidthDip, 420, Math.Max(420, maxW));
            // 下限 200：空态高度 215 要能真的用上 —— 原来卡 260 会把它抬回去（2026-11-04 实测）
            var h = Math.Clamp(heightDip, 200, Math.Max(200, maxH));

            ClientSize = new Size(w, h);

            if (center)
            {
                CenterOnScreen();
                return;
            }

            // 保持当前位置，只在底边出屏时往上顶
            var pos = Position;
            var bottom = pos.Y + (int)Math.Round(ClientSize.Height * scale);
            var limit = work.Value.Y + work.Value.Height - 8;
            var top = bottom > limit ? Math.Max(work.Value.Y + 8, limit - (int)Math.Round(ClientSize.Height * scale)) : pos.Y;

            if (top != pos.Y) Position = new PixelPoint(pos.X, top);

            AppLog.Step("image-color", $"窗口高度按内容调整：{h:0} dip");
        }
        catch (Exception ex)
        {
            AppLog.Warn("image-color", "图片取色窗尺寸设置失败：" + ex.Message);
        }
    }

    private void CenterOnScreen()
    {
        try
        {
            var work = Screens.Primary?.WorkingArea;
            if (work is null) return;

            var scale = Scale();
            var pw = (int)Math.Round(ClientSize.Width * scale);
            var ph = (int)Math.Round(ClientSize.Height * scale);

            Position = new PixelPoint(
                work.Value.X + Math.Max(0, (work.Value.Width - pw) / 2),
                work.Value.Y + Math.Max(0, (work.Value.Height - ph) / 2));
        }
        catch (Exception ex)
        {
            AppLog.Catch("image-color", "定位图片取色窗口", ex, LogLevel.Debug);
        }
    }

    private void BringUp()
    {
        try
        {
            if (!IsVisible) Show();
            _corners?.Refresh();
            ThemeCompat.Apply(Root);
            BackdropTheme.Apply(this, Root, _backdropPrefer);

            var hwnd = Backdrop.TryGetHwnd(this);
            try { NativeMethods.SetForegroundWindow(hwnd); } catch { }
            Activate();
        }
        catch (Exception ex)
        {
            AppLog.Exception("image-color", ex, "激活图片取色窗口");
        }
    }

    private double Scale() => RenderScaling > 0 ? RenderScaling : 1.0;

    /// <summary>主窗口开了 Mica 才在浮窗上退回亚克力（Win7/10 没有 Mica，见 BackdropTheme 的说明）。</summary>
    private static string NormalizeBackdrop(string? want)
        => string.Equals(want, "mica", StringComparison.OrdinalIgnoreCase) ? "acrylic" : (want ?? "solid");
}
