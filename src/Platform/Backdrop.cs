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
///   Win11 22000+          → Mica          （DwmSetWindowAttribute / DWMSBT_MAINWINDOW）
///   Win10 1803+           → Acrylic       （SetWindowCompositionAttribute / ACCENT_ENABLE_ACRYLICBLURBEHIND）
///   Win8 / 8.1            → Aero 毛玻璃    （DwmEnableBlurBehindWindow + DwmExtendFrameIntoClientArea）
///   以上都不可用 / Win7 / Aero关闭 → 纯色底 （深浅色各一，与原版同样的色值）
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
///    「Win7 开 Aero 时圆角有白边/白角」；关掉 Aero 会落到下面的纯色分支，反而一切正常。
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

    /// <summary>已经记过「Win7 退纯色底」那条日志了（材质会随主题/设置多次重算，只记第一条）。</summary>
    private static bool _loggedWin7Route;

    /// <summary>
    /// 给窗口上背景，返回**实际生效**的 kind：<c>mica</c> / <c>acrylic</c> / <c>blur</c> / <c>solid</c>。
    /// </summary>
    /// <param name="win">目标窗口。</param>
    /// <param name="root">根面板；退纯色时用它刷底色，可空。</param>
    /// <param name="prefer">"mica" / "acrylic" / "solid"；不传则读设置。</param>
    public static string Apply(Window win, Panel? root = null, string? prefer = null)
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

            // ── Win11：Mica ────────────────────────────────────────────────
            if (kind == "mica" && OsInfo.SupportsMica)
            {
                // 先把 Ava 的平台透明提示打开，DWM 材质才透得上来。
                // ⛔ Win11 必须只声明 Transparent：声明 AcrylicBlur 会让 Avalonia 自己去调
                //    SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)，
                //    Win11 上那是"成功但没模糊"的脏玻璃，还会盖掉刚设好的 DWM 背板
                //    （2026-10-30 实测：选亚克力整窗发灰、标题区发黑）。
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
                    return ApplyWin10Acrylic(win, root, hwnd);
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

            // 真机排障凭证：Win7 上「开 Aero 才有白边」这个问题，看这一行就能确认代码走了纯色底。
            if (OsInfo.IsWindows7 && !_loggedWin7Route)
            {
                _loggedWin7Route = true;
                PortLog.Step("材质: Win7 不走 Aero 毛玻璃（Avalonia 在 Win7 上无法透明，玻璃只会带来白边）→ 退纯色底");
            }
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
                        return ApplyWin10Acrylic(win, root, hwnd);
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
            Solid(win, root);
            return "solid";
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  各系统的具体实现
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Win10 亚克力，两级：
    ///
    /// <list type="number">
    ///   <item><b>先让 Avalonia 自己上</b> —— 合成背板（WinUI 合成窗口 + <c>BlurEffect.Acrylic</c>）。
    ///         依据 Avalonia 11.2.8 <c>WindowImpl.SetTransparencyAcrylicBlur</c>：
    ///         <c>SetUseHostBackdropBrush(true)</c>（Win11 专属，Win10 上是空操作）
    ///         + <c>SetLegacyTransparency(false)</c> + 合成面 <c>SetBlur(Acrylic)</c>；
    ///         <c>WinUiCompositionShared.MinAcrylicVersion = 10.0.15063</c>。</item>
    ///   <item><b>合成面拿不到时</b>（软件渲染 → <c>_glSurface</c> 不是
    ///         <c>ICompositionEffectsSurface</c>）再退回老路子：透明提示 + AccentPolicy。</item>
    /// </list>
    ///
    /// ⛔ 绝不能在合成亚克力之上再补 AccentPolicy：合成模式下窗口是
    ///    <c>WS_EX_NOREDIRECTIONBITMAP</c>，AccentPolicy 对它不生效，只会把窗口搞成
    ///    「透明但没材质」的脏底 —— 就是用户报的「Win10 亚克力还是有问题」。
    /// </summary>
    private static string ApplyWin10Acrylic(Window win, Panel? root, IntPtr hwnd)
    {
        EnableAvaloniaTransparency(win,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.None);

        // 平台真的兑现了 AcrylicBlur 才算数（只认实际级别，不认我们的意图）。
        if (win.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
        {
            win.Background = Brushes.Transparent;
            if (root is not null) root.Background = null;
            return "acrylic";
        }

        // 拿不到合成亚克力：AccentPolicy 在重定向表面模式下才是真模糊。
        // 提示切回 Transparent —— 这条老路就靠「透明窗口 + accent」出效果。
        EnableAvaloniaTransparency(win,
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.None);
        if (TryApplyAcrylic(hwnd, win.ActualThemeVariant == ThemeVariant.Dark))
        {
            win.Background = Brushes.Transparent;
            if (root is not null) root.Background = null;
            return "acrylic";
        }

        // ⚠️ 这里必须退纯色：透明提示可能已经生效，留个"透明无材质"的窗口在 Win10 上
        //    露出来的是黑底/脏底，比没有材质更糟。
        Solid(win, root);
        return "solid";
    }

    /// <summary>
    /// Win10 1803+ 的 AccentPolicy 亚克力（<b>只作合成背板不可用时的兜底</b>，
    /// 见 <see cref="ApplyWin10Acrylic"/>）。Win11 上这个 API 也"成功"但没有模糊，不要用。
    /// </summary>
    private static bool TryApplyAcrylic(IntPtr hwnd, bool dark)
    {
        var fn = GetSetComposition();
        if (fn is null) return false;

        try
        {
            var policy = new NativeMethods.ACCENTPOLICY
            {
                AccentState = NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0x20,        // 2 = 给整个客户区上模糊
                // AABBGGRR：深色 80% #1F1F1F / 浅色 60% 白。写死白底在深色模式下整窗是奶白的雾。
                GradientColor = dark ? unchecked((int)0xCC1F1F1F) : unchecked((int)0x99FFFFFF),
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
                // ⛔ 这里原来是 `== 0`，方向搞反了：SetWindowCompositionAttribute 返回的是
                //    **BOOL**（成功 = 非 0），不是 HRESULT。于是「亚克力成功」被当成失败：
                //    Win10 上必然落到纯色兜底（用户报的「Win10 亚克力不能用」就是它），
                //    Win11 上则总是退到 mica 分支 —— 看起来"有材质"，实际从没上过亚克力。
                return fn(hwnd, ref data) != 0;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { return false; }
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
