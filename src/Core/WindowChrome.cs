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

    // ── WM_NCCALCSIZE：干掉"幻影框架"的最后一刀（2026-10-01）──────────────
    // 实测（2026-10-01，屏幕实拍 + FrameBounds 对账）：即便 WS_POPUP 化成功，
    // DWM 仍给窗口保留 ~2px（左/右/下）非客户区 —— GetWindowRect 比 DWM 实际可视范围
    // 大出一圈，内容被迫往里缩，那一圈露出没压薄纱的裸亚克力/系统框，
    // 在壁纸上就是一圈"没填充完"的灰环。关 NC 渲染、设边框色都治不了它。
    // 标准解法：子类化窗口，WM_NCCALCSIZE(wParam=TRUE) 直接返回 0 —— 客户区=整个窗口，
    // 非客户区彻底归零，内容铺满到边。无边框 Win32 应用的通用做法。
    //
    // ⚠️ 移植说明：主窗口/工具浮窗那几支走的是 <see cref="SelfDrawnFrame"/>（Avalonia 的
    //    WndProc 钩子版，能力更全，见其注释）；这里补的是**自绘浮窗**这条 WS_POPUP 路线
    //    —— 它们由 <see cref="RemoveBorder"/> 收尾，没有 SelfDrawnFrame，此前缺这一刀。

    private const uint WmNcCalcSize = 0x0083;

    private const uint NcCalcSizeSubclassId = 0x43534842;   // "CSHB"

    /// <summary>给窗口装上 WM_NCCALCSIZE 处理（幂等：同 ID 重复装会替换，不会叠加）。
    /// 必须在**拥有该窗口的线程**上调用（RemoveBorder 的调用方都在 UI 线程）。</summary>
    private static void InstallNcCalcSizeHook(IntPtr hwnd)
    {
        var ok = NativeMethodsEx.SetWindowSubclass(hwnd, NcCalcSizeProc, NcCalcSizeSubclassId, IntPtr.Zero);
        ChromeLog($"subclass hwnd={hwnd} ok={ok}");
    }

    // ⚠️ 必须留字段：委托一旦被 GC，原生回调就指向野指针，下一步直接崩。
    private static readonly NativeMethodsEx.SubclassProc NcCalcSizeProc = OnNcCalcSize;

    private static bool _ncProcLogged;

    private static IntPtr OnNcCalcSize(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            if (!_ncProcLogged)
            {
                _ncProcLogged = true;
                ChromeLog($"NCCALCSIZE fired hwnd={hWnd} → 返回0（客户区=整个窗口）");
            }
            return IntPtr.Zero;                            // 客户区 = 整个窗口，别给我留框
        }

        return NativeMethodsEx.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static long ReadStyle(IntPtr hwnd) =>
        IntPtr.Size == 8
            ? NativeMethodsEx.GetWindowLongPtrW(hwnd, NativeMethodsEx.GWL_STYLE).ToInt64()
            : NativeMethodsEx.GetWindowLongW(hwnd, NativeMethodsEx.GWL_STYLE);

    /// <summary>诊断日志（排查"幻影框架"用，只在出错/首次时写）。</summary>
    private static void ChromeLog(string message)
    {
        try
        {
            AppLog.Info("chrome", message);
        }
        catch { }
    }

    /// <summary>
    /// 把窗口做成真正的无边框 popup：去掉标题栏/粗边框/系统菜单，加上 WS_POPUP，
    /// 再让系统重算一次框架。做完窗口就"贴边"了，系统不再在外面画那圈线。
    /// </summary>
    /// <returns>样式改成功与否。失败（极少）时调用方走"关 NC 渲染"的老路兜底。</returns>
    public static bool MakeBorderless(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

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
            return true;
        }
        catch
        {
            // 改不动就算了，顶多还留一圈线，不影响用
            return false;
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

        ChromeLog($"RemoveBorder begin hwnd={hwnd}");
        try
        {
            var borderless = MakeBorderless(hwnd);   // 先把系统框架整个去掉，这是"白边"的根
            ChromeLog($"  MakeBorderless={borderless} styleAfter=0x{ReadStyle(hwnd):X}");

            // 客户区=整个窗口，杀掉幻影框架（见 WM_NCCALCSIZE 段注释）。单独兜异常：
            // 它要是炸了不能连累后面的边框色/圆角（更不能悄悄吞掉整段流程）。
            try { InstallNcCalcSizeHook(hwnd); }
            catch (Exception ex) { ChromeLog("  subclass FAILED: " + ex.GetType().Name + " " + ex.Message); }

            SetDarkMode(hwnd, dark);

            if (borderless)
            {
                // ⚠️ **WS_POPUP 成功就别再关 NC 渲染了**（2026-10-01 修"没填充完的一圈边"）：
                //    关掉后窗口还留着 ~2px 的"幻影框架"——窗口矩形比 DWM 实际可视范围大出一圈，
                //    内容被迫往里缩，那一圈露出的是没压薄纱的裸亚克力，在浅色壁纸上就是一圈灰环。
                //    WS_POPUP + NCCALCSIZE 归零后已经没有非客户区可画，不再需要这个老兜底。
                //    边框色设成"无色"防止个别系统还在外圈画 1px 线（Win11 生效，老系统自动忽略）。
                var none = unchecked((int)ColorNone);
                _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
            }
            else
            {
                // 老路兜底：样式改不动（或 Win10 个别版本），只好关掉 DWM 的非客户区渲染。
                // 代价是内容往里缩 ~3px，但至少没有白线。
                var disabled = NcRenderingDisabled;
                if (NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref disabled, sizeof(int)) != 0)
                {
                    var none2 = unchecked((int)ColorNone);
                    _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none2, sizeof(int));
                }
            }

            var corner = rounded ? CornerRound : CornerDoNotRound;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            // ⚠️ 上面那个属性 **Win7/8/10 根本不认**（静默返回失败），圆角得靠窗口区域裁剪。
            //    这一步不能省 —— 教室里跑的就是这条兜底路径，用户看到的
            //    "悬浮窗展开没有圆角、贴边没有圆角"就是漏了它。
            //    具体实现和三个 GDI 坑都在 <see cref="RoundedCorners"/> 里；Win11 上它会自动让路。
            if (rounded) RoundedCorners.Apply(hwnd, RoundedCorners.DefaultRadiusDip);
            else RoundedCorners.Clear(hwnd);

            // ⚠️ 移植说明：上游此处还调 SetBackgroundFallback(hwnd, dark)（把窗口类背景刷换成主题底色）。
            //    本仓库**故意不移植**：那个修复针对的是「WinUI 3 注册窗口类时用的是黑刷」这一根因
            //    （点开侧边栏瞬间闪纯黑框）；而 Avalonia 注册的窗口类背景刷是 NULL（见 SelfDrawnFrame 实测注释），
            //    根本不会闪黑。反过来把类背景刷设成不透明纯色，还会盖掉浮窗的亚克力/透明底 —— 是倒退。
            //    浮窗首帧的兜底色由 Views/FlyoutChrome.PrimeOpaqueBackdrop 负责，主窗口由 SelfDrawnFrame 的擦除负责。

            ChromeLog($"  done style=0x{ReadStyle(hwnd):X} corner={(rounded ? "round" : "square")}");
        }
        catch (Exception ex)
        {
            ChromeLog("EXCEPTION: " + ex.GetType().Name + " " + ex.Message);
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

    // ── Win10 的窗口圆角（2026-10-11；2026-10-18 自上游 1.2.0 移植）──────────────────────
    //
    // 起因（Nick 在 Win10 上截图）：侧边栏收起那条小抓手**四角是方的**。
    //   根因：这条的圆角在 Win11 上是 **DWM 把整个窗口裁圆**（DWMWA_WINDOW_CORNER_PREFERENCE，
    //   见 SetRounded），而那个属性 **Win10 根本不认**（返回失败码，静默无效）。
    //   XAML 里 Panel 的 CornerRadius=8 只圆了内容那一层，圆角外面露的仍是窗口自身的背景 ——
    //   窗口是方的，就顶到方角，于是整条看着就是个方角块。
    //
    // Win10 上唯一能改窗口形状的就是 GDI 的**窗口区域**（SetWindowRgn）。
    //
    // ⚠️ 四条，改前先看：
    //   1) 默认**只在 Win10 用**。Win11 上 DWM 的圆角带抗锯齿、还跟着系统设置走，比 region 好 ——
    //      ⛔ 别拿 region 去顶掉它（两边都设会叠一层锯齿边）。
    //      **例外（2026-10-12）**：侧边栏**收起态**那条（12dip 宽）在 Win11 上必须改用 region ——
    //      因为 DWM 给"圆角窗口"画的那圈投影在这个尺寸下会右偏约 6px，看着像条右边多了一圈边框。
    //      那时必须**先**把 DWM 圆角偏好设成 DONOTROUND（见 SetRounded(hwnd,false)）**再**设区域，
    //      两边都设才不叠锯齿；展开态记得 SetRounded(hwnd,true) + ClearRoundRegion 还原。
    //      详见 ToolSidebarWindow.ApplyRoundRegion 的注释。
    //   2) 区域是**窗口相对坐标**：窗口挪位置不用重设，**尺寸一变必须重设**
    //      （收起↔展开、横竖切换、紧凑档、DPI 变化）—— 所以调用方要在每次改尺寸之后调一次。
    //   3) 区域**没有抗锯齿**，圆角边缘会有 1px 级锯齿（深色条贴深色壁纸基本看不出，浅色壁纸上要有心理准备）。
    //   4) 一旦设过区域，切回"DWM 管圆角"时必须 ClearRoundRegion 清一次，
    //      否则旧区域会继续按老尺寸裁着窗口。
    //
    // ⛔ 别再走"透明窗口 + 自己画圆角"那条路：那要靠 DwmExtendFrameIntoClientArea 撑，
    //    而它会让 DWM 给整窗画一道边 —— 就是 Nick 2026-10-11 报的"判定区阴影"，两个开关是同一个。

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    /// <summary>
    /// 把窗口裁成圆角矩形（物理像素）。
    /// </summary>
    /// <param name="widthPx">窗口宽（物理像素）⚠️ 传**目标尺寸**，别传读回来的旧尺寸。</param>
    /// <param name="heightPx">窗口高（物理像素）。</param>
    /// <param name="radiusPx">圆角半径（物理像素）；会夹到不超过短边的一半。</param>
    public static void SetRoundRegion(IntPtr hwnd, int widthPx, int heightPx, int radiusPx)
    {
        if (hwnd == IntPtr.Zero || widthPx <= 0 || heightPx <= 0) return;
        try
        {
            var r = Math.Clamp(radiusPx, 0, Math.Min(widthPx, heightPx) / 2);
            if (r <= 0) return;

            // GDI 的区域是**不含右下边界**的：右下各多给 1px，否则会削掉最后一行/一列。
            var rgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, r, r);
            if (rgn == IntPtr.Zero) { ChromeLog("CreateRoundRectRgn 失败"); return; }

            // ⚠️ SetWindowRgn 之后这个区域归**系统**所有（文档明确），⛔ 不要再 DeleteObject —— 二次释放会崩。
            //    再设一次新区域时系统会自己换掉旧的。
            if (SetWindowRgn(hwnd, rgn, true) == 0) ChromeLog("SetWindowRgn 失败");
        }
        catch (Exception ex)
        {
            ChromeLog("圆角区域失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 撤掉窗口上自己裁的区域（恢复成完整的矩形窗口）。跟 <see cref="SetRoundRegion"/> 成对使用：
    /// 从"自己裁圆角"切回"DWM 管圆角"时必须清一次，否则旧区域会一直挂在窗口上，
    /// 把窗口按**当时那一版的尺寸**继续裁着（尺寸一变就露馅）。
    /// ⚠️ 传 <c>IntPtr.Zero</c> 是文档认可的清空方式，系统会回收旧区域，⛔ 别自己 DeleteObject。
    /// </summary>
    public static void ClearRoundRegion(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { SetWindowRgn(hwnd, IntPtr.Zero, true); }
        catch (Exception ex) { ChromeLog("清圆角区域失败: " + ex.Message); }
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
