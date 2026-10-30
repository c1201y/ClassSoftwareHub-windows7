using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Styling;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 在 <see cref="Backdrop"/> 之上补三件 Win10 专属的事（<see cref="Backdrop"/> 本身按契约不改）：
///
/// <list type="number">
///   <item><b>亚克力着色跟随深浅色</b> —— <see cref="Backdrop"/> 里写死的
///         <c>GradientColor = 0x99FFFFFF</c>（60% 白）在 Win10 深色模式下会把整个窗口刷成
///         奶白的雾；Win11 的 Mica 自带主题色，所以只有 Win10 上看得出来。</item>
///   <item><b>Win10 浮窗圆角</b> —— Win10 默认走 GPU 渲染，Avalonia 给窗口加
///         <c>WS_EX_NOREDIRECTIONBITMAP</c>，DWM 会无视 <c>SetWindowRgn</c>；
///         偏偏 <c>OsInfo.SupportsWindowRounding</c> 只在 Win11 为真，于是 Win10 浮窗
///         "有时候"是直角（软件渲染退路才有重定向位图、区域裁剪才生效）。
///         这里对 <b>Win8+ 且 DWM 开着</b> 的浮窗统一改走「内容自身 alpha 抠角」。</item>
///   <item><b>深色标题栏</b> —— Win10 1809+ 的 <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>
///         之前只在自绘浮窗里顺手设过，主窗口 / 客户区扩展的窗口从没设，
///         深色下窗口外圈那条边仍是浅色的。</item>
/// </list>
///
/// <para>
/// 所有入口都会在窗口上挂一次 <c>ActualThemeVariantChanged</c> 监听：主题变了就按新深浅色
/// 重算材质 / 着色 / 标题栏。这样 <c>ThemeCompat.Notify()</c> 之后不会留下"半旧半新"的外观。
/// </para>
///
/// <para>
/// ⚠️ <c>alphaCorners</c> 只给**内容自带圆角面板**的浮窗传 true（音量 / 合成器 / 侧边栏 / 留言）。
///    主窗口、工具面板这类有系统框架或方形根底的窗口不能传 —— 底色一透就会露出桌面。
/// </para>
/// </summary>
public static class BackdropTheme
{
    /// <summary>
    /// 可以用「内容 alpha」做圆角的系统：Win8 起才有逐像素透明；Win7 没有透明窗口能力，
    /// 只能继续靠 <see cref="RoundedCorners"/> 的区域裁剪。
    /// </summary>
    public static bool SupportsAlphaCorners
        => !OsInfo.IsWindows7 && OsInfo.IsDwmCompositionEnabled;

    private sealed class State
    {
        public Panel? Root;
        public string? Prefer;
        public bool AlphaCorners;
        public bool Logged;
        public EventHandler? Handler;
        public bool ReassertQueued;
    }

    /// <summary>
    /// 每个窗口的调用参数。用弱表：窗口关了不需要手动反注册，也不会把窗口钉在内存里。
    /// </summary>
    private static readonly ConditionalWeakTable<Window, State> States = new();

    /// <summary>
    /// 上材质 + 主题化着色 + 深色标题栏，并登记主题变化后的自动重算。
    /// 返回值与 <see cref="Backdrop.Apply"/> 一致（<c>mica</c> / <c>acrylic</c> / <c>blur</c> / <c>solid</c>）。
    /// </summary>
    /// <param name="win">目标窗口。</param>
    /// <param name="root">根面板（传给 <see cref="Backdrop.Apply"/>，可空）。</param>
    /// <param name="prefer">"mica" / "acrylic" / "solid"；不传则沿用上一次或读设置。</param>
    /// <param name="alphaCorners">窗口内容是否有自带圆角的面板（浮窗传 true）。</param>
    public static string Apply(Window win, Panel? root = null, string? prefer = null, bool alphaCorners = false)
    {
        var state = States.GetValue(win, _ => new State());
        state.Root = root ?? state.Root;
        state.Prefer = prefer ?? state.Prefer;
        state.AlphaCorners = alphaCorners;

        if (state.Handler is null)
        {
            state.Handler = (_, _) =>
            {
                try { ApplyCore(win, state); }
                catch (Exception ex) { AppLog.Warning("backdrop", "主题切换重算材质失败: " + ex.Message); }
            };
            try { win.ActualThemeVariantChanged += state.Handler; } catch { }

            // ⚠️ 2026-10-25（用户实机复现：Win11 改缩放/分辨率后悬浮窗丢圆角，Win7 不受影响）：
            //    WM_DPICHANGED 会让系统重置窗口的 accent / 逐像素 alpha 状态，
            //    Avalonia 也可能把 ActualTransparencyLevel 退回 None —— 「内容 alpha 抠角」
            //    随之失效变方角，而调用方只在 Present() 显示时 Apply 一次，没人补。
            //    Win7 走 SetWindowRgn 区域裁剪，不受系统重置影响，所以只有 Win10/11 出问题。
            //    这里在 ScalingChanged（覆盖改缩放、跨屏拖动）上做抖动合并的重钉：
            //    合并到一帧只重算一次，与 Present() 同一套幂等路径。
            try { win.ScalingChanged += (_, _) => ScheduleReassert(win); } catch { }
        }

        return ApplyCore(win, state);
    }

    /// <summary>
    /// 缩放/显示变化后的重钉调度。连续触发合并为一次（Loaded 优先级执行，
    /// 此时 WM_DPICHANGED 的新尺寸已落定，量得到终态）。
    /// </summary>
    private static void ScheduleReassert(Window win)
    {
        var state = States.GetOrCreateValue(win);
        if (state.ReassertQueued) return;
        state.ReassertQueued = true;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            state.ReassertQueued = false;
            try
            {
                var before = win.ActualTransparencyLevel;
                ApplyCore(win, state);

                // 提示值没变时 Avalonia 不会重新评估透明状态 —— 若系统重置后级别掉下去
                // （Transparent → None），先把提示降到 None 再升回，强制 Win32 端重建
                // 逐像素 alpha 管线；两次都传新数组，styled property 变更一定触发。
                if (state.AlphaCorners && SupportsAlphaCorners &&
                    win.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
                {
                    win.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
                    win.TransparencyLevelHint = new[]
                    {
                        WindowTransparencyLevel.Transparent,
                        WindowTransparencyLevel.None
                    };
                }

                AppLog.Info("backdrop",
                    $"缩放变化重钉材质/圆角: 透明级别 {before} → {win.ActualTransparencyLevel} " +
                    $"(alpha={state.AlphaCorners && SupportsAlphaCorners})");
            }
            catch (Exception ex)
            {
                AppLog.Warning("backdrop", "缩放变化重钉材质失败: " + ex.Message);
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private static string ApplyCore(Window win, State state)
    {
        var dark = win.ActualThemeVariant == ThemeVariant.Dark;

        // Win10 1809+ 的深色标题栏。Win7/8/10 早期版本不认这个属性，调用是安全的空操作。
        TrySetDarkFrame(win, dark);

        var alphaPath = state.AlphaCorners && SupportsAlphaCorners;

        string kind;
        if (alphaPath)
        {
            // ⚠️ 2026-10-25（用户 Win11 26200 实锤：缩放/分辨率一变，悬浮窗就丢圆角）：
            //    alpha 抠角窗口**绝不能让 DWM 上任何系统材质**。此前走 Backdrop.Apply("acrylic")：
            //    亚克力成功 → 紧接着 EnablePerPixelCorners 关 accent，净效果无材质、一切正常；
            //    可亚克力一旦失败（Win11 新版本上 SetWindowCompositionAttribute 很不稳，DWM
            //    抖动期尤其容易挂），Backdrop.Apply 会**自作主张退到 Mica**，把
            //    DWMWA_SYSTEMBACKDROP_TYPE=MAINWINDOW 钉在窗口上 —— DWM 从此用贴壁纸取色的
            //    暗色材质铺满**整个窗口矩形**（内容 alpha 抠出的四角月牙全被糊死 → 视觉方角），
            //    而且没有任何代码再清它。窗口透明级别、DWM 圆角偏好全都正常，纯纯被背板盖住。
            //    这里改成：透明提示 + 关 accent + **显式把系统背板清成 NONE**（自愈已钉上的 Mica），
            //    不再经过 Backdrop.Apply 的材质级联。
            kind = "transparent";
            try
            {
                win.TransparencyLevelHint = new[]
                {
                    WindowTransparencyLevel.Transparent,
                    WindowTransparencyLevel.None
                };
            }
            catch { }

            Backdrop.EnablePerPixelCorners(win);
            ClearSystemBackdrop(win);
        }
        else
        {
            kind = Backdrop.Apply(win, state.Root, state.Prefer);
        }

        if (!state.Logged)
        {
            state.Logged = true;
            PortLog.Step($"材质: {kind} 深色={dark} 圆角={(alphaPath ? "内容alpha" : "窗口区域")}");
        }

        return kind;
    }

    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;

    /// <summary>把 DWM 系统背板（Mica/Acrylic 系）显式关掉。没设过的窗口设了也无副作用。</summary>
    private static void ClearSystemBackdrop(Window win)
    {
        try
        {
            var hwnd = Backdrop.TryGetHwnd(win);
            if (hwnd == IntPtr.Zero) return;
            var none = DwmsbtNone;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
        }
        catch { }
    }

    private static void TrySetDarkFrame(Window win, bool dark)
    {
        try
        {
            var hwnd = Backdrop.TryGetHwnd(win);
            if (hwnd != IntPtr.Zero) WindowChrome.SetDarkMode(hwnd, dark);
        }
        catch { }
    }
}
