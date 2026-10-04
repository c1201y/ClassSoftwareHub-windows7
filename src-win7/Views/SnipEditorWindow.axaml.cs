using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Shapes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 截图编辑窗：截完一块就弹这个，简单编辑 + 三个动作（复制 / 保存 / 钉图）。
///
/// 规矩（踩过的坑）：
///   · 图片**一律 1:1**（原始像素），窗口装不下就让 ScrollViewer 滚，绝不缩放、绝不铺满；
///   · 导出统一走 PNG（RenderTargetBitmap）：剪贴板 / 保存 / 钉图都用它，
///     省得自己折腾 BMP 和预乘 alpha；
///   · 撤销/重做 = 增删 InkLayer 上的元素（不原地改，天生好回滚）。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · KeyboardAccelerators（Avalonia 没有）→ 窗口 KeyDown 手判 Ctrl+Z/Y/C/S（见 Editor_KeyDown）；
///   · RenderTargetBitmap + BitmapEncoder → Avalonia RenderTargetBitmap（Render 同步、Save 出 PNG）；
///   · 「复制到剪贴板」：原版 WinUI 剪贴板吃 PNG；本移植版的 ScreenCapture.CopyToClipboardAsync
///     写 Win32 CF_DIB（要 BMP 字节），所以复制路径多一步「PNG → BMP」（GDI+ 解码重编，见 Copy_Click）；
///   · FileSavePicker → IStorageProvider.SaveFilePickerAsync；
///   · 标题栏方案沿用 MainWindow 的 UseExtendedTitleBar：Win7 保留系统标题栏；
///     系统按钮配色（UpdateCaptionColors）砍掉了 —— Avalonia 没有对应 API；
///   · StickerWindow.Pin 是 views-b2 负责的 StickerWindow 的公开契约（原样调用）。
/// </summary>
public sealed partial class SnipEditorWindow : Window
{
    /// <summary>Win7 上保留系统标题栏（理由见 MainWindow.ConfigureTitleBar 的长注释，两边同一套）。</summary>
    private static readonly bool UseExtendedTitleBar = !OsInfo.IsWindows7;

    /// <summary>Win7 路线的自绘窗口框架（<see cref="AttachSelfDrawnFrame"/>）。⚠️ 别叫 _frame —— 那是 ScreenFrame。</summary>
    private SelfDrawnFrame? _frameNc;

    private enum Tool
    {
        Pen,
        Marker,
        Arrow,
        Rect,
        Ellipse,
        Text,
        Mosaic,
    }

    private ScreenFrame? _frame;
    private int _cropX;
    private int _cropY;
    private int _pxW;
    private int _pxH;
    private int _shotX;
    private int _shotY;
    private byte[]? _bmp;

    private double _scale = 1.0;
    private double _dipW;
    private double _dipH;

    private Tool _tool = Tool.Pen;
    private Color _color = Color.FromArgb(255, 232, 17, 35);
    private readonly List<Color> _palette = new();
    private double _thickness = 6;                       // DIP
    private int _thicknessStep = 1;                      // 0=细 1=中 2=粗

    private readonly List<Control> _items = new();
    private readonly List<Control> _redo = new();

    // 当前这一笔
    private bool _drawing;
    private Point _start;
    private Polyline? _stroke;
    private Shape? _shape;
    private bool _textPending;

    private DispatcherTimer? _statusTimer;

    private SnipEditorWindow()
    {
        InitializeComponent();

        InkLayer.PointerPressed += Ink_PointerPressed;
        InkLayer.PointerMoved += Ink_PointerMoved;
        InkLayer.PointerReleased += Ink_PointerReleased;
        InkLayer.PointerCaptureLost += (_, _) => _drawing = false;

        // 快捷键（原版 KeyboardAccelerators：Ctrl+Z/Y/C/S，Avalonia 没有，手判）
        KeyDown += Editor_KeyDown;
    }

    /// <summary>截完一块 → 打开编辑窗（必须在 UI 线程调用）。</summary>
    public static SnipEditorWindow? Open(ScreenFrame frame, int cropX, int cropY, int cropW, int cropH)
    {
        try
        {
            var w = new SnipEditorWindow();
            w.Setup(frame, cropX, cropY, cropW, cropH);
            w.Show();                                     // Avalonia：先 Show 才拿得到 HWND / 真实缩放
            w.PostShow();
            _ = w.AutoSaveAsync();                        // 自动存一份原图（设置里能关/能换目录）
            ScreenCapture.Log($"编辑窗已开: {cropW}x{cropH}");
            return w;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("编辑窗打开失败: " + ex);
            return null;
        }
    }

    /// <summary>
    /// 四角圆角（最大化时退回直角）。为什么要手动设、为什么不能拿 RemoveBorder 顶上，
    /// 见 Core/WindowChrome.SetRounded 的说明 —— 这里和主窗口是同一个坑。
    /// </summary>
    private void ApplyRounded()
    {
        try
        {
            WindowChrome.SetRounded(Backdrop.TryGetHwnd(this), WindowState != WindowState.Maximized);
        }
        catch { }
    }

    /// <summary>
    /// Win7 路线：接管非客户区 —— 和主窗 / ToolPaletteWindow 同一个方案（见 Core/SelfDrawnFrame）：
    /// 一个字节的窗口样式都不改，原生标题栏/边框不再绘制，系统缩放与拖动照旧。
    /// </summary>
    private void AttachSelfDrawnFrame()
    {
        try
        {
            _frameNc = SelfDrawnFrame.Attach(this);
            _frameNc.TitleBarHeight = 32;                      // 32dip 以内返回 HTCAPTION，拖动交给系统
            _frameNc.InteractiveRegions = TitleBarInteractiveRegions;
            _frameNc.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(RootGrid);
            ScreenCapture.Log("编辑窗: 已接管非客户区（自绘标题栏 32dip，Win7 不再画原生边框）");
        }
        catch (Exception ex)
        {
            _frameNc = null;
            CaptionCloseButton.IsVisible = false;              // 挂载失败退回原生标题栏：系统有 ✕，自绘的撤掉
            ScreenCapture.Log("编辑窗自绘框架挂载失败，退回原生标题栏: " + ex.Message);
        }
    }

    /// <summary>标题栏里「点了不该拖动窗口」的区域 —— 目前只有自绘关闭按钮一颗。</summary>
    private System.Collections.Generic.IReadOnlyList<Rect> TitleBarInteractiveRegions()
    {
        var list = new System.Collections.Generic.List<Rect>(1);
        try
        {
            if (CaptionCloseButton.IsVisible &&
                CaptionCloseButton.Bounds.Width > 0 && CaptionCloseButton.Bounds.Height > 0 &&
                CaptionCloseButton.TranslatePoint(new Point(0, 0), this) is { } p)
            {
                list.Add(new Rect(p.X, p.Y, CaptionCloseButton.Bounds.Width, CaptionCloseButton.Bounds.Height));
            }
        }
        catch { }
        return list;
    }

    private void CaptionClose_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>窗口内容与状态（在 Show 之前做：不碰 HWND）。</summary>
    private void Setup(ScreenFrame frame, int cropX, int cropY, int cropW, int cropH)
    {
        _frame = frame;
        _cropX = cropX;
        _cropY = cropY;
        _pxW = Math.Max(1, cropW);
        _pxH = Math.Max(1, cropH);
        _shotX = frame.X + cropX;
        _shotY = frame.Y + cropY;
        _bmp = ScreenCapture.ToBmp(frame, cropX, cropY, _pxW, _pxH);

        BuildPalette();
        SelectTool(Tool.Pen);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusTimer.Tick += (_, _) => { try { StatusText.Text = ""; } catch { } };

        _ = LoadAsync();
    }

    /// <summary>窗口外观 / 几何（在 Show 之后做：拿得到 HWND 与真实缩放）。</summary>
    private void PostShow()
    {
        Title = $"截图编辑　{_pxW} × {_pxH}";
        Topmost = true;                                  // 上课时别被别的窗口压下去

        // 自绘标题栏：外观我们自己的（跟应用里「常用工具」窗一个套路），拖动/阴影/圆角还是系统的。
        // 注意：根元素必须有**不透明底色**，否则窗口内容岛不铺满会露黑底（之前就是一团黑的）。
        try
        {
            if (UseExtendedTitleBar)
            {
                ExtendClientAreaToDecorationsHint = true;
                ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
                ExtendClientAreaTitleBarHeightHint = 32;   // 32px，跟 AppTitleBar 一样高
            }
            else
            {
                // ⚠️ 2026-10-03（用户第 12 轮「截图编辑有原生窗口边框」）：Win7 也不再留系统标题栏。
                //    原来这里 ExtendClientArea=false 之后什么都不挂 → 整条 Win7 原生蓝边框全须全尾。
                //    改成跟主窗 / 常用工具窗（ToolPaletteWindow）同一套 SelfDrawnFrame：
                //    原生边框一个像素不画、32dip 标题栏归拖动区、系统缩放/吸附保留；
                //    关闭按钮用自绘的 CaptionCloseButton（axaml 里默认藏着，只在这条路线露出）。
                ExtendClientAreaToDecorationsHint = false;
                AttachSelfDrawnFrame();
                CaptionCloseButton.IsVisible = true;
            }

            // 标题栏整条可拖（原版 SetTitleBar(AppTitleBar) 的等价物）
            AppTitleBar.PointerPressed += (_, e) =>
            {
                try { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); } catch { }
            };

            TitleText.Text = $"截图编辑　{_pxW} × {_pxH}";

            // ⚠️ 扩展客户区会把窗口圆角偏好按回 Default —— 2026-09-29 实测在 Win11 上渲染出来就是**直角**。
            //    必须显式要一次 ROUND；这个窗口可以最大化，最大化时再退回直角（贴边窗口不该削角）。
            ApplyRounded();
            PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty) ApplyRounded();
            };
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("编辑窗标题栏定制失败: " + ex.Message);
        }

        // 系统背景（云母 / 亚克力，跟着「设置」里主窗口那个选择走）：根底置空让背景透出来，
        // 工具条那行透明、图片区是 in-app 亚克力——不支持就退纯色（Backdrop 管）。
        Backdrop.Apply(this, RootGrid);
        ThemeCompat.Apply(RootGrid);                     // 深浅色跟「设置」走（不只是跟系统走）

        _scale = DpiScale();
        _dipW = _pxW / _scale;
        _dipH = _pxH / _scale;

        BaseImage.Width = _dipW;
        BaseImage.Height = _dipH;
        InkLayer.Width = _dipW;
        InkLayer.Height = _dipH;
        CanvasHost.Width = _dipW;
        CanvasHost.Height = _dipH;

        // 窗口：能装下就 1:1 摆开，装不下就按工作区开 + 里面滚（图片本身永远不缩放）
        var work = WorkArea();
        var chromeDip = 96;                              // 标题栏 + 工具栏
        var maxW = Math.Max(360, work.Width / _scale - 40);
        var maxH = Math.Max(260, work.Height / _scale - chromeDip - 40);
        var winW = Math.Min(Math.Max(_dipW + 24, 720), maxW);
        var winH = Math.Min(_dipH, maxH) + chromeDip;
        ClientSize = new Size(winW, winH);
        var x = work.X + Math.Max(0, (int)(work.Width - winW * _scale) / 2);
        var y = work.Y + Math.Max(0, (int)((work.Height - winH * _scale) / 4));
        Position = new PixelPoint(x, y);
    }

    private PixelRect WorkArea()
    {
        try { return Screens.Primary.WorkingArea; }
        catch { return new PixelRect(0, 0, 1920, 1040); }
    }

    /// <summary>截完自动存一份原图（设置 → 常用工具里能关、能改目录，默认桌面）。</summary>
    private async Task AutoSaveAsync()
    {
        try
        {
            if (_frame is null || _bmp is null) return;
            if (!App.Settings.Current.ShotAutoSave) return;

            var path = await ShotSaver.SaveFrameAsync(_frame, _cropX, _cropY, _pxW, _pxH);
            if (string.IsNullOrEmpty(path)) return;

            var name = System.IO.Path.GetFileName(path);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    Status("已自动保存：" + name);
                    ToolTip.SetTip(StatusText, path);   // 悬停看完整路径
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("自动保存失败: " + ex.Message);
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            if (_bmp is null) return;
            var img = await ScreenCapture.ToImageAsync(_bmp);
            if (img is not null) BaseImage.Source = img;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("编辑窗载图失败: " + ex);
        }
    }

    private double DpiScale()
    {
        try
        {
            if (RenderScaling > 0) return RenderScaling;
        }
        catch { }
        return 1.0;
    }

    // ── 工具栏 ───────────────────────────────────────────────

    /// <summary>原版 KeyboardAccelerators 的等价物：Ctrl+Z/Y/C/S 直连对应按钮。</summary>
    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        switch (e.Key)
        {
            case Key.Z: Undo_Click(BtUndo, e!); e.Handled = true; break;
            case Key.Y: Redo_Click(BtRedo, e!); e.Handled = true; break;
            case Key.C: Copy_Click(BtCopy, e!); e.Handled = true; break;
            case Key.S: Save_Click(BtSave, e!); e.Handled = true; break;
        }
    }

    private void Tool_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        SelectTool(tag switch
        {
            "marker" => Tool.Marker,
            "arrow" => Tool.Arrow,
            "rect" => Tool.Rect,
            "ellipse" => Tool.Ellipse,
            "text" => Tool.Text,
            "mosaic" => Tool.Mosaic,
            _ => Tool.Pen,
        });
    }

    private void SelectTool(Tool t)
    {
        _tool = t;
        var sel = new SolidColorBrush(Color.FromArgb(60, 0, 120, 212));
        var none = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        foreach (var (btn, own) in new (Button, Tool)[]
        {
            (BtPen, Tool.Pen), (BtMarker, Tool.Marker), (BtArrow, Tool.Arrow),
            (BtRect, Tool.Rect), (BtEllipse, Tool.Ellipse), (BtText, Tool.Text), (BtMosaic, Tool.Mosaic),
        })
        {
            var on = own == t;
            btn.Background = on ? sel : none;
            btn.BorderBrush = new SolidColorBrush(on ? Color.FromArgb(255, 0, 120, 212) : Color.FromArgb(0, 0, 0, 0));
            btn.BorderThickness = new Thickness(1);
        }
    }

    private void BuildPalette()
    {
        _palette.Clear();
        _palette.Add(Color.FromArgb(255, 232, 17, 35));    // 红
        _palette.Add(Color.FromArgb(255, 255, 140, 0));    // 橙
        _palette.Add(Color.FromArgb(255, 255, 214, 10));   // 黄
        _palette.Add(Color.FromArgb(255, 16, 185, 129));   // 绿
        _palette.Add(Color.FromArgb(255, 0, 120, 212));    // 蓝
        _palette.Add(Color.FromArgb(255, 255, 255, 255));  // 白
        _palette.Add(Color.FromArgb(255, 0, 0, 0));        // 黑

        foreach (var c in _palette)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(SwatchLine()),
                Tag = c,
            };
            swatch.Tapped += (s, _) =>
            {
                if (s is Border sb && sb.Tag is Color cc) { _color = cc; PaintPalette(); }
            };
            ColorStrip.Children.Add(swatch);
        }

        PaintPalette();
    }

    /// <summary>调色板小方块的描边：浅色底用黑、深色底用白，不然深色下看不见边。</summary>
    private Color SwatchLine()
        => RootGrid.ActualThemeVariant == ThemeVariant.Dark
            ? Color.FromArgb(70, 255, 255, 255)
            : Color.FromArgb(60, 0, 0, 0);

    private void PaintPalette()
    {
        foreach (var child in ColorStrip.Children)
        {
            if (child is not Border b || b.Tag is not Color c) continue;
            var isSel = c == _color;
            b.BorderThickness = new Thickness(isSel ? 3 : 2);
            b.BorderBrush = new SolidColorBrush(isSel ? Color.FromArgb(255, 0, 120, 212) : SwatchLine());
        }
    }

    private void Thickness_Click(object? sender, RoutedEventArgs e)
    {
        _thicknessStep = (_thicknessStep + 1) % 3;
        (_thickness, ThicknessText.Text) = _thicknessStep switch
        {
            0 => (3.0, "细"),
            2 => (11.0, "粗"),
            _ => (6.0, "中"),
        };
    }

    // ── 画 ──────────────────────────────────────────────────

    private void Ink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_drawing || _textPending) return;
        var pt = e.GetPosition(InkLayer);

        if (_tool == Tool.Text)
        {
            _ = PromptTextAsync(pt);
            return;
        }

        _drawing = true;
        _start = pt;
        try { e.Pointer.Capture(InkLayer); } catch { }

        var brush = new SolidColorBrush(_color);

        switch (_tool)
        {
            case Tool.Pen:
            case Tool.Marker:
                // ⚠️ WinUI 的 StrokeLineJoin/StrokeStartLineCap/StrokeEndLineCap：Avalonia 的 Shape.Stroke
                //    只接受 IBrush，圆头线帽没有对应属性（Pen 的 cap/join 无法回灌进 Shape 渲染管线）。
                //    降级：仅保留颜色与粗细，笔迹端点为方头（视觉差异极小）。
                _stroke = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = _tool == Tool.Marker ? _thickness * 3 : _thickness,
                    Opacity = _tool == Tool.Marker ? 0.32 : 1.0,
                };
                _stroke.Points.Add(pt);
                InkLayer.Children.Add(_stroke);
                break;

            case Tool.Arrow:
                //（同上：线帽/连接方式无对应属性，仅保留颜色与粗细）
                _stroke = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = _thickness,
                };
                _stroke.Points.Add(pt);
                _stroke.Points.Add(pt);
                InkLayer.Children.Add(_stroke);
                break;

            case Tool.Rect:
                _shape = new Rectangle { Stroke = brush, StrokeThickness = _thickness, Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
                InkLayer.Children.Add(_shape);
                break;

            case Tool.Ellipse:
                _shape = new Ellipse { Stroke = brush, StrokeThickness = _thickness, Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
                InkLayer.Children.Add(_shape);
                break;

            case Tool.Mosaic:
                _shape = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80)),
                    StrokeThickness = 1,
                    // ⚠️ Avalonia 没有 DoubleCollection，工程统一用 AvaloniaList<double>（见 ImageColorToolPage）
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
                    Fill = new SolidColorBrush(Color.FromArgb(56, 0, 0, 0)),
                };
                InkLayer.Children.Add(_shape);
                break;
        }

        e.Handled = true;
    }

    private void Ink_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_drawing) return;
        var pt = e.GetPosition(InkLayer);

        if (_stroke is not null)
        {
            if (_tool == Tool.Arrow)
            {
                UpdateArrow(_stroke, _start, pt);
            }
            else
            {
                _stroke.Points.Add(pt);
            }
        }
        else if (_shape is not null)
        {
            PlaceRect(_shape, new Rect(
                Math.Min(_start.X, pt.X), Math.Min(_start.Y, pt.Y),
                Math.Abs(pt.X - _start.X), Math.Abs(pt.Y - _start.Y)));
        }

        e.Handled = true;
    }

    private void Ink_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        try { e.Pointer.Capture(null); } catch { }

        var end = e.GetPosition(InkLayer);
        var moved = Math.Abs(end.X - _start.X) + Math.Abs(end.Y - _start.Y);

        if (_stroke is not null)
        {
            // 点一下没动 → 不要这一笔
            if (moved < 3) InkLayer.Children.Remove(_stroke);
            else Commit(_stroke);
            _stroke = null;
        }
        else if (_shape is not null)
        {
            var rect = new Rect(
                Math.Min(_start.X, end.X), Math.Min(_start.Y, end.Y),
                Math.Abs(end.X - _start.X), Math.Abs(end.Y - _start.Y));

            if (rect.Width < 3 || rect.Height < 3 || moved < 3)
            {
                InkLayer.Children.Remove(_shape);
            }
            else if (_tool == Tool.Mosaic)
            {
                InkLayer.Children.Remove(_shape);        // 换成一格一格的颜色块
                AddMosaic(rect);
            }
            else
            {
                Commit(_shape);
            }

            _shape = null;
        }

        e.Handled = true;
    }

    private void Commit(Control el)
    {
        _items.Add(el);
        _redo.Clear();
    }

    private static void UpdateArrow(Polyline line, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        line.Points.Clear();
        if (len < 1)
        {
            line.Points.Add(a);
            line.Points.Add(b);
            return;
        }

        var ux = dx / len;
        var uy = dy / len;
        var head = Math.Clamp(len * 0.22, 9, 26);
        var hx = b.X - ux * head;
        var hy = b.Y - uy * head;
        var px = -uy * head * 0.45;
        var py = ux * head * 0.45;

        line.Points.Add(a);
        line.Points.Add(b);
        line.Points.Add(new Point(hx + px, hy + py));
        line.Points.Add(b);
        line.Points.Add(new Point(hx - px, hy - py));
    }

    private static void PlaceRect(Shape shape, Rect r)
    {
        Canvas.SetLeft(shape, r.X);
        Canvas.SetTop(shape, r.Y);
        shape.Width = Math.Max(0, r.Width);
        shape.Height = Math.Max(0, r.Height);
    }

    /// <summary>马赛克：按"截图原始像素"取色，一格一格铺（格数封顶，别造出几万个元素）。</summary>
    private void AddMosaic(Rect rDip)
    {
        var frame = _frame;
        if (frame is null) return;

        var x0 = Math.Clamp((int)Math.Round(rDip.X * _scale), 0, _pxW);
        var y0 = Math.Clamp((int)Math.Round(rDip.Y * _scale), 0, _pxH);
        var x1 = Math.Clamp((int)Math.Round((rDip.X + rDip.Width) * _scale), 0, _pxW);
        var y1 = Math.Clamp((int)Math.Round((rDip.Y + rDip.Height) * _scale), 0, _pxH);

        var w = x1 - x0;
        var h = y1 - y0;
        if (w < 4 || h < 4) return;

        var block = 10;
        while ((long)(w / block + 1) * (h / block + 1) > 1200 && block < 80) block *= 2;

        var group = new Canvas();
        var stride = frame.Width * 4;

        for (var by = y0; by < y1; by += block)
        {
            for (var bx = x0; bx < x1; bx += block)
            {
                var bw = Math.Min(block, x1 - bx);
                var bh = Math.Min(block, y1 - by);

                long r = 0, g = 0, bl = 0;
                var n = 0;
                for (var y = by; y < by + bh; y++)
                {
                    for (var x = bx; x < bx + bw; x++)
                    {
                        var i = (_cropY + y) * stride + (_cropX + x) * 4;
                        if (i < 0 || i + 2 >= frame.Bgra.Length) continue;
                        bl += frame.Bgra[i];
                        g += frame.Bgra[i + 1];
                        r += frame.Bgra[i + 2];
                        n++;
                    }
                }

                if (n == 0) continue;
                var col = Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(bl / n));
                var cell = new Rectangle { Width = bw / _scale, Height = bh / _scale, Fill = new SolidColorBrush(col) };
                Canvas.SetLeft(cell, bx / _scale);
                Canvas.SetTop(cell, by / _scale);
                group.Children.Add(cell);
            }
        }

        InkLayer.Children.Add(group);
        Commit(group);
    }

    private async Task PromptTextAsync(Point at)
    {
        _textPending = true;
        try
        {
            var box = new TextBox { Watermark = "输入文字", Width = 260 };
            var dlg = new ContentDialog
            {
                Title = "添加文字",
                Content = box,
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            var res = await dlg.ShowAsync(this);
            var text = box.Text?.Trim();
            if (res != ContentDialogResult.Primary || string.IsNullOrEmpty(text)) return;

            var tb = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(_color),
                FontSize = 10 + _thickness * 1.6,
                FontWeight = FontWeight.SemiBold,
            };
            Canvas.SetLeft(tb, at.X);
            Canvas.SetTop(tb, Math.Max(0, at.Y - 12));
            InkLayer.Children.Add(tb);
            Commit(tb);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("文字失败: " + ex.Message);
        }
        finally
        {
            _textPending = false;
        }
    }

    // ── 撤销 / 重做 / 清除 ────────────────────────────────────

    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        InkLayer.Children.Remove(last);
        _redo.Add(last);
    }

    private void Redo_Click(object? sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var el = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        InkLayer.Children.Add(el);
        _items.Add(el);
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        InkLayer.Children.Clear();
        _items.Clear();
        _redo.Clear();
        Status("已清除所有标注");
    }

    // ── 三个动作 ─────────────────────────────────────────────

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var png = await ComposeAsync();
            if (png is null) { Status("合成失败"); return; }

            // ⚠️ 原版剪贴板吃 PNG（WinUI DataPackage）；本移植版的 CopyToClipboardAsync 写
            //    Win32 CF_DIB，要 BMP 字节 —— 这里用 GDI+ 把 PNG 解码重编成 BMP（含文件头，
            //    服务端自己剥掉 14 字节文件头换 DIB）。
            using var pngStream = new MemoryStream(png);
            using var img = System.Drawing.Image.FromStream(pngStream);
            using var gdiBmp = new System.Drawing.Bitmap(img);
            using var bmpStream = new MemoryStream();
            gdiBmp.Save(bmpStream, System.Drawing.Imaging.ImageFormat.Bmp);
            await ScreenCapture.CopyToClipboardAsync(bmpStream.ToArray());

            Status("已复制到剪贴板");
            ScreenCapture.Log($"编辑窗：已复制 {_pxW}x{_pxH}");
        }
        catch (Exception ex)
        {
            Status("复制失败");
            ScreenCapture.Log("复制失败: " + ex.Message);
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var png = await ComposeAsync();
            if (png is null) { Status("合成失败"); return; }

            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            // 原版 FileSavePicker（PicturesLibrary + .png 过滤）的等价物
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"截图_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "png",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("PNG 图片") { Patterns = new[] { "*.png" } },
                },
            });
            if (file is null) return;

            await using (var s = await file.OpenWriteAsync())
            {
                await s.WriteAsync(png, 0, png.Length);
            }
            Status("已保存：" + file.Name);
            ScreenCapture.Log($"编辑窗：已保存 {file.Path}");
        }
        catch (Exception ex)
        {
            Status("保存失败");
            ScreenCapture.Log("保存失败: " + ex.Message);
        }
    }

    private async void Pin_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var png = await ComposeAsync();
            if (png is null) { Status("合成失败"); return; }
            StickerWindow.Pin(png, _shotX, _shotY, _pxW, _pxH);
            ScreenCapture.Log($"编辑窗：已钉图 {_pxW}x{_pxH}");
            Close();
        }
        catch (Exception ex)
        {
            Status("钉图失败");
            ScreenCapture.Log("钉图失败: " + ex.Message);
        }
    }

    /// <summary>把「底图 + 标注」合成成 PNG（按物理像素输出）。
    /// ⚠️ 原版 RenderAsync 是异步的；Avalonia 的 Render 同步，语义一致（当帧渲染）。</summary>
    private Task<byte[]?> ComposeAsync()
    {
        try
        {
            var status = StatusText.Text;
            StatusText.Text = "";                        // 别把状态字也照进去

            var w = Math.Max(1, (int)Math.Ceiling(_dipW * _scale));
            var h = Math.Max(1, (int)Math.Ceiling(_dipH * _scale));
            var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96 * _scale, 96 * _scale));
            rtb.Render(CanvasHost);

            StatusText.Text = status;

            using var ms = new MemoryStream();
            rtb.Save(ms);                                // Avalonia 的 Save 默认就编 PNG
            return Task.FromResult<byte[]?>(ms.ToArray());
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("合成失败: " + ex);
            return Task.FromResult<byte[]?>(null);
        }
    }

    private void Status(string text)
    {
        try
        {
            StatusText.Text = text;
            _statusTimer?.Stop();
            _statusTimer?.Start();
        }
        catch { }
    }
}
