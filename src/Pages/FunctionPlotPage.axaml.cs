using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using XamlPath = Avalonia.Controls.Shapes.Path;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 函数图像（2D）。输入含 x 的表达式 → 采样 → 画折线。
/// （2026-10-18 自上游 1.2.0 移植，WinUI → Avalonia。）
///
/// <para>
/// 分工：解析在 <see cref="ExpressionParser"/>（表达式树，绝不 eval），
/// 采样分段与刻度在 <see cref="FunctionPlot"/>，本页只做交互与呈现。
/// </para>
///
/// <para>⚠️ WinUI → Avalonia 移植说明：</para>
/// <list type="bullet">
/// <item>Windows.Foundation.Point → Avalonia.Point；PathGeometry/PathFigure/LineSegment 同名可用；</item>
/// <item>PointerRoutedEventArgs.GetCurrentPoint(el).Properties.MouseWheelDelta → PointerWheelEventArgs.Delta.Y；</item>
/// <item>CapturePointer/ReleasePointerCapture → e.Pointer.Capture(el) / e.Pointer.Capture(null)；</item>
/// <item>TextBox.PlaceholderText → Watermark；IsSpellCheckEnabled/InputScope 在 Avalonia 无对应，省去；</item>
/// <item>DispatcherTimer → Avalonia.Threading.DispatcherTimer；ContentDialog 不带 XamlRoot。</item>
/// </list>
/// </summary>
public partial class FunctionPlotPage : Platform.PageBase
{
    // 绘图区内边距：左留给 y 轴刻度、下留给 x 轴刻度。
    private const double PadLeft = 54;
    private const double PadTop = 16;
    private const double PadRight = 20;
    private const double PadBottom = 30;

    /// <summary>拖动期间降采样（先跟手，松手再补满密度）。</summary>
    private const double DragSamplesPerPixel = 0.35;

    private sealed class Curve
    {
        public Grid Row = null!;
        public TextBox Box = null!;
        public TextBlock Tip = null!;
        public Border Preview = null!;   // 数学排版预览（GeoGebra 观感）
        public XamlPath Path = null!;
        public Func<double, double>? Fn;
        public bool Invalid;
    }

    private readonly List<Curve> _curves = new();
    private readonly PlotViewport _vp = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };

    // 分析要跑闭式匹配，比解析重一些；缩放拖动时每次都算会卡，
    // 所以单独一个更长的防抖，等操作停下来再算。
    private readonly DispatcherTimer _analysisTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };

    private bool _ready;
    private bool _dragging;
    private Point _dragFrom;
    private int _colorIndex;
    private IBrush[] _strokes = Array.Empty<IBrush>();

    /// <summary>分析栏是否被用户手动收起（讲课时不想让学生看到结论）。</summary>
    private bool _analysisCollapsed;

    // 曲线配色：深浅各一套。
    // ⚠️ 这属于**数据**用色不是界面用色，所以不能跟主题画刷走 —— 但必须按主题换一套，
    //    否则浅色底上的亮蓝/亮黄根本看不清。
    private static readonly Color[] DarkPalette =
    {
        Color.FromArgb(255, 0x4C, 0xC2, 0xFF),  // 天蓝
        Color.FromArgb(255, 0xFF, 0xA9, 0x4D),  // 橙
        Color.FromArgb(255, 0x69, 0xDB, 0x7C),  // 绿
        Color.FromArgb(255, 0xFF, 0x87, 0x87),  // 珊瑚红
        Color.FromArgb(255, 0xB1, 0x97, 0xFC),  // 紫
        Color.FromArgb(255, 0xFF, 0xD4, 0x3B),  // 黄
        Color.FromArgb(255, 0x66, 0xD9, 0xE8),  // 青
        Color.FromArgb(255, 0xF7, 0x83, 0xAC),  // 粉
    };

    private static readonly Color[] LightPalette =
    {
        Color.FromArgb(255, 0x0F, 0x6C, 0xBD),  // 深蓝
        Color.FromArgb(255, 0xD9, 0x53, 0x09),  // 深橙
        Color.FromArgb(255, 0x0F, 0x7B, 0x0F),  // 深绿
        Color.FromArgb(255, 0xC4, 0x2B, 0x1C),  // 深红
        Color.FromArgb(255, 0x6B, 0x3F, 0xC4),  // 深紫
        Color.FromArgb(255, 0x9A, 0x6A, 0x00),  // 深金
        Color.FromArgb(255, 0x0B, 0x6E, 0x8A),  // 深青
        Color.FromArgb(255, 0xB0, 0x2A, 0x6E),  // 深玫红
    };

    public FunctionPlotPage()
    {
        InitializeComponent();

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reparse();
        };

        _analysisTimer.Tick += (_, _) =>
        {
            _analysisTimer.Stop();
            RefreshAnalysis();
        };

        AttachedToVisualTree += (_, _) => OnLoaded();
        DetachedFromVisualTree += (_, _) =>
        {
            _debounce.Stop();
            _analysisTimer.Stop();
        };
    }

    /// <summary>三角函数按弧度还是角度解释。</summary>
    private AngleMode Mode => AngleBox.SelectedIndex == 1 ? AngleMode.Degrees : AngleMode.Radians;

    private void OnLoaded()
    {
        if (_ready) return;

        // ⚠️ 取色必须在挂到树上之后 —— ThemeBrush 是按**元素所在那棵树**取色的。
        var palette = ThemeBrush.IsDark(this) ? DarkPalette : LightPalette;
        _strokes = palette.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();

        _ready = true;

        // 预填一条 sin(x)：一进来就有东西可看，改一个字符比从零输入省事
        AddCurveRow("sin(x)", focus: false);

        BuildKeypadIfReady();

        Reparse();
        ApplyAnalysisLayout(Bounds.Width);
    }

    // ────────────────────────────── 表达式行 ──────────────────────────────

    private void AddCurve_Click(object? sender, RoutedEventArgs e) => AddCurveRow("", focus: true);

    private void AddCurveRow(string text, bool focus)
    {
        var stroke = _strokes.Length > 0
            ? _strokes[_colorIndex++ % _strokes.Length]
            : new SolidColorBrush(Colors.Gray);

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = stroke,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var box = new TextBox
        {
            Text = text,
            Watermark = "例如 sin(x)、x² − 3x + 1、2ˣ",
            TextWrapping = TextWrapping.NoWrap,
        };
        box.TextChanged += Curve_TextChanged;
        box.KeyDown += Curve_KeyDown;
        // ⚠️ 键盘的目标跟踪：记「最后在编辑哪条」。
        box.GotFocus += (_, _) => _keypadTarget = box;

        // 「y =」前缀（Nick 定）：数学惯例长这样，也明示了"不用输入 y ="—— 解析器只吃等号右边。
        var prefix = new TextBlock
        {
            Text = "y =",
            FontSize = 14,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        var tip = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = ThemeBrush.Get(this, "SystemFillColorCriticalBrush"),
            Margin = new Thickness(2, 2, 0, 0),
        };

        // 数学排版预览（GeoGebra 那种观感）：把表达式排成分数/上标/根号。
        // ⛔ 只是**显示**，输入框本身仍是线性文本 —— 可编辑、解析器吃的是它。
        var preview = new Border
        {
            // 左缩进对齐 TextBox 里的正文（输入框内容内边距约 11），别贴着列边
            Padding = new Thickness(11, 2, 0, 0),
            IsVisible = false,
            Child = new TextBlock { FontSize = 12 },
        };

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(remove, "移除这条曲线");

        // 两行：第一行是「圆点｜y =｜输入框｜删除」，第二行是错误提示（横跨输入框那一列）。
        // ⚠️ 圆点和删除按钮必须只在**第一行**里居中 —— 跟提示挤在同一格会让它们随着提示出现往下跑。
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumn(dot, 0);
        Grid.SetRow(dot, 0);

        Grid.SetColumn(prefix, 1);
        Grid.SetRow(prefix, 0);

        Grid.SetColumn(box, 2);
        Grid.SetRow(box, 0);
        box.Margin = new Thickness(10, 0, 0, 0);

        Grid.SetColumn(remove, 3);
        Grid.SetRow(remove, 0);
        remove.Margin = new Thickness(10, 0, 0, 0);

        var under = new StackPanel { Spacing = 0 };
        under.Children.Add(preview);
        under.Children.Add(tip);
        Grid.SetColumn(under, 2);
        Grid.SetRow(under, 1);

        row.Children.Add(dot);
        row.Children.Add(prefix);
        row.Children.Add(box);
        row.Children.Add(remove);
        row.Children.Add(under);

        var line = new XamlPath
        {
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
        };
        CurveLayer.Children.Add(line);

        var curve = new Curve { Row = row, Box = box, Tip = tip, Preview = preview, Path = line };
        _curves.Add(curve);

        remove.Click += (_, _) => RemoveCurve(curve);
        CurveList.Children.Add(row);

        UpdateTargetBox();

        if (focus) box.Focus();
    }

    private void RemoveCurve(Curve curve)
    {
        if (!_curves.Remove(curve)) return;

        CurveLayer.Children.Remove(curve.Path);
        CurveList.Children.Remove(curve.Row);

        // 删空了就补一条空的：别让人对着一个没有输入框的界面发愣
        if (_curves.Count == 0) AddCurveRow("", focus: true);

        Reparse();
    }

    private void Curve_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;

        // 防抖：一边打字一边解析会白算很多轮
        _debounce.Stop();
        _debounce.Start();
    }

    private void Curve_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        _debounce.Stop();
        Reparse();

        // 在最后一条上回车 → 接着加一条新的（连着输几条的常见动作）
        if (sender is TextBox box && _curves.Count > 0
            && ReferenceEquals(_curves[^1].Box, box)
            && !_curves[^1].Invalid
            && !string.IsNullOrWhiteSpace(box.Text))
        {
            AddCurveRow("", focus: true);
        }
    }

    private void AngleBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // ⚠️ 这个事件在 InitializeComponent 里就会触发一次，那时还没有任何曲线
        if (!_ready) return;

        Reparse();   // 同一个 sin(x)，弧度制与角度制的解释不同，整张图要重算
    }

    // ────────────────────────────── 解析与重绘 ──────────────────────────────

    private void Reparse()
    {
        if (!_ready) return;

        foreach (var curve in _curves)
        {
            var text = curve.Box.Text ?? "";

            if (string.IsNullOrWhiteSpace(text))
            {
                curve.Fn = null;
                curve.Invalid = false;
                ShowTip(curve, null);
                UpdatePreview(curve, null);
            }
            else if (ExpressionParser.TryCompile(text, Mode, out var fn, out var error))
            {
                curve.Fn = fn;
                curve.Invalid = false;
                ShowTip(curve, null);

                // 预览用**表达式树**排版（不是文本替换）：树里"除"就是除，
                // 排版边界天然清楚，(a+b)/(c−d) 这种不会排错。
                ExpressionParser.TryParseTree(text, Mode, out var lambda, out _);
                UpdatePreview(curve, lambda);
            }
            else
            {
                curve.Fn = null;
                curve.Invalid = true;
                ShowTip(curve, error);
                UpdatePreview(curve, null);
            }
        }

        Redraw();
        UpdateTargetBox();
        ScheduleAnalysis();
    }

    /// <summary>
    /// 渲染数学排版预览。传 null 表示收起预览（空表达式或解析失败）。
    /// ⚠️ 用 12 号字 + 半透明：它是**辅助**，主角仍是可编辑的输入框。
    /// </summary>
    private void UpdatePreview(Curve curve, LambdaExpression? lambda)
    {
        if (lambda is null)
        {
            curve.Preview.Child = null;
            curve.Preview.IsVisible = false;
            return;
        }

        try
        {
            var brush = ThemeBrush.Get(this, "TextFillColorSecondaryBrush");
            var sized = MathTypesetter.Render(lambda.Body, 13, brush, degrees: Mode == AngleMode.Degrees);

            // 预览左对齐、顶对齐（分数会把高度撑开）
            sized.HorizontalAlignment = HorizontalAlignment.Left;

            curve.Preview.Child = sized;
            curve.Preview.IsVisible = true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("plot", ex, "渲染数学排版预览");
            curve.Preview.Child = null;
            curve.Preview.IsVisible = false;
        }
    }

    /// <summary>改表达式或改视图之后，隔一会儿再跑分析（分析比绘线贵得多）。</summary>
    private void ScheduleAnalysis()
    {
        if (!_ready) return;
        _analysisTimer.Stop();
        _analysisTimer.Start();
    }

    private void ShowTip(Curve curve, string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            curve.Tip.Text = "";
            curve.Tip.IsVisible = false;
            curve.Box.ClearValue(Border.BorderBrushProperty);
        }
        else
        {
            curve.Tip.Text = error;
            curve.Tip.IsVisible = true;
            curve.Box.BorderBrush = ThemeBrush.Get(this, "SystemFillColorCriticalBrush");
        }
    }

    private void PlotArea_SizeChanged(object? sender, SizeChangedEventArgs e) => Redraw();

    /// <summary>
    /// 窗口较窄时**先压缩分析栏**（Nick 定）：图和输入优先。缩放/拖窗口时自动跟着变。
    /// ⚠️ 收起必须连 IsVisible 一起收 —— 只把列宽压成 0 的话内容还在测量/挂在树里。
    /// ⚠️ 阈值 900：实测 1018 逻辑宽的三列布局分析栏放得下，1020 会把它误杀。
    /// </summary>
    private void ApplyAnalysisLayout(double width)
    {
        if (!_ready && width <= 0) return;

        var roomy = width >= 900;
        var show = !_analysisCollapsed && roomy;

        if (Content is Grid root && root.ColumnDefinitions.Count > 0)
            root.ColumnDefinitions[0].Width = show ? new GridLength(272) : new GridLength(0);
        AnalysisPanel.IsVisible = show;

        // 只有"用户主动收起"才给画布上的展开按钮；被挤没了的话按钮会误导
        AnalysisExpand.IsVisible = _analysisCollapsed && roomy;
    }

    private void AnalysisToggle_Click(object? sender, RoutedEventArgs e)
    {
        _analysisCollapsed = !_analysisCollapsed;
        ApplyAnalysisLayout(Bounds.Width);
    }

    private PlotFrame MeasureFrame()
    {
        var w = PlotArea.Bounds.Width;
        var h = PlotArea.Bounds.Height;
        return new PlotFrame(
            PadLeft, PadTop,
            Math.Max(1, w - PadLeft - PadRight),
            Math.Max(1, h - PadTop - PadBottom));
    }

    private void Redraw()
    {
        if (!_ready) return;

        if (PlotArea.Bounds.Width <= 1 || PlotArea.Bounds.Height <= 1) return;

        var frame = MeasureFrame();

        DrawChrome(frame);
        DrawCurves(frame);
    }

    private void DrawChrome(PlotFrame f)
    {
        ChromeLayer.Children.Clear();

        // ⚠️ 只用 ThemeBrush 表里**真实存在**的 key（表外的会静默退到应用级查找）。
        var gridBrush = ThemeBrush.Get(this, "ControlStrokeColorDefaultBrush");
        var axisBrush = ThemeBrush.Get(this, "TextFillColorTertiaryBrush");
        var labelBrush = ThemeBrush.Get(this, "TextFillColorSecondaryBrush");

        // 目标密度：横向约每 88px 一个刻度，纵向约每 62px 一个
        var xStep = FunctionPlot.NiceStep(_vp.XSpan, Math.Max(2, (int)(f.Width / 88)));
        var ySpan = _vp.YSpan(f);
        var yStep = FunctionPlot.NiceStep(ySpan, Math.Max(2, (int)(f.Height / 62)));

        var xTicks = FunctionPlot.Ticks(_vp.XMin, _vp.XMax, xStep);
        var yMin = _vp.YMin(f);
        var yMax = _vp.YMax(f);
        var yTicks = FunctionPlot.Ticks(yMin, yMax, yStep);

        // ── 网格：所有线并进一条几何，省掉几十个可视元素 ──
        var grid = new PathGeometry();
        foreach (var x in xTicks)
        {
            var px = f.Left + (x - _vp.XMin) / _vp.XSpan * f.Width;
            grid.Figures.Add(Segment(new Point(px, f.Top), new Point(px, f.Top + f.Height)));
        }
        foreach (var y in yTicks)
        {
            var py = f.Top + (yMax - y) / (yMax - yMin) * f.Height;
            grid.Figures.Add(Segment(new Point(f.Left, py), new Point(f.Left + f.Width, py)));
        }
        ChromeLayer.Children.Add(new XamlPath { Data = grid, Stroke = gridBrush, StrokeThickness = 1 });

        // ── 坐标轴（只在落进视口时画）──
        var onScreenAxis = yMin <= 0 && yMax >= 0;
        var axes = new PathGeometry();
        if (_vp.XMin <= 0 && _vp.XMax >= 0)
        {
            var px = f.Left + (0 - _vp.XMin) / _vp.XSpan * f.Width;
            axes.Figures.Add(Segment(new Point(px, f.Top), new Point(px, f.Top + f.Height)));
        }
        if (onScreenAxis)
        {
            var py = f.Top + yMax / (yMax - yMin) * f.Height;
            axes.Figures.Add(Segment(new Point(f.Left, py), new Point(f.Left + f.Width, py)));
        }
        ChromeLayer.Children.Add(new XamlPath
        {
            Data = axes,
            Stroke = axisBrush,
            StrokeThickness = 1.4,
            Opacity = 0.85,
        });

        // ── x 轴刻度：贴着 x 轴；轴不在视口里就贴底边 ──
        var xLabelY = onScreenAxis
            ? Math.Clamp(f.Top + yMax / (yMax - yMin) * f.Height + 5, f.Top, f.Top + f.Height - 18)
            : f.Top + f.Height + 5;

        foreach (var x in xTicks)
        {
            // 原点的「0」让 y 轴那一列去显示，两处都写会重字
            if (Math.Abs(x) < xStep * 1e-6) continue;

            var px = f.Left + (x - _vp.XMin) / _vp.XSpan * f.Width;
            PlaceXLabel(FunctionPlot.FormatTick(x, xStep), f, px, xLabelY, labelBrush);
        }

        // ── y 轴刻度：一律贴着左边留白，右对齐 ──
        foreach (var y in yTicks)
        {
            var py = f.Top + (yMax - y) / (yMax - yMin) * f.Height;
            PlaceYLabel(FunctionPlot.FormatTick(y, yStep), f.Left - 8, py, labelBrush);
        }
    }

    private static PathFigure Segment(Point a, Point b) => new()
    {
        StartPoint = a,
        IsClosed = false,
        IsFilled = false,
        Segments = new PathSegments { new LineSegment { Point = b } },
    };

    /// <summary>x 轴刻度：水平居中于 <paramref name="px"/>，贴住 <paramref name="py"/>。位置夹在绘图区内，别把字切掉。</summary>
    private void PlaceXLabel(string text, PlotFrame f, double px, double py, IBrush brush)
    {
        var label = MakeLabel(text, brush);
        var half = label.DesiredSize.Width / 2;

        // ⚠️ 用「先 Min 后 Max」而不是 Math.Clamp —— 窗口极窄时 lo > hi，Clamp 会直接抛。
        var left = Math.Max(f.Left + half, Math.Min(px, f.Left + f.Width - half));

        Canvas.SetLeft(label, left - half);
        Canvas.SetTop(label, py);
        ChromeLayer.Children.Add(label);
    }

    /// <summary>y 轴刻度：右对齐到 <paramref name="px"/>，垂直居中于 <paramref name="py"/>。</summary>
    private void PlaceYLabel(string text, double px, double py, IBrush brush)
    {
        var label = MakeLabel(text, brush);

        Canvas.SetLeft(label, px - label.DesiredSize.Width);
        Canvas.SetTop(label, py - label.DesiredSize.Height / 2);
        ChromeLayer.Children.Add(label);
    }

    private static TextBlock MakeLabel(string text, IBrush brush)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = brush,
            IsHitTestVisible = false,
        };

        // Canvas 不做布局，要自己量出尺寸才能摆位置
        label.Measure(Size.Infinity);
        return label;
    }

    private void DrawCurves(PlotFrame f)
    {
        var density = _dragging ? DragSamplesPerPixel : FunctionPlot.SamplesPerPixel;

        foreach (var curve in _curves)
        {
            if (curve.Fn is null)
            {
                curve.Path.Data = null;
                continue;
            }

            var geometry = new PathGeometry();

            // ⛔ 每一段都是**独立**的 PathFigure：断开的段如果塞进同一条折线，会被连出一条假线。
            foreach (var segment in FunctionPlot.Sample(curve.Fn, _vp, f, density))
            {
                var figure = new PathFigure { StartPoint = segment[0], IsClosed = false, IsFilled = false };
                for (var i = 1; i < segment.Count; i++)
                    figure.Segments.Add(new LineSegment { Point = segment[i] });
                geometry.Figures.Add(figure);
            }

            curve.Path.Data = geometry;
        }

        // 曲线层只在绘图区内出图 —— 否则会盖到刻度标签上、甚至沿着卡片圆角描出去
        var rect = new Rect(f.Left, f.Top, f.Width, f.Height);
        if (CurveLayer.Clip is not RectangleGeometry current || current.Rect != rect)
            CurveLayer.Clip = new RectangleGeometry(rect);
    }

    // ────────────────────────────── 视角交互 ──────────────────────────────

    private void Plot_Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_ready) return;

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 1e-6) return;

        var position = e.GetPosition(PlotArea);
        var f = MeasureFrame();
        _vp.Zoom(delta > 0 ? 1 / 1.15 : 1.15,
                 position.X - f.Left,
                 position.Y - f.Top,
                 f);

        Redraw();
        ScheduleAnalysis();   // 视口变了，分析结论里的范围也要跟着重算
        e.Handled = true;
    }

    private void Plot_Pressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(PlotArea);
        if (!point.Properties.IsLeftButtonPressed) return;

        _dragging = true;
        _dragFrom = point.Position;
        e.Pointer.Capture(PlotArea);
        e.Handled = true;
    }

    private void Plot_Moved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;

        var position = e.GetPosition(PlotArea);
        var dx = position.X - _dragFrom.X;
        var dy = position.Y - _dragFrom.Y;

        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;

        _dragFrom = position;
        _vp.Pan(dx, dy, MeasureFrame());
        Redraw();
        e.Handled = true;
    }

    private void Plot_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);
        Redraw();   // 松手后按满密度重画一遍，把拖动期间省掉的点补回来
        ScheduleAnalysis();   // 视口变了，取值范围与单调区间都会跟着变
        e.Handled = true;
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        _vp.Reset();
        Redraw();
        ScheduleAnalysis();
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => ZoomByButton(1 / 1.25);

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => ZoomByButton(1.25);

    private void ZoomByButton(double factor)
    {
        var f = MeasureFrame();
        _vp.Zoom(factor, f.Width / 2, f.Height / 2, f);
        Redraw();
        ScheduleAnalysis();
    }

    // ────────────────────────────── 函数分析 ──────────────────────────────

    /// <summary>曲线列表变了就刷新「分析对象」下拉；只有一条曲线时整个藏起来。</summary>
    private void UpdateTargetBox()
    {
        if (!_ready) return;

        var previous = TargetBox.SelectedIndex;

        // ⚠️ 改 Items 会触发 SelectionChanged，先摘掉处理器免得白跑一轮分析
        TargetBox.SelectionChanged -= TargetBox_Changed;
        TargetBox.Items.Clear();
        foreach (var curve in _curves)
            TargetBox.Items.Add(string.IsNullOrWhiteSpace(curve.Box.Text) ? "（空表达式）" : curve.Box.Text);

        TargetBox.SelectedIndex = previous >= 0 && previous < _curves.Count ? previous : 0;
        TargetBox.SelectionChanged += TargetBox_Changed;

        // ⚠️ 只有一条曲线时别显示选择器 —— 空壳控件比没有更难懂
        TargetBox.IsVisible = _curves.Count > 1;
    }

    private void TargetBox_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ScheduleAnalysis();
    }

    private Curve? AnalysisTarget()
    {
        if (_curves.Count == 0) return null;

        var index = TargetBox.SelectedIndex;
        if (index < 0 || index >= _curves.Count) index = 0;
        return _curves[index];
    }

    private void RefreshAnalysis()
    {
        if (!_ready) return;

        var curve = AnalysisTarget();

        if (curve?.Fn is null)
        {
            AnalysisEmpty.Text = curve is null
                ? "输入表达式后显示分析结果。"
                : "当前表达式无法解析，暂不能分析。";
            AnalysisEmpty.IsVisible = true;
            AnalysisMode.Text = "";
            AnalysisList.Children.Clear();
            return;
        }

        // ⛔⛔ 只给**函数本身的性质**：标准形式、线性包裹、商式走闭式匹配；其余只在能严格确证时给。
        //    **绝不按当前视口采样后当结论** —— 老师上课把视图拖一下数字就变，
        //    那不是函数性质，是采样窗口的坐标（Nick 点名）。整条数值观察链路已删除。
        ClosedForm? closed = null;
        if (ExpressionParser.TryParseTree(curve.Box.Text ?? "", Mode, out var lambda, out _)
            && lambda is not null)
        {
            closed = FunctionExact.Match(lambda, Mode);
        }

        AnalysisList.Children.Clear();

        if (closed is null)
        {
            AnalysisMode.Text = "无解析结论";
            AnalysisEmpty.Text = "该表达式暂无可给出的闭式结论。";
            AnalysisEmpty.IsVisible = true;
            return;
        }

        AnalysisMode.Text = "精确结论";
        foreach (var (label, value) in closed.Fields)
            AnalysisList.Children.Add(Field(label, value));

        if (closed.Note is { Length: > 0 } note)
            AnalysisList.Children.Add(Footnote(note));

        AnalysisEmpty.IsVisible = false;
    }

    /// <summary>一项＝「标签（固定宽）＋ 值（数学排版）」同行。同行比上下堆紧凑得多，能省出画布的高度。</summary>
    private Control Field(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            // ⛔⛔ 底对齐＝对齐**文字基线**，不是对齐行中心（分数会把行框撑高）。
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        // ⛔ 值走**数学排版**：π/2 是上下堆叠的分数、变量斜体、区间括号按记号排。
        var content = MathText.Render(value, 13, ThemeBrush.Get(this, "TextFillColorPrimaryBrush"));

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(content, 1);
        content.Margin = new Thickness(10, 0, 0, 0);
        grid.Children.Add(caption);
        grid.Children.Add(content);

        return grid;
    }

    /// <summary>末尾说明（为什么有些项没列）。小一号、半透明，不抢结论。</summary>
    private static TextBlock Footnote(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.55,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    // ────────────────────────────── 数学键盘 ──────────────────────────────

    /// <summary>
    /// 一颗键。<para/>
    /// <see cref="Insert"/> 是插进输入框的**表达式文本**；插进去的就是显示的字样
    /// （² √ π − × ÷），解析器直接认这些字符，没有"显示一层、解析另一层"的映射。
    /// <see cref="CaretBack"/> 是插入后光标从文本末尾往回退几格（函数名那类键要留在括号里）。<para/>
    /// <see cref="WrapSelection"/> 非空时，若用户此刻**选中了文本**，则改为套用这个模板
    /// （<c>{0}</c> 代表选区）——"选中 x+1 再按 x² 得到 (x+1)²"。
    /// ⛔ 后缀型键的包裹模板**必须带 {0}**，否则会把选区整个吞掉（实测踩过）。
    /// </summary>
    private sealed record KeyCap(
        string Label,
        string Insert,
        int CaretBack = 0,
        string? WrapSelection = null,
        string? Tooltip = null,
        bool IsCommand = false);

    /// <summary>
    /// 键位表：**6 列 × 6 行 = 36 格，恰好 36 颗键，一格不空**。<para/>
    /// 左 3 列数学（三角/反三角/对数根/幂/常数/括号），右 3 列数字区（照计算器）。
    /// ⛔⛔ 键数必须凑满 36 —— 双栏行数不齐时矮的那栏下方会空一大片（Nick 点名过）。
    /// </summary>
    private static readonly KeyCap[] Caps =
    {
        // ── 三角 ──
        new("sin", "sin()", 1, "sin({0})"),
        new("cos", "cos()", 1, "cos({0})"),
        new("tan", "tan()", 1, "tan({0})"),
        new("7", "7"), new("8", "8"), new("9", "9"),

        // ── 反三角 ──
        new("asin", "asin()", 1, "asin({0})", "反正弦"),
        new("acos", "acos()", 1, "acos({0})", "反余弦"),
        new("atan", "atan()", 1, "atan({0})", "反正切"),
        new("4", "4"), new("5", "5"), new("6", "6"),

        // ── 对数与根 ──
        new("ln", "ln()", 1, "ln({0})", "自然对数"),
        new("log", "log()", 1, "log({0})", "单参数按常用对数算；写 log(x, 2) 是换底"),
        new("√", "√()", 1, "√({0})", "算术平方根"),
        new("1", "1"), new("2", "2"), new("3", "3"),

        // ── 幂 ──
        // ⚠️ 后缀型键的包裹模板**必须带 {0}**，否则选中一段按下去会把选区吞掉。
        new("x²", "²", 0, "({0})²", "选中一段后按此键，给它加平方"),
        new("x³", "³", 0, "({0})³"),
        new("xⁿ", "^", 0, "({0})^", "乘方：底数后按 ^，再输指数"),
        new("0", "0"), new(".", "."), new("⌫", "", 0, null, "删除光标前一个字符", IsCommand: true),

        // ── 常数与绝对值 ──
        // ⛔ π / e **故意不给 WrapSelection**：常数直接插进光标处，不该顶掉选区。
        new("|x|", "abs()", 1, "abs({0})", "绝对值"),
        new("π", "π"),
        new("e", "e"),
        new("+", "+"), new("−", "−"), new("×", "×"),

        // ── 变量 / 括号 / 编辑 ──
        new("(", "(", 0, "({0})"),
        new(")", ")"),
        new("x", "x", 0, null, "自变量"),
        new("÷", "÷"), new(",", ","), new("清空", "", 0, null, "清空这一条表达式", IsCommand: true),
    };

    /// <summary>键盘列数。36 颗键 ÷ 6 列 = 6 行，恰好铺满。</summary>
    private const int KeypadColumns = 6;

    /// <summary>键盘作用在哪一条曲线上：点键时正在用的那个输入框。</summary>
    private TextBox? _keypadTarget;

    /// <summary>
    /// 键盘**常驻**右栏（Nick 定：照 Windows 计算器），页面首帧后建一次。
    /// </summary>
    private void BuildKeypadIfReady()
    {
        if (KeypadGrid.Children.Count > 0) return;
        BuildKeypad();
    }

    /// <summary>键盘当前作用的目标框。</summary>
    private TextBox? FindFocusedBox()
    {
        if (_keypadTarget is not null && _curves.Any(c => ReferenceEquals(c.Box, _keypadTarget)))
            return _keypadTarget;

        return null;
    }

    /// <summary>生成一颗键按钮。</summary>
    private Button MakeKeyButton(KeyCap cap)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = cap.Label,
                FontSize = 12,
            },
            MinWidth = 44,
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = cap,
        };

        if (cap.Tooltip is not null) ToolTip.SetTip(btn, cap.Tooltip);
        btn.Click += Keypad_Click;
        return btn;
    }

    private void BuildKeypad()
    {
        if (KeypadGrid.Children.Count > 0) return;

        // ── 单网格 6 列 × 6 行：左 3 列数学、右 3 列数字，36 格 36 键零空位 ──
        KeypadGrid.Children.Clear();
        KeypadGrid.RowDefinitions.Clear();
        KeypadGrid.ColumnDefinitions.Clear();

        for (var i = 0; i < KeypadColumns; i++)
            KeypadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (int)Math.Ceiling(Caps.Length / (double)KeypadColumns);
        for (var i = 0; i < rows; i++)
            KeypadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < Caps.Length; i++)
        {
            var btn = MakeKeyButton(Caps[i]);
            Grid.SetRow(btn, i / KeypadColumns);
            Grid.SetColumn(btn, i % KeypadColumns);
            btn.Margin = new Thickness(i % KeypadColumns == 0 ? 0 : 6, i / KeypadColumns == 0 ? 0 : 6, 0, 0);
            KeypadGrid.Children.Add(btn);
        }
    }

    private void Keypad_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KeyCap cap }) return;

        var box = _keypadTarget;
        if (box is null || !_curves.Any(c => ReferenceEquals(c.Box, box)))
        {
            box = FindFocusedBox() ?? _curves.FirstOrDefault()?.Box;
            _keypadTarget = box;
        }

        if (box is null) return;

        var text = box.Text ?? "";
        var selStart = box.SelectionStart;
        var selLength = Math.Max(0, box.SelectionEnd - box.SelectionStart);

        // ── 编辑键不走"插入" ──
        if (cap.IsCommand)
        {
            if (cap.Label == "清空")
            {
                box.Text = "";
            }
            else if (selLength > 0)
            {
                box.Text = text.Remove(selStart, selLength);
                box.SelectionStart = selStart;
                box.SelectionEnd = selStart;
            }
            else if (selStart > 0)
            {
                box.Text = text.Remove(selStart - 1, 1);
                box.SelectionStart = selStart - 1;
                box.SelectionEnd = selStart - 1;
            }

            box.Focus();
            return;
        }

        string ins;
        int caret;

        if (selLength > 0 && cap.WrapSelection is not null)
        {
            // 选中了东西 ⇒ 把选区套进模板，光标落在整个结果之后
            var picked = text.Substring(selStart, selLength);
            ins = cap.WrapSelection.Replace("{0}", picked);
            caret = selStart + ins.Length;
        }
        else
        {
            ins = cap.Insert;

            // 已有选区时替换掉它，而不是把新键插进去
            if (selLength > 0)
            {
                text = text.Remove(selStart, selLength);
                box.Text = text;
            }

            caret = selStart + ins.Length - cap.CaretBack;
        }

        box.Text = text.Insert(selStart, ins);

        var caretAt = Math.Clamp(caret, 0, (box.Text ?? "").Length);
        box.SelectionStart = caretAt;
        box.SelectionEnd = caretAt;

        box.Focus();
    }

    // ────────────────────────────── 说明 / 依据 ──────────────────────────────

    private async void Basis_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "分析依据",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            Content = BuildBasis(),
        };

        await dialog.ShowAsync();
    }

    private static Control BuildBasis()
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 500 };

        panel.Children.Add(Line("下列结论由表达式的闭式匹配得出；匹配不到时不列结论，也不用采样值顶替。"));

        panel.Children.Add(Section("标准形式"));
        panel.Children.Add(Line("把表达式树与一批已知函数原型比对（x、x²、x³、1/x、√x、eˣ、ln x、sin/cos/tan 及其反三角等），"));
        panel.Children.Add(Line("同时识别线性包裹 y = a·f(x) + c 与商式 f(x)/g(x)（分母为一次式）。"));
        panel.Children.Add(Line("命中即给出定义域、值域、零点、奇偶性、单调性、极值、渐近线等**函数本身**的性质。"));

        panel.Children.Add(Section("为什么不做数值推断"));
        panel.Children.Add(Line("按当前视口采样得到的极值、单调区间会随缩放平移变化 —— 那描述的是采样窗口，不是函数性质。"));
        panel.Children.Add(Line("因此本页只输出能严格确证的结论；确证不了的项直接不列。"));

        panel.Children.Add(Section("已知的边界"));
        panel.Children.Add(Line("·　结论与当前视图无关，改变缩放/平移不会改变结论。"));
        panel.Children.Add(Line("·　未覆盖的复合形式（如 sin(x²)、x·ln x）暂不给出闭式结论。"));
        panel.Children.Add(Line("·　不提供导数表达式、积分与化简，那需要符号计算。"));

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    private static TextBlock Line(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 20,
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 8, 0, 0),
    };
}
