using System;
using System.Collections.Generic;
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
/// 钉图（贴纸）：把一张图钉在屏幕上。
///
/// 规矩（都是踩出来的）：
///   · **1:1**：初始就是图片的原始像素大小，绝不拉伸铺满屏幕（大图就是大图，小图就是小图）；
///   · 窗口尺寸**就等于**图片尺寸（不再 +2 留边），配合深色底 → 没有白边；
///   · 拖四条边/四个角 = 等比缩放（对角/对边锚住不动），拖中间 = 挪位，滚轮也能缩；
///   · **触屏/笔与鼠标走同一条路**：Avalonia 的指针事件按指针给位置（不像 WinUI 的
///     GetCursorPos 永远只认鼠标），单指拖 = 挪位、单指从边缘/角拖 = 改大小（判定放宽到 30dip），
///     双指捏合走 Gestures.PinchEvent = 缩放 —— 一条路全覆盖，不会互相打架；
///   · 双击或右键（长按）关掉；<c>WS_EX_NOACTIVATE</c> 不抢焦点（上课时别把 PPT 顶掉）。
///
/// ⚠️ 移植说明：窗口摆位 / 尺寸走 <c>Platform.NativeMethods.SetWindowPos</c>（物理像素，
/// 与原版 AppWindow.MoveAndResize 等价）；原版内联的 DllImport 按契约收进 Platform。
/// </summary>
public sealed partial class StickerWindow : Window
{
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    private const double MinSide = 48;
    private const double MaxSide = 8000;
    private const double EdgeDip = 10;                 // 鼠标：离边多近算"要拖边框"
    private const double TouchEdgeDip = 30;            // 触屏/笔：手指比光标粗，判定放宽

    private static readonly List<StickerWindow> Keep = new();

    private int _pxW;
    private int _pxH;

    private enum Mode
    {
        None,
        Move,
        Resize,
    }

    // zone：0=身体 1=左 2=右 3=上 4=下 5=左上 6=右上 7=左下 8=右下
    private Mode _mode = Mode.None;
    private int _zone;
    private Point _startPos;                           // 按下时指针在窗口内的位置（DIP）
    private PixelRect _startRect;                      // 按下时的窗口矩形（物理像素）

    // —— 双指捏合那条路（Gestures.PinchEvent，全走"累计倍率 + 锚点"，不会自激）——
    private bool _pinchActive;
    private PixelRect _pinchStartRect;
    private double _pinchK = 1.0;                      // 累计缩放倍数
    private double _pinchPx;                           // 缩放锚点（窗口内 DIP）
    private double _pinchPy;

    private StickerWindow(int pxW, int pxH, int screenX, int screenY)
    {
        InitializeComponent();

        _pxW = Math.Max(1, pxW);
        _pxH = Math.Max(1, pxH);

        Title = "贴图";
        ShowActivated = false;                         // 显示这一下别抢前台

        Root.PointerPressed += Root_PointerPressed;
        Root.PointerMoved += Root_PointerMoved;
        Root.PointerReleased += Root_PointerReleased;
        Root.PointerCaptureLost += (_, _) => { _mode = Mode.None; };
        Root.PointerWheelChanged += Root_WheelChanged;
        Root.DoubleTapped += (_, _) => CloseSticker();
        // ⚠️ Avalonia 的「右键轻点」只有 Gestures.RightTappedEvent 路由事件（没有 CLR 便捷事件）
        Root.AddHandler(Gestures.RightTappedEvent,
            new EventHandler<RoutedEventArgs>((_, _) => CloseSticker()), RoutingStrategies.Bubble);

        // 双指捏合 = 缩放（Gestures 走 Tunnel 收；鼠标不存在 pinch，天然互不打架）
        Root.AddHandler(Gestures.PinchEvent, Root_Pinched, RoutingStrategies.Tunnel);

        // 摆到指定位置、按原始像素定尺寸（1:1）
        ApplyRect(screenX, screenY, _pxW, _pxH);

        Closed += (_, _) => Keep.Remove(this);
    }

    /// <summary>钉一张图。<paramref name="bmp"/> 可以是 PNG 或 BMP 字节（<see cref="ScreenCapture.ToImageAsync"/> 会自己认）。</summary>
    public static void Pin(byte[] bmp, int screenX, int screenY, int pxW, int pxH)
    {
        try
        {
            var w = new StickerWindow(pxW, pxH, screenX, screenY);
            Keep.Add(w);
            _ = w.LoadAsync(bmp);

            w.Show();                                  // ShowActivated=false：不抢前台地建出窗口
            w.ApplyWindowStyles();                     // NOACTIVATE + TOOLWINDOW（句柄就绪后）
            w.SetTopmostNoActivate();

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var hwnd = w.HwndNow();
                    if (hwnd != IntPtr.Zero) NativeMethodsUtil.UpdateWindow(hwnd);
                }
                catch { }
            });

            ScreenCapture.Log($"贴图已出: {pxW}x{pxH} @{screenX},{screenY}（当前共 {Keep.Count} 张）");
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("贴图失败: " + ex);
        }
    }

    /// <summary>窗口扩展样式：NOACTIVATE（不抢焦点）+ TOOLWINDOW（不进 Alt+Tab）。</summary>
    private void ApplyWindowStyles()
    {
        try
        {
            var hwnd = HwndNow();
            if (hwnd == IntPtr.Zero) return;

            var ex = NativeMethodsEx.GetWindowLongPtrW(hwnd, NativeMethodsEx.GWL_EXSTYLE).ToInt64();
            NativeMethodsEx.SetWindowLongPtrW(hwnd, NativeMethodsEx.GWL_EXSTYLE,
                new IntPtr(ex | WsExNoActivate | WsExToolWindow));
            KillSystemBorder(hwnd);
        }
        catch { }
    }

    /// <summary>置顶但不激活（每次显示 / 摆位后调，别让贴图沉到别的窗口底下）。</summary>
    private void SetTopmostNoActivate()
    {
        try
        {
            var hwnd = HwndNow();
            if (hwnd == IntPtr.Zero) return;
            _ = NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    private async Task LoadAsync(byte[] bmp)
    {
        try
        {
            var img = await ScreenCapture.ToImageAsync(bmp);
            if (img is not null) Shot.Source = img;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("贴图载图失败: " + ex);
        }
    }

    private double DpiScale()
    {
        try
        {
            if (RenderScaling > 0) return RenderScaling;
        }
        catch { }
        try
        {
            var dpi = NativeMethodsUtil.GetDpiForWindow(HwndNow());
            if (dpi > 0) return dpi / 96.0;
        }
        catch { }
        return 1.0;
    }

    /// <summary>当前原生句柄（窗口没起来前可能拿不到，用的时候现取）。</summary>
    private IntPtr HwndNow() => Backdrop.TryGetHwnd(this);

    /// <summary>把窗口摆到 (x,y) 并设成 w×h **物理像素**（1:1 时 w/h 就是图片原始像素）。</summary>
    private void ApplyRect(int x, int y, int w, int h)
    {
        try
        {
            _pxW = Math.Clamp(w, (int)MinSide, (int)MaxSide);
            _pxH = Math.Clamp(h, (int)MinSide, (int)MaxSide);

            var hwnd = HwndNow();
            if (hwnd == IntPtr.Zero) return;
            _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, _pxW, _pxH,
                NativeMethodsUtil.SWP_NOZORDER);
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("贴图摆位失败: " + ex.Message);
        }
    }

    /// <summary>当前窗口矩形（物理像素）。句柄还没就绪时用按下时那个起点。</summary>
    private PixelRect CurrentRect()
    {
        var hwnd = HwndNow();
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
            return new PixelRect(r.Left, r.Top, r.Width, r.Height);
        return _startRect;
    }

    // ── 指针 ─────────────────────────────────────────────────

    private void Root_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var dip = e.GetCurrentPoint(Root).Position;
        _zone = ZoneOf(dip);
        _mode = _zone == 0 ? Mode.Move : Mode.Resize;
        _startPos = dip;
        _startRect = CurrentRect();
        try { e.Pointer.Capture(Root); } catch { }
        _pinchActive = false;                          // 新一轮单指操作把捏合状态作废
        e.Handled = true;
    }

    private int ZoneOf(Point dip, double edge = EdgeDip)
    {
        var w = _pxW / DpiScale();
        var h = _pxH / DpiScale();
        var nearL = dip.X <= edge;
        var nearR = dip.X >= w - edge;
        var nearT = dip.Y <= edge;
        var nearB = dip.Y >= h - edge;

        if (nearL && nearT) return 5;
        if (nearR && nearT) return 6;
        if (nearL && nearB) return 7;
        if (nearR && nearB) return 8;
        if (nearL) return 1;
        if (nearR) return 2;
        if (nearT) return 3;
        if (nearB) return 4;
        return 0;
    }

    private void Root_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_mode == Mode.None)
        {
            ShowHover(ZoneOf(e.GetCurrentPoint(Root).Position));
            return;
        }

        var pos = e.GetCurrentPoint(Root).Position;
        var scale = DpiScale();
        var dx = (int)Math.Round((pos.X - _startPos.X) * scale);
        var dy = (int)Math.Round((pos.Y - _startPos.Y) * scale);

        if (_mode == Mode.Move)
        {
            ApplyRect(_startRect.X + dx, _startRect.Y + dy, _startRect.Width, _startRect.Height);
            e.Handled = true;
            return;
        }

        ResizeBy(_zone, dx, dy);
        e.Handled = true;
    }

    /// <summary>等比缩放：角 = 沿主导轴，边 = 只按那条边的方向；锚住对面（边则锚对面边 + 另一轴居中）。</summary>
    private void ResizeBy(int zone, int dx, int dy)
    {
        double w0 = _startRect.Width;
        double h0 = _startRect.Height;
        var aspect = w0 / h0;

        double w;
        double h;
        if (zone is 1 or 2)
        {
            w = w0 + (zone == 2 ? dx : -dx);
            h = w / aspect;
        }
        else if (zone is 3 or 4)
        {
            h = h0 + (zone == 4 ? dy : -dy);
            w = h * aspect;
        }
        else
        {
            // 角：哪个方向动得多按哪个
            var dw = zone is 6 or 8 ? dx : -dx;
            var dh = zone is 7 or 8 ? dy : -dy;
            if (Math.Abs(dw) >= Math.Abs(dh)) { w = w0 + dw; h = w / aspect; }
            else { h = h0 + dh; w = h * aspect; }
        }

        w = Math.Clamp(w, MinSide, MaxSide);
        h = Math.Clamp(h, MinSide, MaxSide);

        // 定位：锚住"对面"
        double x = _startRect.X;
        double y = _startRect.Y;
        var right = _startRect.X + w0;
        var bottom = _startRect.Y + h0;

        if (zone is 1 or 5 or 7) x = right - w;                       // 左边动 → 右边锚住
        else if (zone is 2 or 6 or 8) x = _startRect.X;               // 右边动 → 左边锚住
        else x = _startRect.X + (w0 - w) / 2;                        // 上下边动 → 水平居中

        if (zone is 3 or 5 or 6) y = bottom - h;                     // 上边动 → 下边锚住
        else if (zone is 4 or 7 or 8) y = _startRect.Y;              // 下边动 → 上边锚住
        else y = _startRect.Y + (h0 - h) / 2;                        // 左右边动 → 垂直居中

        ApplyRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
    }

    // ── 双指捏合（Gestures.PinchEvent）──────────────────────────
    // 为什么单独一条路：单指的挪位/缩放走指针事件；捏合是第二个手指落下之后的持续手势，
    // Avalonia 给的是「相对上一次的倍率增量」，跟绝对坐标无关，不会自激震荡。

    private void Root_Pinched(object? sender, PinchEventArgs e)
    {
        if (!_pinchActive)
        {
            // 手指刚变多：基准挪到"现在"，别把上一段单指拖的位移带进来
            _pinchActive = true;
            _pinchK = 1.0;
            _pinchStartRect = CurrentRect();
            _pinchPx = e.ScaleOrigin.X;
            _pinchPy = e.ScaleOrigin.Y;
        }

        _pinchK = Math.Clamp(_pinchK * e.Scale, 0.15, 8.0);

        var w = Math.Clamp(_pinchStartRect.Width * _pinchK, MinSide, MaxSide);
        var h = Math.Clamp(_pinchStartRect.Height * _pinchK, MinSide, MaxSide);
        var k = w / _pinchStartRect.Width;                            // 夹过之后的真实倍数

        // 缩放锚点：捏合中心那个点在屏幕上尽量别跑
        var scale = DpiScale();
        var x = _pinchStartRect.X + _pinchPx * scale * (1 - k);
        var y = _pinchStartRect.Y + _pinchPy * scale * (1 - k);

        ApplyRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
        e.Handled = true;
    }

    private void Root_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mode == Mode.None) return;
        _mode = Mode.None;
        try { e.Pointer.Capture(null); } catch { }
        e.Handled = true;
    }

    private void Root_WheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        if (delta == 0) return;

        var factor = delta > 0 ? 1.1 : 1.0 / 1.1;
        var w = _pxW * factor;
        var h = _pxH * factor;
        if (w < MinSide || h < MinSide || w > MaxSide || h > MaxSide) return;

        var r = CurrentRect();
        ApplyRect(r.X, r.Y, (int)Math.Round(w), (int)Math.Round(h));
        e.Handled = true;
    }

    /// <summary>悬停在边上时把那一边亮一下（WinUI 不让外部改鼠标指针样式，只能给视觉反馈；Avalonia 同理沿用）。</summary>
    private void ShowHover(int zone)
    {
        if (zone == 0 || _mode != Mode.None)
        {
            HoverEdge.IsVisible = false;
            return;
        }

        const double t = 6;
        HoverEdge.Width = double.NaN;
        HoverEdge.Height = double.NaN;
        HoverEdge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        HoverEdge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        HoverEdge.Margin = new Thickness(0);

        switch (zone)
        {
            case 1: HoverEdge.Width = t; HoverEdge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; break;
            case 2: HoverEdge.Width = t; HoverEdge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right; break;
            case 3: HoverEdge.Height = t; HoverEdge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top; break;
            case 4: HoverEdge.Height = t; HoverEdge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom; break;
            case 5: HoverEdge.Width = t; HoverEdge.Height = t; HoverEdge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; HoverEdge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top; break;
            case 6: HoverEdge.Width = t; HoverEdge.Height = t; HoverEdge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right; HoverEdge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top; break;
            case 7: HoverEdge.Width = t; HoverEdge.Height = t; HoverEdge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; HoverEdge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom; break;
            case 8: HoverEdge.Width = t; HoverEdge.Height = t; HoverEdge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right; HoverEdge.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom; break;
        }

        HoverEdge.IsVisible = true;
    }

    private void CloseSticker()
    {
        ScreenCapture.Log("贴图关闭");
        try { Close(); } catch { }
    }

    /// <summary>去掉系统给无边框窗画的那 1px 边框（DWMWA_BORDER_COLOR = 34 / DWMWA_COLOR_NONE = 0xFFFFFFFE）。
    /// 不去的话，浅色底上那一圈就是肉眼看到的"黑边"。Win10 1809 之前 / Win7 不认识这个属性，调用失败即可忽略。</summary>
    internal static void KillSystemBorder(IntPtr hwnd)
    {
        try
        {
            var none = unchecked((int)0xFFFFFFFE);
            NativeMethods.DwmSetWindowAttribute(hwnd, 34, ref none, sizeof(int));
        }
        catch { }
    }
}
