using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 侧边栏收起条的**隐形点击/滑动热区**（2026-10-17）。
///
/// <para>
/// ── 为什么需要（用户原话）────────────────────────────────────────────
/// 「优化一下竖线点击，用鼠标点必须对准，差一点点都不行，优化触控向屏幕内滑动展开」
/// 收起条本身就是一条 8dip（细线样式 6dip）宽的线，离屏幕边还留了 5dip 空隙：
///   · 鼠标：习惯把指针怼到屏幕边再点 —— 正好落在空隙里，点空；
///   · 触控：手指落点误差 10px 起步，8dip 的线基本点不着，更别说在它上面起滑。
/// </para>
///
/// <para>
/// ── 为什么不能直接把窗口加宽 ────────────────────────────────────────
/// Win7 上 Avalonia 11.2.8 拿不到透明窗口（见 <see cref="Backdrop"/> 顶部注释：
/// 没有 WS_EX_LAYERED / TransparencyLevel 恒为 None），窗口加宽多少就露出多少实色块，
/// 用户看到的就是「竖线又变粗了」——这条红线不能碰。
/// </para>
///
/// <para>
/// ── 做法 ──────────────────────────────────────────────────────────
/// 单独开一扇 **分层窗口（WS_EX_LAYERED）+ alpha = 1/255** 的原生弹窗，盖在
/// 「竖线 + 到屏幕边的空隙 + 竖线内侧一条带」上：
///   · 肉眼完全看不出来（alpha=1 在深/浅底色上的变化 ≤1/255，实测截图逐像素比对无差异）；
///   · 但它是实打实的窗口，鼠标和触摸（Win7 触摸会转成鼠标消息）都落在它身上；
///   · z 序压在收起条窗口**下面**（见 <see cref="Place"/> 的 hWndInsertAfter），
///     竖线自己那块照旧走 Avalonia 的点击/滑动，两条路互不打架。
/// 手势只认两种（都在本类里判）：
///   · **点一下**：按下→抬起、位移 ≤ 阈值（抖动容错）→ 展开；
///   · **朝屏幕里滑**：按下后沿"屏幕内侧"方向位移 ≥ 阈值 → 立刻展开（手指好滑）。
/// 悬停则回调给侧边栏把那条线点亮，告诉用户"这儿能点"。
/// </para>
///
/// <para>
/// ⚠️ 生命周期：只在**收起且可见**时摆出来；展开 / 隐藏 / 关窗立刻 <see cref="Hide"/>，
///    关窗时 <see cref="Dispose"/> 销毁原生窗口。创建失败只是没有容错区，点击照旧走 Avalonia。
/// </para>
/// </summary>
internal sealed class SidebarHitPad : IDisposable
{
    // ── 窗口类（全进程一份）──────────────────────────────────────

    private const string ClassName = "ClassSoftwareHub.SidebarHitPad";

    /// <summary>⚠️ 委托必须用静态字段托住，否则被 GC 回收后窗口过程就野了（Avalonia 里同样的坑）。</summary>
    private static NativeMethods.WNDPROC? _wndProc;

    private static ushort _classAtom;

    /// <summary>窗口句柄 → 实例（WndProc 是静态的，靠它找回对象；同一 UI 线程创建，无并发）。</summary>
    private static readonly Dictionary<IntPtr, SidebarHitPad> _byHwnd = new();

    private static readonly IntPtr HWND_TOPMOST = NativeMethods.HWND_TOPMOST;

    // ── 实例 ────────────────────────────────────────────────────

    private readonly IntPtr _hwnd;
    private readonly string _edge;          // left / right / top / bottom —— 决定"朝屏幕里"是哪个方向
    private readonly Action _onExpand;
    private readonly Action<bool> _onHover;

    private bool _down;                     // 左键按下中
    private bool _fired;                    // 本次手势已经展开过（防重复）
    private bool _trackingLeave;            // 已挂 TrackMouseEvent(TME_LEAVE)
    private bool _hovering;                 // 当前是否在热区里悬停
    private int _startX, _startY;           // 按下点（客户区像素）
    private int _tapPx = 14;                // 点击容错（物理像素，调用方按 DPI 传）
    private int _swipePx = 10;              // 朝屏幕里滑的阈值（物理像素）

    private SidebarHitPad(IntPtr hwnd, string edge, Action onExpand, Action<bool> onHover)
    {
        _hwnd = hwnd;
        _edge = edge;
        _onExpand = onExpand;
        _onHover = onHover;
    }

    /// <summary>创建一块隐形热区窗口。失败返回 <c>null</c>（调用方按"没有容错区"继续跑就行）。</summary>
    /// <param name="edge">贴的边：left / right / top / bottom（决定"朝屏幕里"的方向）。</param>
    /// <param name="onExpand">点一下 / 朝屏幕里滑够了 → 调它展开。</param>
    /// <param name="onHover">悬停进入/离开 → 调它点亮竖线（同一 UI 线程，可直接改控件）。</param>
    public static SidebarHitPad? TryCreate(string edge, Action onExpand, Action<bool> onHover)
    {
        try
        {
            if (!EnsureClass())
            {
                Core.AppLog.Info("sidebar", "热区: 窗口类注册失败，跳过（不影响点击，只是没有容错区）");
                return null;
            }

            var hinst = NativeMethods.GetModuleHandle(null);
            var hwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE,
                ClassName, "", NativeMethods.WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                Core.AppLog.Info("sidebar", "热区: CreateWindowEx 失败，跳过（不影响点击）");
                return null;
            }

            var pad = new SidebarHitPad(hwnd, edge, onExpand, onHover);
            _byHwnd[hwnd] = pad;

            // alpha = 1/255：肉眼看不见，但**整块矩形都吃鼠标**（只有逐像素 alpha 的
            // UpdateLayeredWindow 才会按 alpha 做点击穿透，这里用的是整窗 alpha，不会穿透）。
            if (!NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 1, NativeMethods.LWA_ALPHA))
                Core.AppLog.Info("sidebar", "热区: 设置分层透明度失败（不影响点击）");

            return pad;
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("sidebar", "热区: 创建异常 " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 把热区摆到指定矩形（屏幕物理像素）并显示。
    /// </summary>
    /// <param name="rect">热区矩形（含竖线本体 + 到屏幕边的空隙 + 内侧容错带）。</param>
    /// <param name="belowHwnd">z 序参照：热区排在这扇窗口**下面**（传收起条自己的 hwnd）。</param>
    /// <param name="tapPx">点击容错（物理像素）。</param>
    /// <param name="swipePx">朝屏幕里滑的阈值（物理像素）。</param>
    public void Place(PixelRect rect, IntPtr belowHwnd, int tapPx, int swipePx)
    {
        try
        {
            _tapPx = Math.Max(4, tapPx);
            _swipePx = Math.Max(4, swipePx);

            NativeMethods.SetWindowPos(
                _hwnd,
                belowHwnd == IntPtr.Zero ? HWND_TOPMOST : belowHwnd,
                rect.X, rect.Y, rect.Width, rect.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            Core.AppLog.Info("sidebar", "热区: 摆放失败 " + ex.Message);
        }
    }

    /// <summary>收起来（展开 / 隐藏 / 关窗时调）。窗口不销毁，下次 <see cref="Place"/> 直接复用。</summary>
    public void Hide()
    {
        try
        {
            ResetGesture();
            if (_hovering) { _hovering = false; _onHover(false); }
            NativeMethods.ShowWindow(_hwnd, 0 /* SW_HIDE */);
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            _byHwnd.Remove(_hwnd);
            NativeMethods.DestroyWindow(_hwnd);
        }
        catch { }
    }

    // ── 窗口过程 ────────────────────────────────────────────────

    private static bool EnsureClass()
    {
        if (_classAtom != 0) return true;

        try
        {
            _wndProc ??= WndProcThunk;
            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = _wndProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = NativeMethods.GetModuleHandle(null),
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,          // 箭头用系统的，不自己画
                hbrBackground = (IntPtr)(5 /* COLOR_WINDOW */ + 1),
                lpszMenuName = null,
                lpszClassName = ClassName,
                hIconSm = IntPtr.Zero,
            };

            _classAtom = NativeMethods.RegisterClassEx(ref wc);
            // 0 = 失败；1410 = 类已存在（另一个实例先注册了）—— 也算成功
            if (_classAtom == 0 && Marshal.GetLastWin32Error() != 1410) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr WndProcThunk(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_byHwnd.TryGetValue(hwnd, out var pad) && pad is not null)
                return pad.HandleMessage(hwnd, msg, wParam, lParam);
        }
        catch { }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // 点它别抢焦点：老师 PPT / 浏览器还在前台，侧边栏只是个浮层
            case NativeMethods.WM_MOUSEACTIVATE:
                return (IntPtr)NativeMethods.MA_NOACTIVATE;

            // 默认就是客户区（显式写出来，免得某些系统主题把它当非客户区吞掉）
            case NativeMethods.WM_NCHITTEST:
                return (IntPtr)NativeMethods.HTCLIENT;

            case NativeMethods.WM_MOUSEMOVE:
            {
                if (!_down)
                {
                    if (!_trackingLeave)
                    {
                        _trackingLeave = true;
                        var tme = new NativeMethods.TRACKMOUSEEVENT
                        {
                            cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                            dwFlags = NativeMethods.TME_LEAVE,
                            hwndTrack = hwnd,
                            dwHoverTime = 0,
                        };
                        NativeMethods.TrackMouseEvent(ref tme);
                    }
                    if (!_hovering) { _hovering = true; _onHover(true); }
                    return IntPtr.Zero;
                }

                // 按下后：朝屏幕里滑够阈值 → 立刻展开（手指滑一下就能开）
                var (mx, my) = ClientPoint(lParam);
                if (!_fired && InwardDelta(mx, my) >= _swipePx) Fire();
                return IntPtr.Zero;
            }

            case NativeMethods.WM_LBUTTONDOWN:
            {
                ResetGesture();
                _down = true;
                (_startX, _startY) = ClientPoint(lParam);
                NativeMethods.SetCapture(hwnd);
                if (!_hovering) { _hovering = true; _onHover(true); }
                return IntPtr.Zero;
            }

            case NativeMethods.WM_LBUTTONUP:
            {
                if (!_down) return IntPtr.Zero;
                _down = false;
                NativeMethods.ReleaseCapture();

                if (_fired) return IntPtr.Zero;

                // 点一下（位移在容错范围内）：这是最常用的路径，必须"怎么点都中"
                var (ux, uy) = ClientPoint(lParam);
                var moved = Math.Max(Math.Abs(ux - _startX), Math.Abs(uy - _startY));
                if (moved <= _tapPx) Fire();
                return IntPtr.Zero;
            }

            case NativeMethods.WM_CAPTURECHANGED:
                _down = false;
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSELEAVE:
            {
                _trackingLeave = false;
                if (_hovering && !_down) { _hovering = false; _onHover(false); }
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ── 手势工具 ────────────────────────────────────────────────

    /// <summary>lParam 低 16 位 = x、高 16 位 = y（客户区坐标，带符号）。</summary>
    private static (int X, int Y) ClientPoint(IntPtr lParam)
    {
        var v = lParam.ToInt64();
        var x = (short)(v & 0xFFFF);
        var y = (short)((v >> 16) & 0xFFFF);
        return (x, y);
    }

    /// <summary>从按下点往**屏幕里**挪了多少像素（正 = 朝屏幕里，也就是"把面板拉出来"的方向）。</summary>
    private int InwardDelta(int x, int y) => _edge switch
    {
        "left" => x - _startX,        // 贴左边：往右 = 屏幕里
        "right" => _startX - x,       // 贴右边：往左 = 屏幕里
        "top" => y - _startY,         // 贴上边：往下 = 屏幕里
        _ => _startY - y,             // 贴下边：往上 = 屏幕里
    };

    private void ResetGesture()
    {
        _down = false;
        _fired = false;
    }

    private void Fire()
    {
        _fired = true;
        try { NativeMethods.ReleaseCapture(); } catch { }
        try { _onExpand(); } catch (Exception ex) { Core.AppLog.Info("sidebar", "热区: 展开回调异常 " + ex.Message); }
    }
}
