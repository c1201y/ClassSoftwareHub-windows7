using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 截屏的**框选层**，照抄 Windows 自带「截图工具」的套路：
///   点「截屏」→ 整屏冻住压暗 + **顶上弹一条工具栏**（矩形 / 窗口 / 全屏 / 取消）→ 选一块 → 进剪贴板 + 出贴图。
///
/// 套路（重要，别再改成"透明窗口盖在屏幕上实时抓"）：
///   ① 打开时**先把整屏抓成 ScreenFrame 冻住**，用这张图当窗口背景 → 用户看到的是"暂停住的画面"；
///   ② 框选只是在这张**已经抓好的图**上裁 —— 不用 live BitBlt，也就完全绕开
///      "覆盖层会不会把自己照进图里 / 要不要 WDA_EXCLUDEFROMCAPTURE"那一堆坑；
///   ③ 也不用把窗口做成真透明（Avalonia 里很难做干净），因为背景本来就是那张图。
///
/// 三种方式：
///   * 矩形：拖拽框一块；
///   * 窗口：鼠标移到哪个窗口就高亮哪个，点一下截它（z 序枚举找光标下的**别人家**窗口，不用 WindowFromPoint
///           —— 覆盖层铺满全屏，WindowFromPoint 只会返回我们自己）；
///   * 全屏：点一下直接截整个虚拟桌面。
///
/// ⚠️ 全程**必须异步**：曾经在 UI 线程上 `.GetAwaiter().GetResult()` 同步等建图 → 死锁、整个程序卡死。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · 跨屏铺虚拟桌面：原版 AppWindow.MoveAndResize(虚拟屏矩形) → Win32 SetWindowPos（无边框窗口
///     外框 = 客户区，物理像素语义一致，见 MoveResizeWin）；先设 Position 再 Show，显示后按实测
///     缩放再精铺一次；
///   · 原版 Root.RightTapped（Avalonia 没有）→ PointerReleased 里判右键；
///   · StickerWindow.KillSystemBorder 是 views-b2 负责的 StickerWindow 的公开契约（原样调用）。
/// </summary>
public sealed partial class SnipOverlayWindow : Window
{
    private enum SnipMode
    {
        None,
        Rect,
        Window,
        Full,
    }

    private static SnipOverlayWindow? _instance;
    private static bool _starting;

    private ScreenFrame? _frame;
    private double _scale = 1.0;                 // DIP → 物理像素

    private SnipMode _mode = SnipMode.None;
    private bool _dragging;
    private Point _startDip;
    private Point _currentDip;

    /// <summary>「窗口」方式下，光标底下那个窗口的框（DIP，相对本窗口）。</summary>
    private Rect? _hoverDip;

    private static readonly SolidColorBrush ActiveFill = new(Color.FromArgb(40, 0x4C, 0xC2, 0xFF));
    private static readonly SolidColorBrush ActiveStroke = new(Color.FromArgb(255, 0x4C, 0xC2, 0xFF));

    private SnipOverlayWindow()
    {
        InitializeComponent();

        Title = "截屏";

        StickerWindow.KillSystemBorder(Backdrop.TryGetHwnd(this));   // 无边框窗那一圈系统边也去掉（跟贴图一致）
        ThemeCompat.Apply(Root);                          // 深浅色跟「设置」走（不只是跟系统走）

        Root.PointerPressed += Root_PointerPressed;
        Root.PointerMoved += Root_PointerMoved;
        Root.PointerReleased += Root_PointerReleased;
        Root.PointerCaptureLost += (_, _) => _dragging = false;
        Root.KeyDown += Root_KeyDown;
        // ⚠️ 原版 Root.RightTapped（WinUI 有这个事件，Avalonia 没有）→ PointerReleased 里判右键，语义一样
        Root.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right) Cancel();
        };
        Root.Loaded += (_, _) => Reset();               // 窗口真上屏后再铺一次暗化层

        // 工具栏上的按下别当成"开始框选"
        Toolbar.PointerPressed += (_, e) => e.Handled = true;

        ApplyMode(SnipMode.None);

        Closed += (_, _) =>
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    /// <summary>
    /// 打开框选层。<paramref name="delayMs"/> 给边条收起留点时间 —— 别把边条一起照进图里。
    /// </summary>
    public static void Begin(int delayMs = 0) => _ = BeginAsync(delayMs);

    private static async Task BeginAsync(int delayMs)
    {
        try
        {
            if (_instance is not null || _starting)
            {
                Log("已经开着（或在开）框选层了，忽略这次");
                return;
            }

            _starting = true;

            if (delayMs > 0) await Task.Delay(delayMs);      // 等边条收起来

            var (vx, vy, vw, vh) = ScreenCapture.VirtualScreen();
            var frame = ScreenCapture.Grab(vx, vy, vw, vh);
            if (frame is null)
            {
                Log("抓全屏失败，截屏中止");
                return;
            }

            var w = new SnipOverlayWindow();
            w._frame = frame;

            // 覆盖整个虚拟桌面：先落位再显示，显示后按实测缩放再精铺一次
            w.Position = new PixelPoint(vx, vy);
            w.Show();
            w._scale = w.DpiScale();
            w.MoveResizeWin(vx, vy, vw, vh);
            _instance = w;

            // 冻屏背景：整屏图铺满窗口（**await**，不能同步等）
            var bmp = ScreenCapture.ToBmp(frame);
            if (bmp is not null)
            {
                var img = await ScreenCapture.ToImageAsync(bmp);
                if (img is not null) w.Frozen.Source = img;
            }

            w.Reset();
            try { w.KeyCatcher.Focus(); } catch { }

            Log($"框选层已开: 虚拟屏={vx},{vy} {vw}x{vh} 缩放={w._scale:0.##}");
        }
        catch (Exception ex)
        {
            Log("框选层打开失败: " + ex);
        }
        finally
        {
            _starting = false;
        }
    }

    // ── 顶部工具栏：切方式 ───────────────────────────────────

    private void Mode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;

        switch (tag)
        {
            case "rect":
                ApplyMode(SnipMode.Rect);
                break;

            case "window":
                ApplyMode(SnipMode.Window);
                break;

            case "full":
                ApplyMode(SnipMode.Full);
                CommitFullscreen();                        // 全屏不用框，直接拍
                break;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Cancel();

    private void ApplyMode(SnipMode mode)
    {
        _mode = mode;
        _dragging = false;
        _hoverDip = null;

        HintText.Text = mode switch
        {
            SnipMode.Rect => "拖动以选择截取区域　·　Esc 取消",
            SnipMode.Window => "将鼠标移至目标窗口后单击　·　Esc 取消",
            SnipMode.Full => "正在截取全屏",
            _ => "选择截取方式：矩形 / 窗口 / 全屏　·　Esc 取消",
        };

        Highlight(ModeRect, mode == SnipMode.Rect);
        Highlight(ModeWindow, mode == SnipMode.Window);
        Highlight(ModeFull, mode == SnipMode.Full);

        // 2026-09-27（Nick）：工具栏挪到屏幕正中之后，选完方式还不收就会正好压住要框的那块。
        // 只在"还没选方式"时留着；收起来后 Esc / 右键仍可取消（见 Root_KeyDown / 右键取消）。
        Toolbar.IsVisible = mode == SnipMode.None;

        Reset();                                           // 换方式就清掉上一坨框
    }

    private static void Highlight(Button b, bool on)
    {
        try
        {
            if (on)
            {
                b.Background = ActiveFill;
                b.BorderBrush = ActiveStroke;
                b.BorderThickness = new Thickness(1);
            }
            else
            {
                b.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                b.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                b.BorderThickness = new Thickness(1);
            }
        }
        catch { }
    }

    // ── 指针 ─────────────────────────────────────────────────

    private void Root_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_frame is null) return;
        var pt = e.GetPosition(Root);

        switch (_mode)
        {
            case SnipMode.Rect:
                _dragging = true;
                _startDip = pt;
                _currentDip = pt;
                try { e.Pointer.Capture(Root); } catch { }
                Redraw();
                SelBorder.IsVisible = false;
                SizeBadge.IsVisible = false;
                break;

            case SnipMode.Window:
                UpdateHover(pt);
                CommitHover();
                break;

            default:
                break;                                     // 还没选方式：点哪儿都没用
        }
    }

    private void Root_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_frame is null) return;
        var pt = e.GetPosition(Root);

        if (_mode == SnipMode.Rect && _dragging)
        {
            _currentDip = pt;
            Redraw();
        }
        else if (_mode == SnipMode.Window)
        {
            UpdateHover(pt);
        }
    }

    private void Root_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mode != SnipMode.Rect || !_dragging) return;
        _dragging = false;
        try { e.Pointer.Capture(null); } catch { }

        var sel = SelectionDip();
        if (sel is null || sel.Value.Width < 4 || sel.Value.Height < 4)
        {
            Log($"框选太小({sel?.Width:0}x{sel?.Height:0})，当取消");
            Cancel();
            return;
        }

        CommitRect(sel.Value);
    }

    private void Root_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    // ── 画框 ─────────────────────────────────────────────────

    private void Reset()
    {
        if (Layer is null) return;
        // ⚠️ WinUI 的 ActualWidth/ActualHeight → Avalonia 11 没有，等价取 Bounds 的宽高
        Dim.Data = new RectangleGeometry { Rect = new Rect(0, 0, Root.Bounds.Width, Root.Bounds.Height) };
        SelBorder.IsVisible = false;
        SizeBadge.IsVisible = false;
    }

    /// <summary>按当前选区重画：暗化层挖洞 + 选中框 + 尺寸标。</summary>
    private void Redraw()
    {
        var r = _mode == SnipMode.Window ? _hoverDip : SelectionDip();
        if (r is null || r.Value.Width <= 0 || r.Value.Height <= 0)
        {
            Reset();
            return;
        }

        var rect = r.Value;

        // 奇偶填充：外面一整块 + 里面选中块 → 中间挖出透明的"洞"
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, Root.Bounds.Width, Root.Bounds.Height) });
        group.Children.Add(new RectangleGeometry { Rect = rect });
        Dim.Data = group;

        Canvas.SetLeft(SelBorder, rect.X);
        Canvas.SetTop(SelBorder, rect.Y);
        SelBorder.Width = rect.Width;
        SelBorder.Height = rect.Height;
        SelBorder.IsVisible = true;

        // 物理像素尺寸（老师关心的是"我截了多大一块"）
        var pxW = (int)Math.Round(rect.Width * _scale);
        var pxH = (int)Math.Round(rect.Height * _scale);
        SizeText.Text = $"{pxW} × {pxH}";
        SizeBadge.IsVisible = true;

        var badgeLeft = Math.Clamp(rect.X, 0, Math.Max(0, Root.Bounds.Width - 90));
        var badgeTop = rect.Y + rect.Height + 6;
        if (badgeTop > Root.Bounds.Height - 30) badgeTop = Math.Max(0, rect.Y - 28);
        Canvas.SetLeft(SizeBadge, badgeLeft);
        Canvas.SetTop(SizeBadge, badgeTop);
    }

    private Rect? SelectionDip()
    {
        var x = Math.Min(_startDip.X, _currentDip.X);
        var y = Math.Min(_startDip.Y, _currentDip.Y);
        var w = Math.Abs(_currentDip.X - _startDip.X);
        var h = Math.Abs(_currentDip.Y - _startDip.Y);
        if (w <= 0 || h <= 0) return null;
        x = Math.Clamp(x, 0, Root.Bounds.Width);
        y = Math.Clamp(y, 0, Root.Bounds.Height);
        w = Math.Min(w, Root.Bounds.Width - x);
        h = Math.Min(h, Root.Bounds.Height - y);
        return new Rect(x, y, w, h);
    }

    /// <summary>「窗口」方式：找光标底下那个窗口的框（DIP）。</summary>
    private void UpdateHover(Point ptDip)
    {
        var frame = _frame;
        if (frame is null) return;

        var px = frame.X + (int)Math.Round(ptDip.X * _scale);
        var py = frame.Y + (int)Math.Round(ptDip.Y * _scale);

        var hit = ScreenCapture.WindowRectUnderPoint(px, py);
        if (hit is null)
        {
            if (_hoverDip is not null)
            {
                _hoverDip = null;
                Reset();
            }
            return;
        }

        var r = hit.Value;
        var dip = new Rect(
            (r.X - frame.X) / _scale,
            (r.Y - frame.Y) / _scale,
            r.W / _scale,
            r.H / _scale);

        _hoverDip = dip;
        Redraw();
    }

    // ── 落地：裁剪 + 进剪贴板 + 贴图 ─────────────────────────

    private void CommitRect(Rect selDip)
    {
        var px = (int)Math.Round(selDip.X * _scale);
        var py = (int)Math.Round(selDip.Y * _scale);
        var pw = Math.Max(1, (int)Math.Round(selDip.Width * _scale));
        var ph = Math.Max(1, (int)Math.Round(selDip.Height * _scale));
        CommitRegion(px, py, pw, ph, "矩形");
    }

    private void CommitHover()
    {
        var frame = _frame;
        var dip = _hoverDip;
        if (frame is null || dip is null)
        {
            Log("窗口方式：光标下没找到窗口");
            return;
        }

        var px = (int)Math.Round(dip.Value.X * _scale);
        var py = (int)Math.Round(dip.Value.Y * _scale);
        var pw = Math.Max(1, (int)Math.Round(dip.Value.Width * _scale));
        var ph = Math.Max(1, (int)Math.Round(dip.Value.Height * _scale));
        CommitRegion(px, py, pw, ph, "窗口");
    }

    private void CommitFullscreen()
    {
        var frame = _frame;
        if (frame is null) { Cancel(); return; }
        CommitRegion(0, 0, frame.Width, frame.Height, "全屏");
    }

    /// <summary>裁 frame 上的一块（帧内物理像素）→ 剪贴板 + 贴图，然后关掉框选层。</summary>
    private void CommitRegion(int px, int py, int pw, int ph, string how)
    {
        var frame = _frame;
        if (frame is null) { Cancel(); return; }

        var bmp = ScreenCapture.ToBmp(frame, px, py, pw, ph);
        var screenX = frame.X + px;
        var screenY = frame.Y + py;

        Close();

        if (bmp is null)
        {
            Log("裁剪失败");
            return;
        }

        Log($"截屏({how})：{pw}x{ph} @{screenX},{screenY}");
        _ = FinishAsync(frame, px, py, pw, ph);
    }

    private static async Task FinishAsync(ScreenFrame frame, int cropX, int cropY, int cropW, int cropH)
    {
        try
        {
            // 截完直接进编辑窗（涂鸦/几何/文字/马赛克 → 复制 / 保存 / 钉图）
            SnipEditorWindow.Open(frame, cropX, cropY, cropW, cropH);
            await Task.CompletedTask;
            TeachingActions.RefocusPrevious();
        }
        catch (Exception ex)
        {
            Log("打开编辑窗失败: " + ex);
        }
    }

    private void Cancel()
    {
        Log("截屏取消");
        Close();
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

    // ── Win32 窗口操作（无边框窗口外框 = 客户区，物理像素）────

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private void MoveResizeWin(int x, int y, int w, int h)
    {
        var hwnd = Backdrop.TryGetHwnd(this);
        if (hwnd == IntPtr.Zero) return;
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static void Log(string msg) => ScreenCapture.Log(msg);
}
