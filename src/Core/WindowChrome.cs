using System;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 窗口外观收尾：把 Win11 默认给窗口画的那圈细边框去掉（那圈白线在无边框窗口上特别显眼）。
///
/// 两个 DWM 属性：
///   DWMWA_BORDER_COLOR            —— 边框颜色，设成"无色"就不画了（Win11 才认，Win10 忽略）
///   DWMWA_WINDOW_CORNER_PREFERENCE —— 圆角：浮窗要圆角(ROUND)，全屏时钟必须"不圆角"(DONOTROUND)，
///                                     否则 Win11 会把四个角削掉、露出底下的桌面，看着像屏幕外面套了一圈
///
/// ⚠️ Win10 (<22000) 不认识这两个属性，调用会直接返回失败码 —— 忽略即可，不要当异常处理。
///
/// ⚠️ 移植说明：所有 P/Invoke 已按移植契约收进 <c>Platform.NativeMethods</c> /
///    <c>Platform.NativeMethodsEx</c>（原版是散在本文件里的 DllImport），逻辑一字未改。
/// </summary>
public static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;      // Win10 1809+；老系统是 19
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;

    private const int NcRenderingDisabled = 2;             // 关掉 DWM 给窗口画的非客户区（那条灯线的元凶）

    private const int CornerDefault = 0;
    private const int CornerDoNotRound = 1;
    private const int CornerRound = 2;
    private const int CornerRoundSmall = 3;

    private const uint ColorDefault = 0xFFFFFFFF;
    private const uint ColorNone = 0xFFFFFFFE;

    // ── 彻底无边框（这才是干掉"白边"的正解） ──────────────────────
    // 光设 DwmSetWindowAttribute 不够：窗口还留着 WS_CAPTION/WS_SYSMENU 那套非客户区，
    // 系统会在最外画 1px 边框 + 2px 浅色框架（实测左边 773=边框、774~775=浅色、776 才是内容）。
    // 想真正贴边，得把窗口做成 popup 并让系统重算框架。

    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsBorder = 0x00800000;
    private const int WsDlgFrame = 0x00400000;
    private const int WsPopup = unchecked((int)0x80000000);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// 把窗口做成真正的无边框 popup：去掉标题栏/粗边框/系统菜单，加上 WS_POPUP，
    /// 再让系统重算一次框架。做完窗口就"贴边"了，系统不再在外面画那圈线。
    /// </summary>
    public static void MakeBorderless(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var remove = WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox
                         | WsBorder | WsDlgFrame;

            if (IntPtr.Size == 8)
            {
                var style = NativeMethodsEx.GetWindowLongPtrW(hwnd, NativeMethodsEx.GWL_STYLE).ToInt64();
                style = (style & ~(long)remove) | unchecked((uint)WsPopup);
                _ = NativeMethodsEx.SetWindowLongPtrW(hwnd, NativeMethodsEx.GWL_STYLE, new IntPtr(style));
            }
            else
            {
                var style = NativeMethodsEx.GetWindowLongW(hwnd, NativeMethodsEx.GWL_STYLE);
                style = (style & ~remove) | WsPopup;
                _ = NativeMethodsEx.SetWindowLongW(hwnd, NativeMethodsEx.GWL_STYLE, style);
            }

            _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch
        {
            // 改不动就算了，顶多还留一圈线，不影响用
        }
    }

    /// <summary>
    /// 去掉窗口那圈边框；<paramref name="rounded"/> 决定四角是圆角还是直角，
    /// <paramref name="dark"/> 告诉系统"这个窗口是深色的" —— 不告诉它的话，
    /// 深色界面的无边框窗口外边会被画一圈**浅色/白色**的框（就是用 SystemBackdrop 那套材质时最明显）。
    /// </summary>
    public static void RemoveBorder(IntPtr hwnd, bool rounded = false, bool dark = false)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            MakeBorderless(hwnd);        // 先把系统框架整个去掉，这是"白边"的根
            SetDarkMode(hwnd, dark);

            // ⚠️ **白边（那圈细白线）的真正元凶**：DWM 给窗口画的非客户区（1px 边框 + 2px 亮光）。
            //    实测：DWMWA_BORDER_COLOR 设成"无色"它不认（回值一直是 0）；改成红色倒是会变红 —— 说明那 1px 是边框、
            //    紧挨着的 2px 是 DWM 的框架亮光。只有把非客户区渲染整个关掉，这条线才真的没了。
            //    代价：窗口没有系统阴影、圆角和那 3px 会变透明（所以内容看起来会往里缩 3px，但不会再看到白线）。
            var disabled = NcRenderingDisabled;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref disabled, sizeof(int)) != 0)
            {
                // Win10 有些版本不认这个属性，那就退回"把边框设成无色"
                var none = unchecked((int)ColorNone);
                _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
            }

            var corner = rounded ? CornerRound : CornerDoNotRound;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            // ⚠️ 上面那个属性 **Win7/8/10 根本不认**（静默返回失败），圆角得靠窗口区域裁剪。
            //    这一步不能省 —— 教室里跑的就是这条兜底路径，用户看到的
            //    "悬浮窗展开没有圆角、贴边没有圆角"就是漏了它。
            //    具体实现和三个 GDI 坑都在 <see cref="RoundedCorners"/> 里；Win11 上它会自动让路。
            if (rounded) RoundedCorners.Apply(hwnd, RoundedCorners.DefaultRadiusDip);
            else RoundedCorners.Clear(hwnd);
        }
        catch
        {
            // Win10 或 DWM 不给面子：保持系统默认就行，别影响功能
        }
    }

    /// <summary>
    /// **只调"四角圆不圆"，其余一律不动** —— 主窗口专用。
    ///
    /// ⚠️ 为什么不能拿 <see cref="RemoveBorder"/> 顶上：那一套是给**自绘浮窗**用的，
    ///    它会把 WS_CAPTION / WS_THICKFRAME 整个拆掉换成 WS_POPUP，还把 DWMWA_NCRENDERING_POLICY 关掉。
    ///    主窗口的缩放边、吸附、Win + 方向键、系统阴影全靠那套非客户区，拆了就不是个正常窗口了。
    ///
    /// ⚠️ 为什么必须显式设：WinUI 3 只要 <c>ExtendsContentIntoTitleBar = true</c>，
    ///    窗口的圆角偏好就停在 Default(0)。2026-09-29 实测（同一台 Win11）：
    ///    主窗口偏好 0 → 四角是**纯直角**；改成 2(ROUND) → 立刻变圆。
    ///    Win11 原生圆角只有 8px，肉眼第一眼不一定注意得到，但没有它就是"不像原生应用"。
    /// </summary>
    public static void SetRounded(IntPtr hwnd, bool rounded)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var corner = rounded ? CornerRound : CornerDoNotRound;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch { }
    }

    /// <summary>告诉 DWM 这个窗口是深色的（决定系统给它画浅色还是深色的框/材质）。</summary>
    public static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var value = dark ? 1 : 0;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                // 老系统用 19
                _ = NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
            }
        }
        catch { }
    }

    /// <summary>恢复系统默认边框（要用的时候再说，先留着）。</summary>
    public static void RestoreBorder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var corner = CornerDefault;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var color = unchecked((int)ColorDefault);
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref color, sizeof(int));
        }
        catch { }
    }

    /// <summary>小圆角（浮窗想更收敛一点时用）。</summary>
    public static void SetRoundSmall(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var corner = CornerRoundSmall;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch { }
    }

}
