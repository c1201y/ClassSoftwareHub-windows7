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
///   Win7 / Win8 / 8.1     → Aero 毛玻璃    （DwmEnableBlurBehindWindow + DwmExtendFrameIntoClientArea）
///   以上都不可用 / Aero关闭 → 纯色底        （深浅色各一，与原版同样的色值）
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
                // 先把 Ava 的平台透明提示打开，DWM 材质才透得上来
                EnableAvaloniaTransparency(win);
                var v = NativeMethods.DWMSBT_MAINWINDOW;   // MicaKind.Base（不用 BaseAlt，深色下偏色发脏）
                if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int)) == 0)
                {
                    win.Background = Brushes.Transparent;
                    if (root is not null) root.Background = null;
                    return "mica";
                }
                kind = "acrylic";                          // 云母失败 → 退亚克力
            }

            // ── Win10 1803+：Acrylic ───────────────────────────────────────
            if (kind == "acrylic" && OsInfo.SupportsAcrylicBlur)
            {
                EnableAvaloniaTransparency(win);
                if (TryApplyAcrylic(hwnd))
                {
                    win.Background = Brushes.Transparent;
                    if (root is not null) root.Background = null;
                    return "acrylic";
                }
                // 亚克力不支持 → 退云母（与原版 BackdropHost 的互备顺序一致）
                if (OsInfo.SupportsMica)
                {
                    var v = NativeMethods.DWMSBT_MAINWINDOW;
                    if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int)) == 0)
                    {
                        win.Background = Brushes.Transparent;
                        if (root is not null) root.Background = null;
                        return "mica";
                    }
                }
            }

            // ── Win7 / Win8 / 8.1：Aero 毛玻璃 ──────────────────────────────
            // ⚠️ 只允许 Win7/8 走这条路：DWM_BLURBEHIND 是 Vista 时代的 API，
            //    Win10/11 上客户区会直接渲染成**纯黑**（实测 2026-10-01 云桌面）。
            //    设置里的 blur 在 Win10+ 一律转投 acrylic → mica → solid 级联。
            var aeroAllowed = OsInfo.IsWindows7 || OsInfo.IsWindows8;
            if (aeroAllowed && OsInfo.IsDwmCompositionEnabled && TryApplyAeroBlur(hwnd))
            {
                EnableAvaloniaTransparency(win);
                win.Background = Brushes.Transparent;
                if (root is not null) root.Background = null;
                return "blur";
            }
            if (!aeroAllowed && kind == "blur")
            {
                // Win10+ 的 blur 请求：按 acrylic 处理（走上面的级联），避免落进 Aero 黑屏分支
                if (OsInfo.SupportsAcrylicBlur)
                {
                    EnableAvaloniaTransparency(win);
                    if (TryApplyAcrylic(hwnd))
                    {
                        win.Background = Brushes.Transparent;
                        if (root is not null) root.Background = null;
                        return "acrylic";
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

    private static bool TryApplyAcrylic(IntPtr hwnd)
    {
        var fn = GetSetComposition();
        if (fn is null) return false;

        try
        {
            var policy = new NativeMethods.ACCENTPOLICY
            {
                AccentState = NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0x20,        // 2 = 给整个客户区上模糊
                GradientColor = unchecked((int)0x99FFFFFF),   // AABBGGRR，白底微透
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
                return fn(hwnd, ref data) == 0;
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
    /// </summary>
    private static void EnableAvaloniaTransparency(Window win)
    {
        try
        {
            win.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.None
            };
        }
        catch { /* 平台不支持时忽略 */ }
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
