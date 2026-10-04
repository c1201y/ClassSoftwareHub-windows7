using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 自绘窗口框架 —— 把窗口的原生标题栏/边框「吃掉」，让应用内那条 48px 标题栏真正成为窗口顶边，
/// <b>同时完整保留系统的拖拽缩放</b>。
///
/// <para>
/// ── 为什么不能照搬 <see cref="WindowChrome.MakeBorderless"/> ───────────────────
/// 那条路是把窗口样式换成 <c>WS_POPUP</c>（WS_CAPTION / WS_THICKFRAME 全拆掉）。
/// 结果是窗口"干净"了，但系统从此不当它是个正常窗口：<b>缩放边、Aero 吸附、
/// Win + 方向键、任务栏右键菜单、系统阴影会一起消失</b>。浮窗（音量条那种）无所谓，
/// 主窗口不行 —— 这正是 <see cref="WindowChrome"/> 里那段警告的由来。
/// </para>
///
/// <para>
/// ── 这里走的是另一条路：只改消息，不改样式 ────────────────────────────────
/// <list type="number">
///   <item>
///     <b>WM_NCCALCSIZE</b> → 返回 0，声明「非客户区是 0 像素」。
///     系统于是不再绘制标题栏与边框，客户区自动铺满整个窗口。
///     但窗口<b>仍然是</b>一个带 WS_THICKFRAME 的正常窗口。
///   </item>
///   <item>
///     <b>WM_NCHITTEST</b> → 系统问「鼠标这一点算什么」，我们在窗口四周 6px 内回答
///     HTLEFT / HTRIGHT / HTTOP…，在标题栏回答 HTCAPTION。
///     缩放与拖动<b>依然是系统自己做的</b>（还是 DefWindowProc 那套 modal loop），
///     所以手感、光标形状、吸附行为与原生窗口一模一样。
///   </item>
/// </list>
/// </para>
///
/// <para>
/// ── 三个必须自己补的坑 ────────────────────────────────────────────────
/// <list type="bullet">
///   <item>
///     <b>① Win7 最大化会溢出屏幕。</b>
///     Win10+ 的最大化矩形就等于工作区，直接返回 0 没问题；
///     Win7 的最大化矩形是「工作区 + 一圈边框厚度」（左右各多 8px 左右），照抄就会盖住任务栏。
///     所以最大化时用 <c>GetMonitorInfo</c> 的 rcWork 覆写客户区矩形。
///   </item>
///   <item>
///     <b>② 标题栏上的按钮得从拖动区里挖掉。</b>
///     整条标题栏返回 HTCAPTION 会把按钮的点击一起吞掉（系统只认拖动），
///     所以由 <see cref="InteractiveRegions"/> 报上按钮矩形，命中的地方照常返回 HTCLIENT，
///     让 Avalonia 收到点击。
///   </item>
///   <item>
///     <b>③ 关掉 DWM 合成的 Win7 上，缩放时还会冒出原生边框。</b>
///     只返回 0 不够 —— 系统会在缩放途中用主题引擎补画非客户区。
///     对策是三件事一起上：吞掉 <c>WM_NCPAINT / WM_NCUAHDRAWCAPTION / WM_NCUAHDRAWFRAME</c>
///     与 <c>WM_NCACTIVATE</c>；剥掉窗口类的 <c>CS_HREDRAW|CS_VREDRAW</c>（缩放不再整窗重画）；
///     再把 <c>WM_ERASEBKGND</c> 改成"只填新暴露的条带"。
///   </item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 实测说明：开发机是 Win11，Win7 分支靠 <c>--simulate-win7</c> 走
/// （见 <see cref="OsInfo.SimulateWin7"/>）。上面第 ① 条只在真 Win7 上才会真正改变结果 ——
/// 在 Win11 上它是无害的空转（rcWork 与最大化矩形本来就相同）。
/// </para>
///
/// <para>
/// ⚠️ 为什么用 <c>Win32Properties.AddWndProcHookCallback</c> 而不是自己 SetWindowLong 替换 WndProc：
/// Avalonia 的 <c>WindowImpl.WndProcMessageHandler</c> 把 hook 放在<b>最前面</b>调用，
/// <c>handled = true</c> 就完全接管、绕过它自己的消息处理（已核对 11.2.8 源码）。
/// 而 Avalonia 自己<b>从不处理</b> WM_NCHITTEST（一律走 DefWindowProc），
/// 所以这个 hook 与框架不会打架。
/// </para>
/// </summary>
public sealed class SelfDrawnFrame
{
    private readonly Window _window;

    /// <summary>⚠️ 必须留字段：委托一旦被 GC，原生回调就指向野指针，下一步直接崩。</summary>
    private readonly Win32Properties.CustomWndProcHookCallback _hook;

    private bool _attached;

    /// <summary>可缩放边框的宽度（dip）。太窄点不中，太宽会吃掉内容边缘的点击。</summary>
    public double ResizeBorder { get; set; } = 6;

    /// <summary>标题栏高度（dip）。这一段返回 HTCAPTION，交给系统做原生拖动。</summary>
    public double TitleBarHeight { get; set; } = 48;

    /// <summary>
    /// 要不要报四周的缩放命中。<b>不可缩放的窗口必须设 false</b>
    /// （比如内置工具窗口：它没有 <c>WS_THICKFRAME</c>，报了 HTLEFT 系统也无从下手，
    /// 白让鼠标在边上变成双向箭头、还抢掉内容边缘的点击）。
    /// </summary>
    public bool ResizeEnabled { get; set; } = true;

    /// <summary>
    /// 排障开关：设 true 立刻退回原生边框（消息照收，但一律不接管）。用来现场对比"是不是这套改动引起的"。
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>
    /// 给 <see cref="EraseColorProvider"/> 用的现成实现：从控件树里解析出「这个窗口的底色」。
    ///
    /// <para>
    /// 顺序：先按 FluentAvalonia / 原版的几个常用"页面底色"资源键找，
    /// 找不到再退回控件自己的 <see cref="Border.Background"/>。
    /// 只接受**完全不透明**的纯色 —— 半透明的填上去等于没填（底下的残影照样透出来）。
    /// </para>
    /// </summary>
    public static Func<Color?> EraseColorFrom(Control root)
    {
        return () =>
        {
            try
            {
                // ⚠️⚠️ 必须把 ActualThemeVariant 传给 TryFindResource（2026-10-02 真机反馈定论）。
                //    不带变体的重载走 ThemeVariant.Default，**不查 ThemeDictionaries** ——
                //    深色模式下会取回浅色那套（#F9F9F9 近白），于是窗口关掉/别的窗口移走时
                //    露出来的区域就被这条擦除填成近白 →「所有窗口关闭闪一下白」「浮窗拖快了留白边」。
                //    和 PopupSurfaceFix 深色下拉白边是同一类坑（那里已经踩过一次）。
                ThemeVariant variant;
                try { variant = root.ActualThemeVariant; }
                catch { variant = ThemeVariant.Dark; }

                foreach (var key in new[]
                         {
                             "SolidBackgroundFillColorBaseBrush",
                             "ApplicationPageBackgroundThemeBrush",
                             "LayerFillColorDefaultBrush",
                         })
                {
                    if (!root.TryFindResource(key, variant, out var value)) continue;
                    if (value is ISolidColorBrush brushed && brushed.Color.A == 0xFF)
                    {
                        // 只记第一条：真机上「关闭窗口还闪不闪白」就看这行 ——
                        // 深色模式应当是 #1C1C1C / #202020 一类的深色；若打出浅色值说明变体又没传对。
                        if (!_eraseColorLogged)
                        {
                            _eraseColorLogged = true;
                            PortLog.Step($"擦除底色: {brushed.Color}（主题 {variant}）—— "
                                         + "窗口关闭/浮窗移走时露出的区域先填这个色，再由渲染接手");
                        }
                        return brushed.Color;
                    }
                }

                // 资源取不到就退回控件自己那层底 —— Control 基类上有 Background 的只有
                // Panel / Border / TemplatedControl 这几支，逐个试一遍即可。
                var own = root switch
                {
                    Panel panel => panel.Background,
                    Border border => border.Background,
                    TemplatedControl templated => templated.Background,
                    _ => null,
                };
                if (own is ISolidColorBrush solid && solid.Color.A == 0xFF) return solid.Color;
            }
            catch
            {
                // 控件树正在重建时可能取不到资源 —— 返回 null 让调用方走默认重画
            }

            return null;
        };
    }

    /// <summary>
    /// 标题栏里<b>不能</b>当拖动区的矩形（dip，窗口坐标）—— 按钮、输入框、下拉框之类的交互件。
    /// 每次命中检测时回调一次（标题栏控件不多，开销可忽略；换来的好处是不用维护缓存/失效通知）。
    /// </summary>
    public Func<IReadOnlyList<Rect>>? InteractiveRegions { get; set; }

    /// <summary>
    /// 擦背景用的颜色（窗口自身的底色）。返回 null 就退回"什么都不填"，交给 Avalonia 自己重画。
    ///
    /// <para>
    /// 为什么要给这个：窗口类注册时背景刷是 <c>NULL</c>（实测），系统**不会**替我们擦背景。
    /// 缩放时新暴露出来的那一条带如果不主动填色，就会留上一帧的残影（在无 DWM 合成的
    /// Win7 上表现为一条黑/白边，看着就像"原生边框又冒出来了"）。
    /// 这里只填 <c>GetUpdateRect</c> 报出来的那一块，**不做整窗擦除** —— 整窗擦会闪。
    /// </para>
    /// </summary>
    public Func<Color?>? EraseColorProvider { get; set; }

    /// <summary>窗口第一次收到消息时剥掉窗口类的 <c>CS_HREDRAW|CS_VREDRAW</c>（见 <see cref="StripFullWindowErase"/>）。</summary>
    private bool _classStyleStripped;

    /// <summary>
    /// 现在是不是处在系统的「改大小 / 移动」模态循环里（见 <see cref="NativeMethodsEx.WM_ENTERSIZEMOVE"/>）。
    ///
    /// <para>
    /// 拖拽期间系统每移动一像素就改一次尺寸，<c>Resized</c> / <c>PositionChanged</c> 会密集触发。
    /// 应用层看到 <c>true</c> 就应该只做"必须跟着走"的最小工作 ——
    /// 发网页脚本、重算浮层落点这类重活攒到 <see cref="SizeMoveFinished"/> 再一次性做完。
    /// 这正是用户报的「拖拽调整大小卡卡的」的解法。
    /// </para>
    /// </summary>
    public bool InSizeMove { get; private set; }

    /// <summary>拖拽缩放 / 移动的模态循环结束（鼠标松开）。攒下的重活挂这里做一次。</summary>
    public event EventHandler? SizeMoveFinished;

    private SelfDrawnFrame(Window window)
    {
        _window = window;
        _hook = OnWndProc;
    }

    /// <summary>给窗口挂上自绘框架。</summary>
    /// <exception cref="PlatformNotSupportedException">
    /// 平台后端不是 Win32 时抛出 —— <c>Win32Properties.AddWndProcHookCallback</c> 在非 Win32 后端上
    /// 是**静默不做任何事**的，不主动炸出来的话，调用方会以为挂上了、实际什么都拦不到。
    /// </exception>
    public static SelfDrawnFrame Attach(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        // 防静默失败：Avalonia 的 AddWndProcHookCallback 只在 PlatformImpl 实现
        // IWin32OptionsTopLevelImpl 时才真的订阅，否则直接 return —— 一点提示都没有。
        var impl = window.PlatformImpl;
        if (impl is null)
            throw new InvalidOperationException("窗口的 PlatformImpl 尚未建立，此时挂 WndProc 钩子不会生效。");

        var implName = impl.GetType().FullName ?? impl.GetType().Name;
        if (implName.IndexOf("Win32", StringComparison.Ordinal) < 0)
            throw new PlatformNotSupportedException($"自绘窗口框架只支持 Win32 后端，当前是 {implName}。");

        var frame = new SelfDrawnFrame(window);
        Win32Properties.AddWndProcHookCallback(window, frame._hook);
        frame._attached = true;
        return frame;
    }

    /// <summary>摘掉钩子（窗口关闭时用不着，但留着方便排障时热切换）。</summary>
    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        try { Win32Properties.RemoveWndProcHookCallback(_window, _hook); } catch { }
    }

    // ============================================================
    // WndProc
    // ============================================================

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Suspended) return IntPtr.Zero;

        // 窗口类样式只剥一次 —— 它影响的是整个类，每次消息都查一遍纯属浪费。
        if (!_classStyleStripped)
        {
            _classStyleStripped = true;
            StripFullWindowErase(hWnd);
            DisableDwmNonClientRendering(hWnd);
        }

        switch (msg)
        {
            case NativeMethodsEx.WM_NCCALCSIZE:
                // wParam == FALSE（0）是"给定窗口矩形算客户区"的老式调用，交给系统。
                // 只有 wParam == TRUE（1）这次才是我们要接管的。
                if (wParam == IntPtr.Zero) return IntPtr.Zero;

                ConfineMaximizedToWorkArea(hWnd, lParam);
                handled = true;
                return IntPtr.Zero;      // ← 客户区 = 传进来的窗口矩形（即整窗铺满）

            case NativeMethodsEx.WM_NCHITTEST:
                return OnHitTest(hWnd, lParam, ref handled);

            // ── 拖拽缩放进行中：只做最省的事，重活等 WM_EXITSIZEMOVE ──
            // 不接管这两条消息（系统还要靠它跑模态循环），只是记个状态给应用层用。
            case NativeMethodsEx.WM_ENTERSIZEMOVE:
                InSizeMove = true;
                _sizeAtEnter = CurrentSize(hWnd);      // 记下进入时的尺寸（用来分辨"缩放"还是"纯移动"）
                break;

            case NativeMethodsEx.WM_EXITSIZEMOVE:
                InSizeMove = false;
                try { SizeMoveFinished?.Invoke(this, EventArgs.Empty); } catch { }

                // ⚠️ 2026-10-03（用户第 16 轮「常用工具窗口会时不时出现黑边」）：
                //    调完大小补一次整窗重画。窗口类的 CS_HREDRAW|CS_VREDRAW 被我们剥掉了
                //    （缩放时不再整窗重画，也正因如此"新暴露的那块"没人管），
                //    在没有 DWM 合成的 Win7 上就表现为窗口边上一圈黑 —— 见 OnEraseBackground。
                //
                // ⚠️⚠️ 2026-10-04（用户第 17 轮「拖动位置是会有残影」）：但**只有尺寸真变过才补**。
                //    这一条原来是无条件执行的，而 WM_EXITSIZEMOVE 在"只拖动位置、不改大小"时
                //    同样会来 —— 于是每次拖完窗口，整个客户区先被填成底色、再等 Avalonia 重画，
                //    中间那一帧就是用户看到的**残影/闪一下**。
                //    缩放场景仍然需要它（那正是黑边的来源，不能一起删掉），所以按尺寸是否变化分流。
                if (CurrentSize(hWnd) != _sizeAtEnter)
                {
                    RequestFullRepaint(hWnd, "尺寸调整结束");
                }
                else
                {
                    PortLog.Step("拖动结束: 尺寸没变（纯移动）→ 不整窗补画，避免残影");
                }
                break;

            case NativeMethodsEx.WM_SIZE:
                // 系统模态缩放循环里每帧都会来一次，别整窗重画（会闪）；非拖动的尺寸变化
                // （程序化改尺寸、最大化/还原）结束不了 WM_EXITSIZEMOVE，就从这里补。
                if (!InSizeMove) RequestFullRepaint(hWnd, "窗口尺寸变化");
                break;

            // ── 主题 / 合成状态变化：DWM 与主题引擎会借机重读非客户区策略 ──
            // 2026-10-03（用户第 16 轮「还是偶尔冒出原生小按钮」）：这两个时机各重申一次。
            case NativeMethodsEx.WM_THEMECHANGED:
            case NativeMethodsEx.WM_DWMCOMPOSITIONCHANGED:
                DisableDwmNonClientRendering(hWnd);
                break;

            // ── 非客户区绘制的抑制 ──────────────────────────────────────
            // 2026-10-02 追加。光靠 WM_NCCALCSIZE 返回 0，在 DWM 合成开着的 Win10/11 上
            // 已经足够；但**关了 DWM 合成的 Win7 经典主题**下，系统会在缩放过程中用主题引擎
            // 补画那圈边框 —— 现象就是"拖动边缘改大小时，原生边框又冒出来一条"。
            // 这几条把"画非客户区"的请求全部回绝，主题引擎就没有下手的地方了。
            case NativeMethodsEx.WM_NCPAINT:
            case NativeMethodsEx.WM_NCUAHDRAWCAPTION:
            case NativeMethodsEx.WM_NCUAHDRAWFRAME:
                handled = true;
                return IntPtr.Zero;

            case NativeMethodsEx.WM_NCACTIVATE:
                // 返回 TRUE = "非客户区我已经处理过了"，阻止 DefWindowProc 顺手重画边框。
                // 我们整条标题栏都是应用内画的，系统那圈本来就没内容可画。
                // ⚠️ 2026-10-03（用户第 14 轮「还会出原生小按钮」）：顺手把 DWM 的非客户区
                //    渲染策略**再申一遍** —— 激活态切换是 DWM 重置/重读 NC 策略的时机，
                //    只在挂载时设一次的话，被重置后就再也没人管了（真机「有时候冒出来」的形状）。
                DisableDwmNonClientRendering(hWnd);
                handled = true;
                return new IntPtr(1);

            case NativeMethodsEx.WM_ERASEBKGND:
                return OnEraseBackground(hWnd, wParam, ref handled);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 剥掉 Avalonia 给窗口类注册的 <c>CS_HREDRAW | CS_VREDRAW</c>。
    ///
    /// <para>
    /// 实测（2026-10-02 读本进程窗口类的 <c>GCL_STYLE</c>）：Avalonia 的窗口类样式是
    /// <c>0x0023 = CS_VREDRAW | CS_HREDRAW | CS_OWNDC</c>，背景刷是 <c>NULL</c>。
    /// 这两个 CS_*REDRAW 的语义是"窗口宽/高只要变一个像素就**整窗失效**"——
    /// 缩放时每一步都要把整个窗口重画一遍，于是肉眼看到的就是边缘一条跟着抖的杂边。
    /// </para>
    ///
    /// <para>
    /// 剥掉之后，系统只会把**新暴露出来的那一条带**标为无效，
    /// 旧内容原样留在屏幕上（"resize 时保留旧帧"），再配合 <see cref="OnEraseBackground"/>
    /// 给新条带填上窗口底色，缩放过程中就不会再出现任何杂边或闪烁。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 改的是**整个窗口类**（<c>GCL_STYLE</c> 没有"单窗口"版本）。Avalonia 给每个
    /// TopLevel 都注册了独立的类（类名带随机 GUID），所以这里不会误伤别的窗口。
    /// </para>
    /// </summary>
    private static void StripFullWindowErase(IntPtr hWnd)
    {
        try
        {
            var style = NativeMethodsEx.GetClassStyle(hWnd);
            var stripped = style & ~(uint)(NativeMethodsEx.CS_HREDRAW | NativeMethodsEx.CS_VREDRAW);
            if (stripped == style) return;

            NativeMethodsEx.SetClassStyle(hWnd, stripped);
            PortLog.Step($"窗口类样式: 0x{style:X4} → 0x{stripped:X4}（已剥掉 CS_HREDRAW|CS_VREDRAW，"
                         + "缩放时不再整窗重画）");
        }
        catch
        {
            // 拿不到就维持原样：顶多缩放时边缘闪一下，不影响功能
        }
    }

    /// <summary>
    /// 让 DWM 别再画这个窗口的非客户区（标题栏 + 边框）。
    ///
    /// <para>
    /// ── 为什么必须有这一步（2026-10-02 真机反馈「关闭按钮出现两个」）──────────────
    /// 非客户区在 Windows 上有**两条**绘制通道：
    /// <list type="number">
    ///   <item>GDI / 主题引擎（uxtheme）—— 走 <c>WM_NCPAINT</c>、<c>WM_NCUAHDRAW*</c>，
    ///         这两条我们在 <see cref="OnWndProc"/> 里已经全部回绝了；</item>
    ///   <item><b>DWM 合成</b> —— 开 Aero 的 Win7 / Vista 上，玻璃边框与那三个标题栏按钮
    ///         是 DWM 按窗口样式自己合成上去的，**根本不经过 WM_NCPAINT**。</item>
    /// </list>
    /// 只堵第 1 条，就会出现"原生标题栏明明没了、右上角却还有个小 ×"——
    /// 它和应用内自绘的 × 挨在一起，看起来就是两个关闭按钮。
    /// <c>DWMNCRP_DISABLED</c> 把第 2 条也关掉。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 只动"渲染策略"，**不动任何窗口样式**：缩放边、Aero 吸附、双击最大化、
    ///    任务栏右键菜单全部照旧。比拆 <c>WS_CAPTION</c> 安全得多。
    /// ⚠️ Win10/11 上这个属性已经废弃（可能返回 E_INVALIDARG），失败无所谓，
    ///    那两个系统本来也没有这个毛病 —— 失败也记一条日志，方便真机上对账。
    /// </para>
    /// </summary>
    private static void DisableDwmNonClientRendering(IntPtr hWnd)
    {
        try
        {
            var policy = NativeMethodsEx.DWMNCRP_DISABLED;
            var hr = NativeMethods.DwmSetWindowAttribute(
                hWnd, NativeMethodsEx.DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));

            PortLog.Step(hr == 0
                ? "窗口框架: 已要求 DWM 不绘制非客户区（DWMNCRP_DISABLED）—— 原生标题栏按钮不会再冒出来"
                : $"窗口框架: DWM 非客户区策略设置返回 0x{hr:X8}（该系统不支持/已废弃，属正常）");
        }
        catch
        {
            // 拿不到 dwmapi 也不影响功能：GDI 那一路已经被回绝了
        }
    }

    /// <summary>
    /// 只擦「这次真正需要重画的那块」。整窗擦除是缩放的闪烁/杂边根源，这里坚决不做。
    /// </summary>
    private IntPtr OnEraseBackground(IntPtr hWnd, IntPtr hdc, ref bool handled)
    {
        try
        {
            var color = EraseColorProvider?.Invoke();
            if (color is null || color.Value.A == 0)
                return IntPtr.Zero;      // 没颜色 → 什么都不做，交给 Avalonia 正常重画

            // ⚠️ 2026-10-03（第 16 轮「黑边」）：两种情况下**按整个客户区**擦，而不是只擦脏矩形 ——
            //    ① 刚刚尺寸变过（RequestFullRepaint 置了待办）：新暴露的区域最可能带残影；
            //    ② GetUpdateRect 报不出脏矩形：按 Windows 的惯例，这时 WM_ERASEBKGND 的意思就是
            //       "整个客户区都要擦"。以前这里是直接吃掉消息不擦 —— 无合成系统上就留下了黑边。
            //    擦完 Avalonia 会照常把自己的 surface 画上来，不会出现"窗口变成一整块底色"。
            var full = _fullRepaintPending;
            _fullRepaintPending = false;

            var rect = default(NativeMethods.RECT);
            var hasRect = NativeMethodsEx.GetUpdateRect(hWnd, out rect, false);
            if (full || !hasRect || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                if (!NativeMethodsEx.GetClientRect(hWnd, out rect)) return IntPtr.Zero;
                if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                {
                    handled = true;
                    return new IntPtr(1);
                }
            }

            var brush = NativeMethodsEx.CreateSolidBrush(
                NativeMethodsEx.ToColorRef(color.Value.R, color.Value.G, color.Value.B));
            if (brush == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                _ = NativeMethodsEx.FillRect(hdc, ref rect, brush);
            }
            finally
            {
                _ = NativeMethods.DeleteObject(brush);
            }

            handled = true;
            return new IntPtr(1);
        }
        catch
        {
            // 擦背景失败不是什么大事：交回默认路径，让 Avalonia 自己重画
            return IntPtr.Zero;
        }
    }

    /// <summary>擦除底色的一次性日志开关（避免每次擦背景都刷日志）。</summary>
    private static bool _eraseColorLogged;

    /// <summary>
    /// 「整窗补画」待办：<see cref="RequestFullRepaint"/> 置上，下一次 <c>WM_ERASEBKGND</c>
    /// 就把整个客户区（而不是只把脏矩形）填成底色。
    /// </summary>
    private bool _fullRepaintPending;

    /// <summary>上一次整窗补画的时间（TickCount64，ms）—— 尺寸连续变化时节流。</summary>
    private long _lastFullRepaintMs;

    /// <summary>
    /// 进入系统「改大小 / 移动」模态循环时的窗口尺寸。
    ///
    /// ⚠️ 2026-10-04（第 17 轮「拖动位置有残影」）：用来区分**缩放**和**纯移动** ——
    ///    <c>WM_EXITSIZEMOVE</c> 这两种情况都会来，但只有缩放才需要整窗补画
    ///    （纯移动时补画 = 多一次"整窗变底色再重画"，就是残影来源）。
    /// </summary>
    private (int W, int H) _sizeAtEnter;

    /// <summary>当前窗口尺寸（物理像素）；取不到返回 (0,0)。</summary>
    private static (int W, int H) CurrentSize(IntPtr hWnd)
    {
        try
        {
            if (NativeMethods.GetWindowRect(hWnd, out var r)) return (r.Width, r.Height);
        }
        catch
        {
            // 拿不到就当没变化：顶多少补画一次，不影响功能
        }

        return (0, 0);
    }

    /// <summary>
    /// 请求一次「整窗补画」：标脏整个客户区，下一次 <c>WM_ERASEBKGND</c> 会连整窗一起填底色。
    ///
    /// <para>
    /// ⚠️ 2026-10-03（用户第 16 轮「常用工具窗口时不时出现黑边」）：
    /// 窗口类的 <c>CS_HREDRAW|CS_VREDRAW</c> 被 <see cref="StripFullWindowErase"/> 剥掉了
    /// （缩放时不再整窗重画、闪得少），代价是**尺寸变化后新暴露的那块没人负责**：
    /// 系统不标脏、不产生 WM_ERASEBKGND，Avalonia 也只重画它自己那块 surface ——
    /// 在没有 DWM 合成的 Win7 上，剩下的部分就一直是黑的（有合成时看不出来）。
    /// 所以在尺寸稳定下来之后主动标一次脏，配合 <see cref="OnEraseBackground"/> 的整窗擦。
    /// </para>
    ///
    /// <para>
    /// <c>bErase=false</c>：只标脏不擦背景，避免"拖完窗口先闪一下底色再重画"。
    /// </para>
    /// </summary>
    private void RequestFullRepaint(IntPtr hWnd, string why)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastFullRepaintMs < 300) return;      // 连发的尺寸变化只处理最后那次
            _lastFullRepaintMs = now;

            _fullRepaintPending = true;
            _ = NativeMethodsEx.InvalidateRect(hWnd, IntPtr.Zero, false);

            PortLog.Step($"整窗补画: {why} → 已标脏整个客户区（防止无合成系统上留黑边）");
        }
        catch
        {
            // 标脏失败不影响可用性：最坏就是维持现状（有 DWM 的系统本来也没这个毛病）
        }
    }

    /// <summary>上一次 WM_NCCALCSIZE 时是否是最大化状态（-1 = 还没记录过）。只用来抑制重复日志。</summary>
    private int _lastNcMaximized = -1;

    /// <summary>
    /// 最大化时把客户区矩形改成显示器工作区。
    ///
    /// <para>
    /// 为什么必须做：窗口最大化时系统给的矩形是「工作区 + 一圈不可见边框」
    /// （Win7 上是为了那圈看得见的边框、Win10/11 上是为了 Aero Snap 的拖拽热区）。
    /// 我们返回 0 之后客户区就等于那个矩形，于是内容比工作区大一圈 ——
    /// 任务栏被盖住、右/下边缘的内容被切掉。
    /// </para>
    /// </summary>
    private void ConfineMaximizedToWorkArea(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            var zoomed = NativeMethods.IsZoomed(hWnd);

            // 状态切换时记一条 —— 真机上"最大化后内容盖住任务栏"就靠它定位：
            // 日志里 zoomed 若是 False，说明这个判据在该系统上不可靠，得换判据。
            if (_lastNcMaximized != (zoomed ? 1 : 0))
            {
                _lastNcMaximized = zoomed ? 1 : 0;
                PortLog.Step("NCCALCSIZE: 最大化状态=" + (zoomed ? "是（客户区修正为显示器工作区）" : "否"));
            }

            if (!zoomed) return;

            var mon = NativeMethodsEx.MonitorFromWindow(hWnd, NativeMethodsEx.MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return;

            var mi = new NativeMethodsEx.MONITORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethodsEx.MONITORINFO>()
            };
            if (!NativeMethodsEx.GetMonitorInfo(mon, ref mi)) return;

            // rgrc[0]（本次要算的客户区矩形）就在 lParam 的头 16 字节：left/top/right/bottom
            Marshal.WriteInt32(lParam, 0, mi.rcWork.Left);
            Marshal.WriteInt32(lParam, 4, mi.rcWork.Top);
            Marshal.WriteInt32(lParam, 8, mi.rcWork.Right);
            Marshal.WriteInt32(lParam, 12, mi.rcWork.Bottom);
        }
        catch
        {
            // 拿不到显示器信息就维持原样：顶多多溢出十几像素，不影响可用性
        }
    }

    private IntPtr OnHitTest(IntPtr hWnd, IntPtr lParam, ref bool handled)
    {
        // 先听系统的。系统对非客户区有自己的一套判断（系统菜单、滚动条…），照它说的办最稳。
        var sys = NativeMethodsShell.DefWindowProc(hWnd, NativeMethodsEx.WM_NCHITTEST, IntPtr.Zero, lParam);
        if (sys != new IntPtr(NativeMethodsEx.HTCLIENT))
        {
            // ⚠️ 2026-10-03（用户第 12 轮「有时还会冒出 Win7 原生小按钮」）：
            //    系统对**原生标题栏按钮**区域的命中（最小化/最大化/关闭/帮助/系统菜单）不能照传 ——
            //    非客户区像素虽然是 0（WM_NCCALCSIZE 吃掉了），但 DefWindowProc 的命中判定
            //    还按 WS_CAPTION 的老格子算：鼠标悬到右上角"按钮该在的位置"，它照样报
            //    HTMAXBUTTON / HTCLOSE，DWM/tooltip 引擎就在那儿画出原生按钮并弹「最大化」提示。
            //    这些命中一律拦下，交给下面的自绘逻辑：标题栏范围 → HTCAPTION，按钮位置 → HTCLIENT。
            switch (sys.ToInt32())
            {
                case 8:    // HTMINBUTTON
                case 9:    // HTMAXBUTTON
                case 13:   // HTSYSMENU
                case 20:   // HTCLOSE
                case 21:   // HTHELP
                    // ⚠️ 2026-10-03 节流日志：真机「金刚点了没反应 / 原生小按钮复发」取证 ——
                    //    看看系统到底在哪些坐标报按钮命中。命中测试跟着鼠标走，限 500ms 一条。
                    LogInterceptedButtonHit(hWnd, sys.ToInt32(), lParam);
                    break;

                default:
                    handled = true;
                    return sys;
            }
        }

        if (!NativeMethods.GetWindowRect(hWnd, out var wr))
        {
            // 矩形都拿不到就别瞎猜，交回默认路径
            return IntPtr.Zero;
        }

        // WM_NCHITTEST 的 lParam 是**屏幕坐标**，高低 16 位各一个有符号短整型
        var sx = unchecked((short)(long)lParam);
        var sy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));

        var x = sx - wr.Left;      // 转成窗口内坐标（物理像素）
        var y = sy - wr.Top;

        var scale = _window.RenderScaling;
        if (scale <= 0) scale = 1;

        var w = wr.Width;
        var h = wr.Height;

        // ── ⓪ 交互控件（三大金刚 / 返回 / 主题切换）优先于一切 ──
        // ⚠️ 2026-10-03（用户第 16 轮「最小化/最大化/关闭有时候点了没反应」）：
        //    这一条以前排在**缩放带之后**，而三大金刚是贴着窗口右缘的 ——
        //    ResizeBorder=6dip 的缩放带正好盖住关闭键最右边那一条，
        //    用户按 Windows 的习惯点右上角**最角落**，落点就在缩放带上 → 变成"拖窗口大小"，
        //    表现出来就是"点了没反应"（而且光标会变成缩放箭头）。
        //    真 Windows 的标题栏按钮也是这个优先级：角落属于关闭键，不属于缩放边。
        //
        // ⚠️⚠️ 2026-10-04（用户第 17 轮「关闭按钮出现原生的问题还没解决」）——**真根因在这里**：
        //    原来这一行写的是 `return IntPtr.Zero;`，而**没有把 handled 置 true**。
        //    Avalonia 的钩子在 handled=false 时不会截断这次消息，它自己又不处理 WM_NCHITTEST，
        //    于是下一步落到 DefWindowProc —— 那个函数**看不到我们"客户区为 0"的声明**，
        //    仍按 WS_CAPTION 的老格子算：鼠标悬在右上角就回 HTCLOSE / HTMAXBUTTON。
        //    系统拿到这个命中码，就认为"这里有一个原生按钮"，于是 DWM/主题引擎在那儿
        //    画出一个小 ×（就是用户看到的"两个关闭按钮叠在一起"），并且弹"关闭"提示。
        //    （注释过去写"让 DefWindowProc 报它默认的 HTCLIENT"——那个假设本身是错的。）
        //    现在显式返回 HTCLIENT 并置 handled=true：系统认定那里是普通客户区，
        //    既不画原生按钮，Avalonia 也照常收到点击。
        if (IsInteractive(x, y, scale)) return Hit(NativeMethodsEx.HTCLIENT, ref handled);

        // ── ① 四周边缘 → 交给系统的缩放循环（最大化时系统本来也不让拖，就别报）──
        if (ResizeEnabled && !IsMaximized(hWnd))
        {
            var b = (int)Math.Round(ResizeBorder * scale);
            if (b < 1) b = 1;

            var onLeft = x < b;
            var onRight = x >= w - b;
            var onTop = y < b;
            var onBottom = y >= h - b;

            if (onTop && onLeft) return Hit(NativeMethodsEx.HTTOPLEFT, ref handled);
            if (onTop && onRight) return Hit(NativeMethodsEx.HTTOPRIGHT, ref handled);
            if (onBottom && onLeft) return Hit(NativeMethodsEx.HTBOTTOMLEFT, ref handled);
            if (onBottom && onRight) return Hit(NativeMethodsEx.HTBOTTOMRIGHT, ref handled);
            if (onLeft) return Hit(NativeMethodsEx.HTLEFT, ref handled);
            if (onRight) return Hit(NativeMethodsEx.HTRIGHT, ref handled);
            if (onTop) return Hit(NativeMethodsEx.HTTOP, ref handled);
            if (onBottom) return Hit(NativeMethodsEx.HTBOTTOM, ref handled);
        }

        // ── ② 标题栏 → 交给系统的拖动循环 ──
        // 这一条是"自绘标题栏还保留原生手感"的全部秘密：返回 HTCAPTION 之后，
        // 拖到屏幕边缘吸附半屏、拖到顶部最大化、双击最大化、右键弹系统菜单，
        // 全部由系统按它自己的规则处理，不需要我们写一行代码。
        var titlePx = (int)Math.Round(TitleBarHeight * scale);
        if (y < titlePx && !IsInteractive(x, y, scale))
        {
            return Hit(NativeMethods.HTCAPTION, ref handled);
        }

        // 其余都算客户区：不接管，让 Avalonia 自己走默认路径（它本来也不处理这个消息）
        return IntPtr.Zero;
    }

    private static IntPtr Hit(int value, ref bool handled)
    {
        handled = true;
        return new IntPtr(value);
    }

    /// <summary>上一次「系统报原生按钮命中」日志的时间（TickCount64，ms）。500ms 节流防刷屏。</summary>
    private static long _lastButtonHitLogMs;

    /// <summary>真机取证：系统在什么坐标把自绘按钮区当成原生标题栏按钮（第 14 轮「金刚没反应/原生小按钮」）。</summary>
    private static void LogInterceptedButtonHit(IntPtr hWnd, int code, IntPtr lParam)
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastButtonHitLogMs < 500) return;
            _lastButtonHitLogMs = now;

            var sx = unchecked((short)(long)lParam);
            var sy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            PortLog.Step($"NCHITTEST: 系统报原生按钮命中 code={code} 屏幕=({sx},{sy}) —— 已拦下交给自绘逻辑");
        }
        catch
        {
            // 日志失败不影响命中
        }
    }

    private bool IsMaximized(IntPtr hWnd)
    {
        try
        {
            return _window.WindowState == WindowState.Maximized || NativeMethods.IsZoomed(hWnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>鼠标是不是落在标题栏的交互控件上（按钮等）。命中就让它走 HTCLIENT，Avalonia 才收得到点击。</summary>
    private bool IsInteractive(int x, int y, double scale)
    {
        if (InteractiveRegions is null) return false;

        try
        {
            foreach (var r in InteractiveRegions())
            {
                if (r.Width <= 0 || r.Height <= 0) continue;

                var left = r.X * scale;
                var top = r.Y * scale;
                var right = left + r.Width * scale;
                var bottom = top + r.Height * scale;

                // 往外放宽 4px：让按钮贴边的那几个像素也好点，不至于被当成拖动/缩放
                if (x >= left - 4 && x <= right + 4 && y >= top - 4 && y <= bottom + 4)
                    return true;
            }
        }
        catch
        {
            // 控件树正在重建时可能取不到坐标 —— 当"不是交互区"处理即可
        }

        return false;
    }
}
