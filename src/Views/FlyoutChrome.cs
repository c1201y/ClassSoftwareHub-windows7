using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 浮窗的「窗口外壳」——音量两个浮窗（主音量 / 合成器）共用，**完全照 <see cref="ToolSidebarWindow"/> 那一套**：
///
///   · 材质：亚克力打底（<see cref="Platform.Backdrop"/>）+ <see cref="ApplyMaterial"/> 压一层纯色薄纱
///     （深色压深、浅色压白）。跟边条**一模一样**，两个东西摆在一起才像一个整体。
///   · 无边框 + 不进任务栏 + 圆角 8 + 去白边，走 <see cref="Core.WindowChrome"/>。
///     ⚠️ 2026-10-02：圆角不能再只靠 <c>WindowChrome.RemoveBorder</c> —— 那里面写的是
///     <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>，**Win11 才认**，Win7 上静默失败（用户看到的
///     "悬浮窗没有圆角"就是它）。现在由 <see cref="Core.RoundedCorners"/> 兜底：
///     Win11 走 DWM，Win7/10 用 <c>SetWindowRgn</c> 把窗口形状裁成圆角矩形。
///   · <see cref="Core.ThemeCompat"/> 管深浅色跟着设置走。
///
/// ⚠️ 规矩（都是踩出来的，别改）：
///   1. <see cref="Finish"/> 必须在窗口**显示之后**再调 —— 显示前改窗口样式会让窗口显示不出来；
///   2. 材质随主题变：<c>ActualThemeVariantChanged</c> 里重压一次薄纱 + 重画一次窗口边框；
///   3. 别在这儿自己写无边框那套，去用 WindowChrome。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///   · 原版暴露的 <c>AppWindow</c> 对象收成了本类自己的一组属性/方法（<see cref="Position"/> /
///     <see cref="SizePx"/> / <see cref="MoveResize"/>），底层全走 Win32 SetWindowPos / GetWindowRect
///     —— 无边框窗口外框 = 客户区，坐标语义与原版 AppWindow 完全一致；
///   · 亚克力材质必须等窗口显示之后才上（构造期拿不到 HWND），挪进 <see cref="Present"/>；
///   · SystemBackdrop → <c>Backdrop.Apply(window, null, "acrylic")</c>（Win7 自动降级毛玻璃/纯色）。
/// </summary>
public sealed class FlyoutChrome
{
    private readonly Window _window;
    private readonly Control _root;
    private readonly Border _panel;
    private readonly RoundedCorners _corners;
    private bool _backdropApplied;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    /// <summary>窗口句柄（注册给 <see cref="VolumeFlyoutGroup"/> 判焦点用）。</summary>
    public IntPtr Hwnd { get; private set; }

    public FlyoutChrome(Window window, Control root, Border panel, string title)
    {
        _window = window;
        _root = root;
        _panel = panel;

        Hwnd = Backdrop.TryGetHwnd(window);      // 窗口没显示前可能是 IntPtr.Zero，Present 里会再取
        Title = title;
        _window.ShowInTaskbar = false;           // 别进 Alt+Tab、别占任务栏
        _window.SystemDecorations = SystemDecorations.None;
        _window.CanResize = false;
        _window.Topmost = true;

        // 圆角：Win11 走 DWM，Win7/10 走窗口区域裁剪（见 Core/RoundedCorners）。
        // 这个窗口是"宽固定 + 高实测"的，尺寸每次都可能变 → 由 RoundedCorners 挂在 Resized 上自动重算。
        _corners = RoundedCorners.Attach(_window);

        ApplyMaterial();

        // ⚠️ 2026-10-03（第 11 轮）：鼠标一进浮窗就把它的激活态补上（Activate 幂等，重复调没副作用）。
        //    背景：浮窗弹出时前台常常还在边条/别的进程上，Windows 对"点击未激活窗口"的处理是
        //    先发 WM_MOUSEACTIVATE 走激活流程 —— 大多数情况按下照常送达，但工具窗口（WS_EX_TOOLWINDOW）
        //    在某些路径下第一次按下会被激活流程搅掉。悬停时先把激活补好，就绕开了这条路径。
        //    （注：开发机上实测的"SendInput 按下 100% 被吞"后来查明是**自动化宿主**拦截注入式按下 ——
        //    光标被拽回 (0,0)、前台被宿主抢走，与本应用无关；真机无此拦截层。这个兜底保留，无害。）
        root.PointerEntered += (_, _) =>
        {
            try { _window.Activate(); } catch { }
            EnsureForeground();
        };

        root.ActualThemeVariantChanged += (_, _) =>
        {
            ApplyMaterial();
            try { Finish(_root.ActualThemeVariant == ThemeVariant.Dark); } catch { }
        };
    }

    /// <summary>浮窗标题（原版 AppWindow.Title）。</summary>
    public string Title
    {
        get => _window.Title ?? "";
        set => _window.Title = value;
    }

    /// <summary>
    /// 侧边栏同款薄纱：⚠️ 2026-10-03（用户第 12 轮）改**全不透明强制色**：
    /// 深色就是黑面板（#202020）、浅色就是白面板（#FCFCFC），不再留"一丝透"。
    /// 背景：Win7 的 Aero 毛玻璃本身颜色偏灰蓝，之前深色只压 95α、浅色压 205α，
    /// 底下的毛玻璃永远透出一层灰 —— 用户要的是明确的黑/白，那就直接给死色。
    /// 色值跟 <see cref="ToolSidebarWindow"/> 里的 ApplyPanelBrush **逐字一致**，改就一起改。
    /// </summary>
    public void ApplyMaterial()
    {
        try
        {
            var dark = _root.ActualThemeVariant == ThemeVariant.Dark;
            _panel.Background = new SolidColorBrush(dark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xFC, 0xFC, 0xFC));
        }
        catch { }
    }

    /// <summary>窗口所在显示器的缩放比（96dpi = 1.0）。</summary>
    public double Scale => _window.RenderScaling > 0 ? _window.RenderScaling : 1.0;

    /// <summary>当前在屏幕上的矩形。</summary>
    public PixelRect? CurrentRect
    {
        get
        {
            try
            {
                var p = Position;
                var s = SizePx;
                return new PixelRect(p.X, p.Y, s.Width, s.Height);
            }
            catch { return null; }
        }
    }

    /// <summary>窗口当前位置（物理像素）。</summary>
    public PixelPoint Position
    {
        get
        {
            EnsureHwnd();
            if (Hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(Hwnd, out var r))
                return new PixelPoint(r.Left, r.Top);
            return _window.Position;
        }
    }

    /// <summary>窗口当前尺寸（物理像素）。</summary>
    public PixelSize SizePx
    {
        get
        {
            EnsureHwnd();
            if (Hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(Hwnd, out var r))
                return new PixelSize(r.Width, r.Height);
            var scale = Scale;
            return new PixelSize((int)Math.Round(_window.ClientSize.Width * scale),
                                 (int)Math.Round(_window.ClientSize.Height * scale));
        }
    }

    /// <summary>
    /// 一次把「起点位置 + 最终尺寸」设下去，再显形抢前台。
    /// ⚠️ 位置和尺寸必须**一次**设：分开写的话中间那一帧会被系统画出来，就是"闪一下再滑"的来源。
    /// </summary>
    public void Present(PixelPoint start, int width, int height)
    {
        EnsureHwnd();

        // ⚠️⚠️ 首次显示**必须**走 Avalonia 自己的 Show()（2026-10-02 实机修的坑，别改回去）
        //
        //   原来这里只调 Win32 的 ShowWindow(SW_SHOW)，结果实机截图里浮窗是一块**纯白**的方块：
        //   外框尺寸对（600×272 物理 = 300×136 dip），里面图标、文字、滑块一个都没有。
        //   原因：Avalonia 的 Window 在没走过 Show() 之前，TopLevel 侧一直是"不可见"，
        //   **整棵视觉树一个像素都不画**，屏幕上剩下的只是 Win32 那个默认白底窗口。
        //
        //   同一个仓库里能正常显示的窗口全都调了 Show()：
        //       ToolSidebarWindow:439 / ToolPaletteWindow:148 / ClockFullscreenWindow:93 / StickerWindow:103
        //   只有 FlyoutChrome（音量 + 合成器这两个浮窗）漏了 —— 用户说的"太难看了还不能用"就是它。
        //
        //   先把位置尺寸按 DIP 摆好再 Show，避免用默认尺寸闪一帧；物理尺寸随后由 MoveResize 对齐。
        if (!_window.IsVisible)
        {
            // 再往前一步：把底色也先定下来，否则这一帧是"不透明的默认白" —— 就是用户说的
            // 「显示和隐藏时闪一下白色」。见 PrimeOpaqueBackdrop。
            PrimeOpaqueBackdrop();

            var scale = Scale;
            try
            {
                _window.Width = width / scale;
                _window.Height = height / scale;
                _window.Position = start;
            }
            catch { }
            _window.Show();
        }

        MoveResize(start, width, height);

        EnsureHwnd();
        EnsureForeground();

        // 亚克力底（Win7 → Aero 毛玻璃 → 纯色兜底）；必须等窗口显示之后（拿得到 HWND）
        if (!_backdropApplied)
        {
            _backdropApplied = true;
            try { Backdrop.Apply(_window, null, "acrylic"); } catch { }
        }
    }

    /// <summary>
    /// 把浮窗推到前台。⚠️ 裸 <c>SetForegroundWindow</c> 会被系统的**前台锁**静默拒绝
    /// （浮窗弹出时前台往往是别的进程；被拒绝的浮窗处于非激活态，**点它的第一下会被吃掉**
    /// —— 2026-10-03 本机实测：非前台的浮窗 SendInput 按下不产生任何 Avalonia 事件，
    /// 用户看到的就是"点了没反应/识别不出拖动还是点击"）。
    /// 这里用 <c>AttachThreadInput</c> 跟当前前台线程搭桥后再抢 —— 经典可靠的做法。
    /// </summary>
    private void EnsureForeground()
    {
        try
        {
            if (Hwnd == IntPtr.Zero) return;
            if (NativeMethods.GetForegroundWindow() == Hwnd) return;

            _ = NativeMethods.SetForegroundWindow(Hwnd);
            if (NativeMethods.GetForegroundWindow() == Hwnd) { ScreenCapture.Log("EnsureForeground: 直接管用"); return; }

            // 前台锁拒绝了 → 跟前台线程搭桥再试一次
            var fg = NativeMethods.GetForegroundWindow();
            var fgThread = fg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0;
            var myThread = NativeMethods.GetCurrentThreadId();
            if (fgThread != 0 && fgThread != myThread)
            {
                _ = NativeMethods.AttachThreadInput(myThread, fgThread, true);
                _ = NativeMethods.SetForegroundWindow(Hwnd);
                _ = NativeMethods.AttachThreadInput(myThread, fgThread, false);
            }

            try { _window.Activate(); } catch { }
            ScreenCapture.Log($"EnsureForeground: 搭桥后前台是自己={NativeMethods.GetForegroundWindow() == Hwnd}");
        }
        catch { }
    }

    /// <summary>外观收尾（去白边 + 圆角 + 深浅色）。**必须在窗口显示之后调**。</summary>
    public void Finish(bool dark)
    {
        EnsureHwnd();
        WindowChrome.RemoveBorder(Hwnd, rounded: true, dark: dark);
        _corners.Refresh();       // 显示之后才量得到真实尺寸，这里补裁一次
    }

    /// <summary>立刻藏起来（滑出动画播完后由动画回调调它）。</summary>
    /// <remarks>
    /// ⚠️ 走 Avalonia 的 <see cref="Window.Hide"/> 而不是裸 ShowWindow(SW_HIDE)：
    ///    <see cref="Present"/> 靠 <c>IsVisible</c> 判断"要不要补一次 Show()"，
    ///    裸 Win32 隐藏的话 Avalonia 侧还以为窗口是可见的 —— 下次再开就跳过了 Show()，
    ///    于是又变成那个"白块"（跟首次显示漏调 Show() 是同一个坑的另一半）。
    /// </remarks>
    public void HideDirect()
    {
        EnsureHwnd();
        try { _window.Hide(); } catch
        {
            try { if (Hwnd != IntPtr.Zero) _ = NativeMethods.ShowWindow(Hwnd, SW_HIDE); } catch { }
        }
    }

    /// <summary>挪动（物理像素；原版 AppWindow.Move）。</summary>
    public void Move(PixelPoint p)
    {
        EnsureHwnd();
        if (Hwnd == IntPtr.Zero) { _window.Position = p; return; }
        _ = NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, p.X, p.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>一次挪动+改尺寸（物理像素；原版 AppWindow.MoveAndResize）。</summary>
    public void MoveResize(PixelPoint p, int w, int h)
    {
        EnsureHwnd();
        if (Hwnd == IntPtr.Zero)
        {
            _window.Position = p;
            /* ⚠️ TopLevel.ClientSize 只读 → 等价的 Width/Height（DIP） */
            _window.Width = w / Scale;
            _window.Height = h / Scale;
            _corners.Refresh();
            return;
        }

        _ = NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, p.X, p.Y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

        // 尺寸变了 → 圆角区域要按新尺寸重裁。Avalonia 的 Resized 是异步派发的，
        // 这里同步补一次，免得中间那一两帧露出没裁到的直角/白角。
        _corners.Refresh();
    }

    // ══════════════════════════════════════════════════════════════════
    //  首帧防白闪
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 显示之前先把窗口底色定成"成品色"（跟面板同色系的不透明纯色）。
    ///
    /// <para>
    /// ── 为什么必须做（用户 2026-10-02：「显示和隐藏时会闪一下白色」）──────
    /// Avalonia 的窗口在 <c>Show()</c> 那一刻只做了一件事：让 Win32 窗口可见。
    /// 视效树的第一帧要等下一轮渲染才画上去 —— 中间那一帧，窗口是**不透明但空白**的，
    /// 而系统的默认窗口底色是**白的**。于是每次显示都会白闪一下。
    /// 显示前先把底色设成成品色，闪的就变成"成品色 → 成品色"，肉眼看不出。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 只在**没有模糊/透明支持**的环境下做（<see cref="OsInfo.SupportsAcrylicBlur"/> 为假）。
    ///    能透明时窗口本来就能透出桌面、也不会白闪，这时候设成不透明是倒退
    ///    （还会把亚克力盖掉）。这正是"关了 Aero 的教室机"才有的毛病。
    /// </para>
    /// </summary>
    private void PrimeOpaqueBackdrop()
    {
        if (OsInfo.SupportsAcrylicBlur) return;

        try
        {
            var dark = _root.ActualThemeVariant == ThemeVariant.Dark;
            _window.Background = new SolidColorBrush(dark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xF3, 0xF3, 0xF3));
        }
        catch
        {
            // 设不上就算了：顶多还是白闪一下，不影响功能
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  拖动浮窗
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 正被用户拖着走。给 <c>VolumeFlyoutGroup</c> 的鼠标监视用 —— 拖动过程中绝不收窗。
    /// </summary>
    public bool IsDragging { get; private set; }

    /// <summary>开始拖 / 拖完（调用方用来停滑动动画、记日志等；⚠️ 拖过不再"钉住"，2026-10-03 删）。</summary>
    public event EventHandler? DragStarted;
    public event EventHandler? DragEnded;

    private Control? _dragGrip;
    private Func<object?, bool>? _dragBlocked;

    /// <summary>
    /// 让浮窗可以被拖着移动（用户 2026-10-02：「悬浮窗无法移动位置」）。
    ///
    /// <para>
    /// 浮窗是 <see cref="SystemDecorations.None"/> 的无边框窗口，**没有标题栏**，
    /// 系统那套"拖标题栏移动"在这里根本不存在 —— 所以只能自己实现。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 位移用**屏幕光标坐标**算，不用 Avalonia 的事件坐标：
    ///    事件坐标是相对窗口的，窗口跟着鼠标一动，它又变回原值 → 阻尼成 0，窗口纹丝不动
    ///    （这个坑 <c>ToolSidebarWindow</c> 的收起拖拽也踩过，注释里写着"屏幕坐标算"）。
    /// ⚠️ 挂在 <c>Tunnel</c> 阶段：先看一眼，是滑块/按钮就**不接管**，让控件自己处理，
    ///    否则点滑块会被当成"拖窗口"，音量就调不了了。
    /// </summary>
    /// <param name="grip">拖动把手（整个窗口根就行）。</param>
    /// <param name="isInteractive">判断"按下的这个东西是不是该自己处理"（滑块、按钮）。</param>
    public void EnableDrag(Control grip, Func<object?, bool> isInteractive)
    {
        _dragGrip = grip;
        _dragBlocked = isInteractive;

        ScreenCapture.Log($"浮窗拖动把手已挂上（grip={grip.GetType().Name}, Bubble+handledEventsToo）");

        // ⚠️ 2026-10-03 真机反馈「悬浮窗识别不出来是拖动还是点击，全按点击处理了」的**真根因**：
        //    这里原来写的是 RoutingStrategies.Tunnel —— 但 Avalonia 的 PointerPressed 是
        //    **冒泡（Bubble）事件**（没有 WPF 那套 Preview 隧道路由），按 Tunnel 过滤的
        //    handler **一次都不会被调** → 拖动从没启动过，按下全落回滑块/按钮 = 全是"点击"。
        //    （console8 发出去的版本就是这个，真机必现；本机 --simulate-win7 现场复现：
        //    snip.log 里连"浮窗按下"都不打。）本仓库其余拖拽（SidebarLayoutPage 等）全是
        //    Bubble + handledEventsToo，跟齐。滑块/按钮的按下也会冒到这 —— 用 IsDragBlocked 挡回去。
        grip.AddHandler(InputElement.PointerPressedEvent, OnDragPressed, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        ScreenCapture.Log($"浮窗按下: source={e.Source?.GetType().Name ?? "null"}");
        try
        {
            if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed) return;
            var blocked = _dragBlocked?.Invoke(e.Source) == true;
            // ⚠️ 每次按下都记一笔（用户真机上"拖不动/全按点击"时，snip.log 里直接能看到
            //    按在了什么控件上、有没有被判定成交给控件 —— 不用再靠猜）。
            ScreenCapture.Log($"浮窗按下: source={e.Source?.GetType().Name ?? "null"} blocked={blocked}");
            if (blocked) return;      // 滑块 / 按钮 → 交给它自己

            // ⚠️ 2026-10-02 真机反馈「还是无法拖动」（第 8 轮那套自己算位移的实现在真机上不工作）：
            //    旧实现靠 Avalonia 指针捕获（e.Pointer.Capture）+ PointerMoved 里 SetWindowPos ——
            //    本机（Win11）SendInput 能拖，但真机 Win7 上大概率一按下就 PointerCaptureLost
            //    （侧边栏那套自算拖拽当时也报过"捕获丢失"，同一类坑）。
            //    改用**系统的模态移动循环**：与主窗 / 常用工具窗标题栏（HTCAPTION）同一机制，
            //    真机上拖主窗是正常的 → 这条路在真机上是验证过可用的。
            //    BeginMoveDrag 在 Windows 上就是 ReleaseCapture + WM_NCLBUTTONDOWN(HTCAPTION)，
            //    阻塞到用户松手为止；期间消息循环照常转（监视器 / 动画不受影响）。
            IsDragging = true;
            try { DragStarted?.Invoke(this, EventArgs.Empty); } catch { }

            _window.BeginMoveDrag(e);                // ← 阻塞：直到松手
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("浮窗拖动启动失败: " + ex.Message);
        }
        finally
        {
            var wasDragging = IsDragging;
            IsDragging = false;
            if (wasDragging)
            {
                try { DragEnded?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }
    }

    private void EnsureHwnd()
    {
        if (Hwnd == IntPtr.Zero) Hwnd = Backdrop.TryGetHwnd(_window);
    }
}

/// <summary>
/// 贴边浮窗的位置计算。两个概念要分清：
///   · **沿边方向**（left/right 时是 Y、top/bottom 时是 X）—— 浮窗在这条线上跟锚点对齐；
///   · **进退方向**（垂直那条边的方向）—— 浮窗从边上滑进来多少。
/// 坐标一律物理像素：dip × <see cref="FlyoutChrome.Scale"/>。
/// </summary>
public static class EdgeGeometry
{
    /// <summary>挨着锚点时留的缝（dip）。</summary>
    public const int GapDip = 8;

    /// <summary>贴上/下（横条）时，浮窗也走横版；贴左/右走竖版。</summary>
    public static bool IsFlat(string edge) => edge is "top" or "bottom";

    /// <summary>浮窗「厚度」：竖版是宽、横版是高。</summary>
    public static int Thickness(string edge, int w, int h) => IsFlat(edge) ? h : w;

    /// <summary>沿边方向上的长度：竖版是高、横版是宽。</summary>
    public static int Length(string edge, int w, int h) => IsFlat(edge) ? w : h;

    /// <summary>
    /// 挨着锚点（边条 / 主音量浮窗）往屏幕**里侧**排：
    /// 进退方向贴住锚点的内侧，沿边方向跟锚点**对齐居中**，最后夹进工作区别跑出屏幕。
    /// 返回（滑入起点 = 往边外退半块，最终位置）。
    /// </summary>
    public static (PixelPoint Start, PixelPoint Final) BesideAnchor(
        string edge, PixelRect anchor, PixelRect work, int w, int h, double scale)
    {
        var gap = (int)Math.Round(GapDip * scale);
        var flat = IsFlat(edge);

        // 沿边方向：以锚点这条边的中点为基准，浮窗自己居中
        var anchorCenter = flat ? anchor.X + anchor.Width / 2 : anchor.Y + anchor.Height / 2;
        var along = anchorCenter - (flat ? w : h) / 2;

        var final = edge switch
        {
            "left" => new PixelPoint(anchor.X + anchor.Width + gap, along),
            "top" => new PixelPoint(along, anchor.Y + anchor.Height + gap),
            "bottom" => new PixelPoint(along, anchor.Y - h - gap),
            _ => new PixelPoint(anchor.X - w - gap, along),
        };

        final = ClampToWork(final, work, w, h);

        // ⚠️ 起点只往屏幕外退**半块**：整块挪到屏幕外的窗口 DWM 常常不给它刷帧，
        //    滑进来的第一帧会发虚/卡顿（侧边栏当初就是踩了这个才改的）。
        var start = Outward(final, edge, Thickness(edge, w, h) / 2);
        return (start, final);
    }

    /// <summary>从某个位置往"屏幕外"退 <paramref name="distance"/> 像素（滑入起点 / 滑出终点）。</summary>
    public static PixelPoint Outward(PixelPoint p, string edge, int distance) => edge switch
    {
        "left" => new PixelPoint(p.X - distance, p.Y),
        "top" => new PixelPoint(p.X, p.Y - distance),
        "bottom" => new PixelPoint(p.X, p.Y + distance),
        _ => new PixelPoint(p.X + distance, p.Y),
    };

    /// <summary>整个窗口都在屏幕外（滑出要滑到底，屏幕边上不留残片）。</summary>
    public static PixelPoint FullyOut(PixelPoint p, string edge, int thickness) => Outward(p, edge, thickness + 2);

    /// <summary>把这条屏幕边当成一条零厚度的「锚点」（边条拿不到时的退路）。</summary>
    public static PixelRect EdgeBar(string edge, PixelRect work) => edge switch
    {
        "left" => new PixelRect(work.X, work.Y, 0, work.Height),
        "top" => new PixelRect(work.X, work.Y, work.Width, 0),
        "bottom" => new PixelRect(work.X, work.Y + work.Height, work.Width, 0),
        _ => new PixelRect(work.X + work.Width, work.Y, 0, work.Height),
    };

    /// <summary>把矩形夹进工作区（贴屏幕下边时会长出屏幕，这里兜住）。</summary>
    public static PixelPoint ClampToWork(PixelPoint p, PixelRect work, int w, int h)
    {
        var x = Math.Clamp(p.X, work.X, Math.Max(work.X, work.X + work.Width - w));
        var y = Math.Clamp(p.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
        return new PixelPoint(x, y);
    }
}

/// <summary>
/// 浮窗内容淡入（跟窗口滑动同时进行，别让字"啪"一下出现）。
/// ⚠️ 移植说明：原版走 ElementCompositionPreview 的合成器 Opacity 动画；
///    Avalonia 没有逐 Visual 的合成动画，改用 16ms 按帧计时器，缓出近似原版贝塞尔。
/// </summary>
public static class FlyoutFade
{
    private static DispatcherTimer? _timer;

    public static void In(Control element, double ms)
    {
        try
        {
            Stop();
            element.Opacity = 0f;

            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / ms, 0, 1);
                var e = 1 - Math.Pow(1 - t, 3);           // ease-out cubic（近似原版贝塞尔）
                element.Opacity = e;
                if (t >= 1)
                {
                    element.Opacity = 1;
                    Stop();
                }
            };
            _timer = timer;
            timer.Start();
        }
        catch
        {
            try { element.Opacity = 1.0; } catch { }
        }
    }

    private static void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}
