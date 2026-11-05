using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 窗口背景材质。对应 WinUI 原版的 <c>Services/BackdropHost.cs</c>（原版用
/// <c>MicaBackdrop</c> / <c>DesktopAcrylicBackdrop</c>）。
///
/// ⛔ Win7 既没有 Mica 也没有 Acrylic，所以这里做**分级降级**，视觉意图保持一致：
///
///   Win11 22000+          → Mica / Mica Alt （DwmSetWindowAttribute：DWMSBT_MAINWINDOW、DWMSBT_TABBEDWINDOW）
///   Win10 1803+           → 亚克力          （Avalonia 合成背板 AcrylicBlur；Win10 没有 DWM 材质属性）
///   Win10 1507–1709       → 壁纸模糊底色    （既无 Mica 也无合成亚克力，17134 之前两条都没有）
///   Win8 / 8.1            → Aero 毛玻璃     （DwmEnableBlurBehindWindow + DwmExtendFrameIntoClientArea）
///   Win7                  → 壁纸模糊底色    （见 <see cref="FrostedGlass"/>，不透明）
///   远程会话              → 壁纸模糊底色    （DWM 不画材质，但桌面画面是本地合成的、壁纸清晰可读）
///   画不出材质的其它环境   → 纯色底          （虚拟显卡 / 透明效果被关，见 OsInfo.DwmMaterialsAvailable）
///
/// ⚠️ 壁纸模糊底色的使用范围（2026-11-04 定稿）：
///    **Win7、Win10 1803 以下、远程会话** —— 这三类环境本来就拿不到任何系统材质，
///    用不透明的「壁纸模糊 + 主题色罩」保住"有材质"的观感，比整窗死色好看。
///    Win10 1803+ 与 Win11 一律上系统原生材质；只有虚拟显卡、系统「透明效果」被关这类
///    「明明有能力却画不出来」的环境才退纯色 —— 在那里磨砂会被当成「Win7 的假亚克力」，
///    比没有材质更难解释清楚。
///
/// ⚠️ Win7 不参与上面任何一级系统材质：
///    没有 DWM 材质；Avalonia 11.2.8 在 Win7 上也没有透明窗口能力，因此改用不透明的
///    壁纸模糊底色近似磨砂效果。
///
/// ⚠️ **Win7 不参与「Aero 毛玻璃」这一级**（2026-10-15 收窄，原先含 Win7）。
///    根因：Avalonia 11.2.8 在 Win7 上**没有透明窗口能力** —— <c>WindowImpl</c> 既没有
///    <c>WS_EX_LAYERED</c> / <c>UpdateLayeredWindow</c>，<c>SetLegacyTransparency</c>
///    （<c>DwmEnableBlurBehindWindow</c>）又显式要求 Win8+，且
///    <c>IsSupported(Transparent) = !UseRedirectionBitmap || Version &gt;= Win8</c>
///    在 Win7（<c>UseRedirectionBitmap</c> 恒为 true）下为 false
///    → 实际 <c>TransparencyLevel</c> 落到 <c>None</c>。
///    于是这一级的 <c>Background = Transparent</c> 根本透明不了，窗口底被交给
///    <c>TransparencyBackgroundFallback</c>（默认**白色**）。叠加本级的
///    <c>DwmExtendFrameIntoClientArea</c>（把玻璃铺满整个客户区），DWM 接管了玻璃框架之后
///    <c>SetWindowRgn</c> 的圆角裁剪会被无视 —— 两者合起来就是用户报的
///    「Win7 开 Aero 时圆角有白边/白角」；当时关掉 Aero 会落到纯色分支，反而一切正常。
///    现在 Win7 一律在更前面就转壁纸模糊，压根进不到这一级。
///    详细判定见 <see cref="Apply"/> 里 Aero 分支前的注释。
///
/// ⚠️ 与原版一致的两条纪律：
///   ① 上了材质就把根元素背景置空，让材质透上来；
///   ② 完全不支持时**必须退纯色**，绝不能留透明根（否则露出来的是黑底）。
/// </summary>
public static class Backdrop
{
    /// <summary>SetWindowCompositionAttribute —— Win7 上没这个导出，必须动态解析。</summary>
    private delegate int SetWindowCompositionAttributeDelegate(IntPtr hwnd, ref NativeMethods.WINDOWCOMPOSITIONATTRIBDATA data);

    private static SetWindowCompositionAttributeDelegate? _setComposition;
    private static bool _compositionProbed;

    /// <summary>
    /// 给窗口上背景，返回**要写回设置**的 kind：<c>mica</c> / <c>acrylic</c> / <c>blur</c> / <c>solid</c>。
    /// ⚠️ 返回值会被调用方写回设置（MainWindow.ApplyBackdrop），因此不得返回词汇表以外的值；
    ///    系统材质画不出来而退纯色、或 Win7 退壁纸模糊时，仍**沿用传入的 kind** —— 否则
    ///    一次远程连接、一次虚拟机启动就会把用户的材质偏好永久改成纯色。
    /// </summary>
    /// <param name="win">目标窗口。</param>
    /// <param name="root">根面板；退纯色时用它刷底色，可空。</param>
    /// <param name="prefer">"mica" / "acrylic" / "solid"；不传则读设置。</param>
    /// <param name="primaryWindow">
    /// 是不是主窗口。**默认 false（按浮窗处理）是安全的那个方向** —— 见 <see cref="ApplyWin10Acrylic"/>：
    /// Win10 上只有主窗口能接受「整窗全透明 + 清空根面板底色」，浮窗那样做一旦合成背板没画出来
    /// 就是一片全透明，用户看到的是「悬浮窗不见了」。
    /// </param>
    public static string Apply(Window win, Panel? root = null, string? prefer = null, bool primaryWindow = false)
    {
        var kind = string.IsNullOrWhiteSpace(prefer) ? SettingBackdrop() : prefer!;
        kind = string.IsNullOrWhiteSpace(kind) ? "acrylic" : kind.Trim().ToLowerInvariant();

        var hwnd = TryGetHwnd(win);
        if (hwnd == IntPtr.Zero)
        {
            Solid(win, root);
            return "solid";
        }

        try
        {
            if (kind == "solid")
            {
                Solid(win, root);
                return "solid";
            }

            // ── Win7：壁纸模糊底色 ─────────────────────────────────────────
            // Win7 没有 DWM 材质，Avalonia 11.2.8 在它上面也拿不到透明窗口，所以这里用不透明的
            // 「壁纸模糊 + 主题色罩」近似磨砂，零运行开销。
            if (OsInfo.IsWindows7) return Frosted(win, root, hwnd, kind);

            // ── Win10 1507–1709（build < 17134）：壁纸模糊底色 ─────────────
            // Mica 要 22000、合成亚克力要 17134，这一段系统两条都没有。既然任何系统材质都上不去，
            // 就不要再落纯色 —— 壁纸模糊至少保留「有材质」的观感，与远程会话同一处理。
            // ⚠️ 只认 Win10：Win8 走下面的 Aero 分支（IsWindows10OrLater 为假），不受影响。
            if (OsInfo.IsWindows10OrLater && !OsInfo.SupportsAcrylicBlur)
                return Frosted(win, root, hwnd, kind);

            // ── Win11：Mica ────────────────────────────────────────────────
            if (kind == "mica" && OsInfo.SupportsMica)
            {
                // 系统材质画不出来的环境 → 远程会话走壁纸模糊，其余退纯色，见 OsInfo.DwmMaterialsAvailable
                if (!OsInfo.DwmMaterialsAvailable)
                    return SystemMaterialUnavailable(win, root, hwnd, "mica");

                // 先把 Ava 的平台透明提示打开，DWM 材质才透得上来。
                // ⛔ Win11 必须只声明 Transparent：声明 AcrylicBlur 会让 Avalonia 上
                //    合成背板（WinUiComposition 的 host backdrop brush + 高斯模糊），
                //    那是另一条与 DWM 背板并行的链路 —— 两条背板同时挂着时 DWM 会叠在
                //    合成面下面（2026-10-30 实测：整窗发灰、标题区发黑）；而在拿不到材质的
                //    机器上合成面直接渲染成黑色（2026-11-01 实测）。
                EnableAvaloniaTransparency(win,
                    WindowTransparencyLevel.Transparent,
                    WindowTransparencyLevel.None);
                var v = NativeMethods.DWMSBT_MAINWINDOW;   // MicaKind.Base（不用 BaseAlt，深色下偏色发脏）
                if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int)) == 0)
                {
                    win.Background = Brushes.Transparent;
                    if (root is not null) root.Background = null;
                    return "mica";
                }
                kind = "acrylic";                          // 云母失败 → 退亚克力
            }

            // Win7/8/10 没有 Mica：云母请求按亚克力走，而不是直接落纯色。
            // ⛔ 以前只在 kind=="acrylic" 时才进亚克力分支，于是 Win10 上「选云母 = 纯色」
            //    （OOBE 默认就是云母，一进来主窗口就没有材质）。Win10 1803+ 的系统亚克力
            //    （Avalonia 合成背板 / AccentPolicy）就在下面这条分支里，必须让它接得住。
            if (kind == "mica") kind = "acrylic";

            // ── Acrylic ───────────────────────────────────────────────────
            if (kind == "acrylic" && OsInfo.SupportsAcrylicBlur)
            {
                // ⛔ Win11 主窗口**别再用 DWMSBT_TRANSIENTWINDOW(3)**（2026-10-30 实机复测）。
                //    3 是 DWM 给「短命浮窗」的亚克力，拿它喂主窗口属于编外用法：有实时模糊的机器上
                //    看着是亚克力，可一旦模糊不可用（虚拟机 / 云桌面 / 远程会话 / 省电模式），
                //    DWM 退回的是**它自己的亚克力兜底色** —— 本机实测浅色 #D3D3D3、深色 #545454；
                //    同一台机器的 Mica 系列兜底却正好是 Fluent 设计色（浅 #F3F3F3 / 深 #202020）。
                //    结果就是「选亚克力 → 整窗发灰、比纯色还脏」（用户报的「亚克力整窗发灰」）。
                //    改用受支持的 DWMSBT_TABBEDWINDOW(4，Mica Alt)：材质更浓、和云母(2)仍可区分，
                //    兜底色也是设计色。
                //    别在 Win11 上碰 ACCENT_ENABLE_ACRYLICBLURBEHIND：它**返回成功但根本没有模糊**，
                //    窗口只是变成一块透明/发黑的脏玻璃（2026-10-29 实测：整窗发黑、内容发灰）。
                //    ——Win10 没有 DWMWA_SYSTEMBACKDROP_TYPE，那台机器才用 AccentPolicy。
                if (OsInfo.SupportsMica)
                {
                    // 系统材质画不出来的环境 → 远程会话走壁纸模糊，其余退纯色。
                    // 判定依据见 OsInfo.DwmMaterialsAvailable：这类机器上系统背板只会露出兜底设计色、
                    // 合成亚克力会整窗发黑，两条都比替代方案更难看。
                    if (!OsInfo.DwmMaterialsAvailable)
                        return SystemMaterialUnavailable(win, root, hwnd, "acrylic");

                    EnableAvaloniaTransparency(win,
                        WindowTransparencyLevel.Transparent,
                        WindowTransparencyLevel.None);
                    var a = NativeMethods.DWMSBT_TABBEDWINDOW;
                    if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref a, sizeof(int)) == 0)
                    {
                        win.Background = Brushes.Transparent;
                        if (root is not null) root.Background = null;
                        return "acrylic";
                    }

                    // DWM 不认（22000 早期版本）→ 退云母，至少是块正常材质，别落进"透明无模糊"
                    var m = NativeMethods.DWMSBT_MAINWINDOW;
                    if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref m, sizeof(int)) == 0)
                    {
                        win.Background = Brushes.Transparent;
                        if (root is not null) root.Background = null;
                        return "mica";
                    }
                }
                else
                {
                    return ApplyWin10Acrylic(win, root, hwnd, primaryWindow);
                }
            }

            // ── Win8 / 8.1：Aero 毛玻璃 ───────────────────────────────────
            // ⚠️ 只允许 Win8/8.1 走这条路（2026-10-15 从 `IsWindows7 || IsWindows8` 收窄）。
            //    两个硬约束：
            //    ① `DWM_BLURBEHIND` 是 Vista 时代的 API，Win10/11 上客户区会直接渲染成**纯黑**
            //       （实测 2026-10-01 云桌面）；设置里的 blur 在 Win10+ 一律转投 acrylic → mica → solid 级联。
            //    ② **Win7 必须排除**：Avalonia 11.2.8 在 Win7 上拿不到透明窗口
            //       （WindowImpl 无 WS_EX_LAYERED/UpdateLayeredWindow；SetLegacyTransparency 要求 Win8+；
            //        IsSupported(Transparent) 在 UseRedirectionBitmap=true 的 Win7 上为 false
            //        → TransparencyLevel 恒为 None）。
            //       这条分支依赖「窗口底透明」：`Background = Transparent` 在 Win7 上透明不了，
            //       窗口底会落到 TransparencyBackgroundFallback（默认白）；同时
            //       DwmExtendFrameIntoClientArea(-1,-1,-1,-1) 把玻璃铺满整个客户区，
            //       DWM 接管玻璃框架后 SetWindowRgn 的圆角裁剪被无视（日志"裁剪成功"、像素纹丝不动）。
            //       → 深色界面下圆角月牙一圈白，即用户报的「Win7 开 Aero 圆角有白边」。
            //       Win7 落到下面的 Solid 分支后：底色是不透明成品色、窗口没有玻璃框架，
            //       区域裁剪正常生效 → 圆角干净。Win7 上毛玻璃本来也无收益（内容不透明，玻璃在底下看不见）。
            var aeroAllowed = OsInfo.IsWindows8;

            if (aeroAllowed && OsInfo.IsDwmCompositionEnabled && TryApplyAeroBlur(hwnd))
            {
                EnableAvaloniaTransparency(win,
                    WindowTransparencyLevel.Blur,
                    WindowTransparencyLevel.Transparent,
                    WindowTransparencyLevel.None);
                win.Background = Brushes.Transparent;
                if (root is not null) root.Background = null;
                return "blur";
            }
            if (!aeroAllowed && kind == "blur")
            {
                // Win10+ 的 blur 请求：按 acrylic 处理（走上面的级联），避免落进 Aero 黑屏分支
                if (OsInfo.SupportsAcrylicBlur)
                {
                    if (OsInfo.SupportsMica)
                    {
                        if (!OsInfo.DwmMaterialsAvailable)
                            return SystemMaterialUnavailable(win, root, hwnd, "acrylic");

                        EnableAvaloniaTransparency(win,
                            WindowTransparencyLevel.Transparent,
                            WindowTransparencyLevel.None);

                        // Win11 不认 AccentPolicy 的模糊，按 DWM 亚克力上
                        // （同样用 4 而不是 3，理由见上面亚克力分支）
                        var t = NativeMethods.DWMSBT_TABBEDWINDOW;
                        if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref t, sizeof(int)) == 0)
                        {
                            win.Background = Brushes.Transparent;
                            if (root is not null) root.Background = null;
                            return "acrylic";
                        }
                    }
                    else
                    {
                        return ApplyWin10Acrylic(win, root, hwnd, primaryWindow);
                    }
                }
                Solid(win, root);
                return "solid";
            }

            Solid(win, root);
            return "solid";
        }
        catch
        {
            // 同理不返回 solid：异常不该改写用户的材质偏好，只是这次没画出来。
            Solid(win, root);
            return kind;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  各系统的具体实现
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Win10 亚克力：只走 Avalonia 的合成背板（WinUI 合成窗口 + <c>BlurEffect.Acrylic</c>）。
    ///
    /// <list type="number">
    ///   <item><b>合成背板</b> —— 依据 Avalonia 11.2.8 <c>WindowImpl.SetTransparencyAcrylicBlur</c>：
    ///         <c>SetUseHostBackdropBrush(true)</c>（Win11 专属，Win10 上是空操作）
    ///         + <c>SetLegacyTransparency(false)</c> + 合成面 <c>SetBlur(Acrylic)</c>；
    ///         <c>WinUiCompositionShared.MinAcrylicVersion = 10.0.15063</c>。
    ///         是否兑现由 <see cref="ScheduleAcrylicVerify"/> 在窗口显示后复核。</item>
    ///   <item><b>浮窗</b> → 半透明实色底（<see cref="Translucent"/>，0xE6）：合不合成都不至于整窗消失。</item>
    ///   <item><b>拿不到合成面 / 系统材质画不出来</b> → 纯色（<see cref="SolidFallback"/>）。
    ///         ⛔ 2026-11-03 起不再退壁纸模糊 —— 磨砂底只留给 Win7。</item>
    /// </list>
    ///
    /// <para>
    /// ⛔ 不走 <c>AccentPolicy</c>：合成模式下窗口是 <c>WS_EX_NOREDIRECTIONBITMAP</c>，
    /// AccentPolicy 对它不生效，只会把窗口搞成「透明但没材质」的脏底；
    /// 就算退回重定向表面，本项目自绘边框处理过 <c>WM_NCCALCSIZE</c>，
    /// <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c> 同样会"返回成功但没有模糊"。
    /// 这正是「Win10 亚克力看着像白纸贴在清晰壁纸上」的来源，别再启用它。
    /// </para>
    /// </summary>
    private static string ApplyWin10Acrylic(Window win, Panel? root, IntPtr hwnd, bool primaryWindow)
    {
        // 虚拟显卡 / 远程会话 / 透明效果关闭：合成背板同样画不出来（Win11 上是整窗发黑），
        // AccentPolicy 在这类机器上也没模糊 —— 远程会话走壁纸模糊，其余退纯色，别去赌。
        if (!OsInfo.DwmMaterialsAvailable)
            return SystemMaterialUnavailable(win, root, hwnd, "acrylic");

        // ⛔⛔ 浮窗不走合成背板（2026-11-03，用户报「Win10 开亚克力后悬浮窗不可见」）。
        //
        //    Win10 这条路的做法是：窗口背景设成**完全透明** + 把根面板底色**清成 null**，
        //    整个窗口的可见性全部押在 Avalonia 的 WinUI 合成背板上（BlurEffect.Acrylic）。
        //    主窗口押得起 —— 它下面还有卡片层、列表项、页面背景兜着，背板就算没画出来也看得见内容。
        //    浮窗押不起 —— 根面板（如 FileSearchWindow 的 Root）往往就是**整窗唯一的底色来源**，
        //    清掉之后窗口只剩内容里那些自带背景的零散子元素。
        //
        //    更要命的是复核判据：ScheduleAcrylicVerify 看到 ActualTransparencyLevel == AcrylicBlur
        //    就认定"已兑现"、不再兜底。可那只是"平台接受了 hint"，不代表真的画出了东西 ——
        //    Win10 上 WinUiComposition 依赖 ANGLE + D3D11，缺一样就是"窗口透明、要啥没啥"。
        //    本机是 Win11 26200（走 DWM 背板），所以这条路复现不出来。
        //
        //    结论：浮窗按 Translucent 给一层**实色半透明底**（0xE6，≈90% 不透明）——
        //    合成背板真工作时它盖在上面几乎看不出来，不工作时窗口照样清晰可见。
        //    牺牲的是模糊（Win10 的亚克力本来就不稳，见类注释里几次实测），换的是"窗口别消失"。
        if (!primaryWindow)
        {
            var fallback = Translucent(win, root, hwnd, "acrylic");
            PortLog.Step($"材质: acrylic 浮窗不走合成背板 → {fallback}"
                         + "（合成背板只被平台接受不代表真画得出来，全透明窗口会整窗消失）");
            return fallback;
        }

        EnableAvaloniaTransparency(win,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.None);

        win.Background = Brushes.Transparent;
        if (root is not null) root.Background = null;

        // ⛔ 这里**绝不能**紧接着读 ActualTransparencyLevel 判定成败（2026-11-03）。
        //    主窗口的材质是在构造函数里下的，那时 Avalonia 的 WinUiComposition 还没连上
        //    （要等窗口真正显示、渲染初始化），SetTransparencyAcrylicBlur 必然失败，
        //    读到的还是旧值 → 判定"没兑现" → 下面把提示降成 Transparent →
        //    刚声明的 AcrylicBlur 被自己撤掉。最终窗口是"透明但没有任何材质"：
        //    壁纸细节原样透上来，只剩内容区那层 50% 白纱 —— 就是用户截图里的
        //    「竹叶清晰可见、整窗发白泛蓝、一点模糊都没有」。
        //    改成窗口显示后再复核，见 ScheduleAcrylicVerify。
        ScheduleAcrylicVerify(win, root, hwnd);
        return "acrylic";
    }

    /// <summary>已排队等待复核的窗口句柄，避免同一窗口重复排队。</summary>
    private static readonly System.Collections.Generic.HashSet<IntPtr> PendingVerify = new();

    /// <summary>
    /// Win10 合成亚克力的**显示后复核**。
    ///
    /// <para>
    /// <c>TransparencyLevelHint</c> 是"意图"，<c>ActualTransparencyLevel</c> 才是平台兑现的结果。
    /// Win10 的亚克力只能由 Avalonia 的合成背板（WinUiComposition + <c>BlurEffect.Acrylic</c>）实现，
    /// 而合成面要等窗口显示之后才存在，所以在构造期读到的一定是旧值。这里等
    /// <c>Opened</c>（或运行中切换材质时直接排到 UI 队列）再判一次：
    /// 兑现了就保持透明；没兑现就强制重建一次透明管线再判，仍不行则退纯色
    /// （不透明成品色，远好过"透明裸奔"那层清晰的壁纸）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 不走 <c>AccentPolicy</c>：本项目的窗口是 GPU 渲染下的
    /// <c>WS_EX_NOREDIRECTIONBITMAP</c>，且自绘边框处理过 <c>WM_NCCALCSIZE</c>，
    /// 这两点都会让 <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c> "返回成功但没有模糊"，
    /// 正是之前"看起来设了、实际只有一层色纱"的来源。
    /// </para>
    /// </summary>
    private static void ScheduleAcrylicVerify(Window win, Panel? root, IntPtr hwnd)
    {
        // 同一窗口可能连续 Apply 多次（构造 / 主题 / 设置页），排队一次就够。
        if (!PendingVerify.Add(hwnd)) return;

        // 首次复核失败不立刻退回纯色：合成链（DesktopWindowTarget → 合成面 →
        // BlurEffect）是异步建立的，真机上窗口显示后往往还要再等一两百毫秒。
        // 早判会把"马上就好"误判成"不支持"，用户看到的仍是一张白纸。逐次重试后再定论。
        const int MaxAttempts = 8;
        var attempt = 0;

        void Check()
        {
            try
            {
                if (win.IsVisible == false) { PendingVerify.Remove(hwnd); return; }

                var actual = win.ActualTransparencyLevel;
                if (actual == WindowTransparencyLevel.AcrylicBlur)
                {
                    PendingVerify.Remove(hwnd);
                    PortLog.Step($"背景材质: acrylic → 合成背板已兑现（实际 {actual}，"
                                 + $"第 {attempt + 1} 次复核，"
                                 + $"D3D11={(GpuInfo.IsD3D11Available ? "可用" : "不可用")}）");
                    return;
                }

                if (attempt == 0)
                {
                    // 平台在显示前那次声明失败了，提示值没变的话它不会重新评估 ——
                    // 先把提示降到 None 再升回去，强制 Win32 端重建透明管线。
                    EnableAvaloniaTransparency(win, WindowTransparencyLevel.None);
                    EnableAvaloniaTransparency(win,
                        WindowTransparencyLevel.AcrylicBlur,
                        WindowTransparencyLevel.Transparent,
                        WindowTransparencyLevel.None);
                    win.Background = Brushes.Transparent;
                    if (root is not null) root.Background = null;
                }

                if (attempt++ < MaxAttempts)
                {
                    // 每 140ms 再看一眼，总计约 1.1s。窗口关闭时下面那步会自行退出。
                    var timer = new Avalonia.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(140)
                    };
                    timer.Tick += (_, _) =>
                    {
                        try { timer.Stop(); } catch { }
                        Check();
                    };
                    timer.Start();
                    return;
                }

                PendingVerify.Remove(hwnd);
                PortLog.Step($"背景材质: acrylic → 合成背板不可用（实际 {actual}，"
                             + $"复核 {attempt} 次，D3D11={(GpuInfo.IsD3D11Available ? "可用" : "不可用")}"
                             + "；Avalonia 的 WinUIComposition 只能架在 ANGLE 上）→ 纯色");

                // 兜底：远程会话走壁纸模糊，其余退纯色（收掉透明意图由两者内部完成，
                // 否则平台之后真兑现了亚克力，会和这层底叠在一起）。
                SystemMaterialUnavailable(win, root, hwnd, "acrylic");
            }
            catch (Exception ex)
            {
                PendingVerify.Remove(hwnd);
                Core.AppLog.Catch("backdrop", "复核亚克力材质生效情况", ex, Core.LogLevel.Debug);
            }
        }

        void OnOpened(object? sender, EventArgs e)
        {
            try { win.Opened -= OnOpened; } catch { }
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Check, Avalonia.Threading.DispatcherPriority.Background);
        }

        try
        {
            if (win.IsVisible)
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    Check, Avalonia.Threading.DispatcherPriority.Background);
            else
                win.Opened += OnOpened;
        }
        catch { }
    }

    /// <summary>Win7 的「毛玻璃」：blur-behind + 玻璃边框延伸。</summary>
    private static bool TryApplyAeroBlur(IntPtr hwnd)
    {
        try
        {
            var bb = new NativeMethods.DWM_BLURBEHIND
            {
                dwFlags = NativeMethods.DWM_BB_ENABLE,
                fEnable = true,
                hRgnBlur = IntPtr.Zero,          // 整个窗口
                fTransitionOnMaximized = true
            };
            var hr = NativeMethods.DwmEnableBlurBehindWindow(hwnd, ref bb);
            if (hr != 0) return false;

            // 玻璃边框铺满，模糊才盖住整个客户区
            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Avalonia 侧需要声明「我允许平台给我透明/模糊」，否则窗口会被强制刷成不透明底，
    /// DWM 的材质就被盖住了。声明失败不算致命，照常继续。
    ///
    /// ⚠️ 列表**按优先级**尝试，平台会取第一个它能兑现的级别 —— 所以列表内容决定 Avalonia 自己
    ///    走哪条实现：
    ///    · DWM 背板（Win11 Mica / 亚克力）：<b>只能给 Transparent</b>。给 AcrylicBlur 会让 Avalonia
    ///      自己去调 AccentPolicy，Win11 上那是"返回成功但没有模糊"的脏玻璃，还会把刚设好的
    ///      DWM 背板盖掉（2026-10-30 实测：选亚克力整窗发灰、顶部标题区发黑）。
    ///    · Win10 亚克力：给 AcrylicBlur（Avalonia 在 Win10 上走 WinUI 合成背板），
    ///      再读 <see cref="Window.ActualTransparencyLevel"/> 确认平台真的兑现了。
    ///    · Win8 Aero：给 Blur。
    /// </summary>
    private static void EnableAvaloniaTransparency(Window win, params WindowTransparencyLevel[] levels)
    {
        try
        {
            win.TransparencyLevelHint = levels;

            // 真机排障凭证：Win10 亚克力"有没有真上"就看这一行 ——
            // 实际级别不是 AcrylicBlur 时窗口没有材质，代码会退纯色/AccentPolicy。
            PortLog.Step($"透明级别: 提示 [{string.Join(",", levels)}] → 实际 {win.ActualTransparencyLevel}");
        }
        catch { /* 平台不支持时忽略 */ }
    }

    /// <summary>
    /// Win11 浮窗专用：把窗口底色与透明回退色都压成**全透**。
    ///
    /// <para>
    /// ── 为什么需要（2026-10-15 实测 Win11 26200）────────────────────────
    /// 无边框浮窗（WS_POPUP + WS_EX_NOREDIRECTIONBITMAP）的内容由 DWM 直接合成、
    /// **没有重定向表面，DWM 无视 SetWindowRgn**（应用日志"裁剪成功"、像素纹丝不动）——
    /// 区域裁剪在这类窗口上是死路。同时 Avalonia 的实际透明级别落到 None 时会把
    /// <c>TransparencyBackgroundFallback</c>（白）刷成窗口底色，面板圆角与窗口矩形
    /// 之间的月牙缝就漏出一条白边。
    /// </para>
    ///
    /// <para>
    /// 解法 = 圆角改由**内容自身的 alpha** 完成：底色全透后月牙 alpha=0，
    /// DWM 合成时直接露出桌面，配合面板自己的 CornerRadius 得到干净的圆角。
    /// 只该在 Win11+ 上调（Win7/10 靠区域裁剪，底色要保住成品色防白闪）。
    /// </para>
    /// </summary>
    public static void EnablePerPixelCorners(Window win)
    {
        try
        {
            win.Background = Brushes.Transparent;
            win.TransparencyBackgroundFallback = Brushes.Transparent;
        }
        catch { /* 属性设不上就算了：顶多月牙还是白的，不影响功能 */ }

        // accent 毛玻璃（若生效）会把整个窗口矩形垫上 60% 白的雾 —— 面板虽是实色看不见它，
        // 四角月牙却会透出奶白。Win11 浮窗的面板全是实色，毛玻璃毫无收益，直接关掉，
        // 月牙才是干净的「透出桌面」。
        try
        {
            var hwnd = TryGetHwnd(win);
            var fn = hwnd != IntPtr.Zero ? GetSetComposition() : null;
            if (fn is null) return;
            var policy = new NativeMethods.ACCENTPOLICY
            {
                AccentState = NativeMethods.ACCENT_DISABLED,
                AccentFlags = 0,
                GradientColor = 0,
                AnimationId = 0
            };
            var size = Marshal.SizeOf<NativeMethods.ACCENTPOLICY>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new NativeMethods.WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attribute = NativeMethods.WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                fn(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { /* 关不掉顶多月牙带雾，不影响功能 */ }
    }

    /// <summary>
    /// 系统材质画不出来时的统一出口：**远程会话用壁纸模糊，其余退纯色**。
    ///
    /// <para>
    /// 远程桌面 / 远程协助 / 云桌面里 DWM 不绘制系统材质（窗口会变成一块死色），
    /// 但这类会话的桌面画面是本地合成的、壁纸清晰可读，所以壁纸模糊底在这里效果接近真材质，
    /// 比整窗纯色好看且不突兀。其余原因（虚拟显卡、系统「透明效果」被关）没有这个前提，
    /// 一律退纯色。
    /// </para>
    /// </summary>
    private static string SystemMaterialUnavailable(Window win, Panel? root, IntPtr hwnd, string kind)
    {
        if (OsInfo.IsRemoteSession) return Frosted(win, root, hwnd, kind);
        return SolidFallback(win, root, kind);
    }

    /// <summary>
    /// 系统材质画不出来时的最终兜底：**纯色**。
    ///
    /// <para>只有 <see cref="SystemMaterialUnavailable"/> 的非远程会话分支会走到这里。
    /// 远程会话、虚拟显卡、系统「透明效果」被关这几种环境下 DWM 不绘制材质，
    /// 再往上挂背板只会得到「透明但无材质」的脏玻璃（清晰壁纸直接透上来），
    /// 因此这里老实退回不透明成品色，并把原因写进两本日志。</para>
    ///
    /// <para>
    /// ⚠️ 返回值刻意是**传入的 kind**，不是 <c>"solid"</c>：调用方会把它写回设置
    /// （见 MainWindow.ApplyBackdrop），返回 solid 等于「在远程桌面里开一次应用，
    /// 用户的材质偏好就被永久改成纯色」。保持原 kind 才能让环境恢复后自动变回材质。
    /// </para>
    /// </summary>
    private static string SolidFallback(Window win, Panel? root, string kind)
    {
        // 先把透明意图收掉：窗口若还挂着 Transparent，不透明底色会变成「半透明的纯色」——
        // 而且平台之后真兑现了材质，会和这层底叠在一起。
        EnableAvaloniaTransparency(win, WindowTransparencyLevel.None);
        Solid(win, root);

        var why = $"（{OsInfo.DescribeDwmMaterials()}，"
                  + $"D3D11={(GpuInfo.IsD3D11Available ? "可用" : "不可用")}）";
        Core.AppLog.Info("backdrop", $"材质降级｜目标={kind}｜实际=纯色｜原因=系统材质不可用"
            + $"｜判据={OsInfo.DescribeDwmMaterials()}｜D3D11={(GpuInfo.IsD3D11Available ? "可用" : "不可用")}");
        return kind;
    }

    /// <summary>
    /// 壁纸模糊底色：给**拿不到任何系统材质**的系统与会话兜底。
    ///
    /// <para>
    /// Win7 上 DWM 不给模糊，真透明只会让背后的桌面清晰地透上来 —— 观感是「透」而不是「材质」。
    /// 这里改用 <see cref="FrostedGlass"/> 生成的不透明位图（壁纸缩小重采样 + 主题色罩）作为窗口底色：
    /// 一次静态绘制，与纯色底同一量级，没有计时器、抓屏和逐帧重算。
    /// </para>
    /// <para>
    /// 服务对象：**Win7、Win10 1507–1709、远程会话**。前两者系统本身没有材质能力；
    /// 远程会话里 DWM 不绘制材质，但桌面画面是本地合成的、壁纸清晰可读，磨砂底效果接近真材质。
    /// Win10 1803+ / Win11 有能力上原生材质，不走这里；只有虚拟显卡、系统「透明效果」被关
    /// 这类「有能力却画不出来」的环境才退纯色，见 <see cref="SystemMaterialUnavailable"/>。
    /// </para>
    /// <para>
    /// 不修改 <see cref="Window.TransparencyLevelHint"/>：主窗口圆角依赖既有的合成/区域裁剪路径，
    /// 动它会改变窗口的合成方式。底色位图本身不透明，透明通道留着也不影响观感。
    /// </para>
    /// 壁纸不可用（纯色桌面 / 文件丢失）时退回 <see cref="Translucent"/>。
    /// </summary>
    private static string Frosted(Window win, Panel? root, IntPtr hwnd, string kind)
    {
        var dark = win.ActualThemeVariant == ThemeVariant.Dark;

        var brush = FrostedGlass.TryCreate(dark);
        if (brush is null) return Translucent(win, root, hwnd, kind);

        try
        {
            win.Background = brush;
            // 透明拿不到时露出来的是这一层，必须是不透明的成品色
            win.TransparencyBackgroundFallback = new SolidColorBrush(dark
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 243, 243, 243));
            if (root is not null) root.Background = null;
        }
        catch
        {
            Solid(win, root);
            return "solid";
        }

        var why = $"（{OsInfo.DescribeDwmMaterials()}，"
                  + $"D3D11={(GpuInfo.IsD3D11Available ? "可用" : "不可用")}）";

        // 同一条也写进 app.log：这是「为什么没有云母/亚克力」的唯一答案，
        // 以前只在 port.log 里，用户交上来的主日志看不出来。
        Core.AppLog.Info("backdrop", $"材质降级｜目标={kind}｜实际=壁纸模糊底色｜原因=系统材质不可用"
            + $"｜判据={OsInfo.DescribeDwmMaterials()}｜D3D11={(GpuInfo.IsD3D11Available ? "可用" : "不可用")}");
        return kind;
    }

    /// <summary>
    /// 半透明实色底：**真透明窗口 + 半透明设计色罩**。
    ///
    /// <para>两处调用点：</para>
    /// <list type="bullet">
    ///   <item>Win7 壁纸不可用（纯色桌面 / 壁纸文件丢失）—— 没有壁纸可模糊，让桌面真的透上来；</item>
    ///   <item>Win10 浮窗 —— 合成背板对浮窗不可依赖（清空根底色后合成面没画出来就是整窗消失）。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ 罩子必须自己给：内容层的 Fluent 画笔本身就是半透明的，没有底色会糊成一片、字都看不清。
    /// 连透明都拿不到时退回纯色。
    /// </para>
    /// </summary>
    private static string Translucent(Window win, Panel? root, IntPtr hwnd, string kind)
    {
        var none = NativeMethods.DWMSBT_NONE;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));

        EnableAvaloniaTransparency(win,
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.None);

        // 连透明都拿不到 → 老实退纯色，别留一块"透明无底"的脏玻璃
        if (win.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
        {
            Solid(win, root);
            return "solid";
        }

        try
        {
            // 透明度定在 0xE6：没有模糊时，背后的桌面/窗口是**清晰**透上来的，罩子一薄就成重影
            // （实测 0xB0 后面窗口的文件名肉眼可读），一厚就退回纯色、看不出材质（实测 0xF7）。
            // 0xE6 在本机壁纸上只剩一层淡色晕，观感与同机任务栏（同样只有透明、没有模糊）一致。
            var dark = win.ActualThemeVariant == ThemeVariant.Dark;
            win.Background = new SolidColorBrush(dark
                ? Color.FromArgb(0xE6, 32, 32, 32)
                : Color.FromArgb(0xE6, 243, 243, 243));
            // 透明拿不到时露出来的是这一层，必须是不透明的成品色（默认白）
            win.TransparencyBackgroundFallback = new SolidColorBrush(dark
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 243, 243, 243));
            if (root is not null) root.Background = null;
            PortLog.Step($"材质: {kind} 无壁纸可模糊 → 半透明底色");
        }
        catch { }

        return kind;
    }

    /// <summary>纯色兜底：与原版同一套色值（深 32,32,32 / 浅 243,243,243）。</summary>
    private static void Solid(Window win, Panel? root)
    {
        try
        {
            var dark = win.ActualThemeVariant == ThemeVariant.Dark;
            var brush = new SolidColorBrush(dark
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 243, 243, 243));

            win.Background = brush;

            // ⚠️ 2026-10-15：回退底色一并刷成品色。
            //    窗口的实际 TransparencyLevel 拿不到透明时（Win7 恒为 None），窗口底由
            //    TransparencyBackgroundFallback 决定，而它默认是**白色** ——
            //    圆角月牙、被内容盖不到的边缘都会露这一层白，即用户报的"白角白边"。
            //    刷成品色后即便兜底层露出来，也是与面板同色，观感上不再是白。
            //    本来就不透明的窗口（用户选 solid / 材质全失败）设它没有任何副作用。
            win.TransparencyBackgroundFallback = brush;

            if (root is not null) root.Background = brush;
        }
        catch { }
    }

    /// <summary>读设置里主窗口选的那个背景（读不到按亚克力）。</summary>
    private static string SettingBackdrop()
    {
        try
        {
            var b = App.Settings.Current.Backdrop;
            return string.IsNullOrWhiteSpace(b) ? "acrylic" : b;
        }
        catch { return "acrylic"; }
    }

    /// <summary>拿原生 HWND。</summary>
    public static IntPtr TryGetHwnd(Window win)
    {
        try
        {
            var handle = win.TryGetPlatformHandle();
            return handle?.Handle ?? IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>动态解析 SetWindowCompositionAttribute —— Win7 上 user32 里没有这个导出。</summary>
    private static SetWindowCompositionAttributeDelegate? GetSetComposition()
    {
        if (_compositionProbed) return _setComposition;
        _compositionProbed = true;

        try
        {
            var user32 = LoadLibrary("user32.dll");
            if (user32 == IntPtr.Zero) return null;

            var addr = GetProcAddress(user32, "SetWindowCompositionAttribute");
            if (addr == IntPtr.Zero) return null;

            _setComposition = Marshal.GetDelegateForFunctionPointer<SetWindowCompositionAttributeDelegate>(addr);
        }
        catch
        {
            _setComposition = null;
        }

        return _setComposition;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
}
