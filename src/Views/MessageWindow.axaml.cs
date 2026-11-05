using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 「桌面留言」的悬浮窗：把一句话钉在桌面上（置顶、可拖、悬停出关闭键）。
///
/// <para>
/// 定位与 <see cref="StickerWindow"/> 同源 —— **内容自己决定窗口大小**（这里不再让用户拉边框）：
///   · 文字与字号决定宽度，超过了就按 <see cref="MaxWidthDip"/> 折行；
///   · 窗口尺寸 = 实测出来的内容尺寸（物理像素），摆位走 Win32 <c>SetWindowPos</c>；
///   · 整窗拖动走 <see cref="Window.BeginMoveDrag"/>（系统的模态移动循环），
///     ⛔ 别改成"指针捕获 + 自己算位移"：真机 Win7 上按下即 PointerCaptureLost，拖不动（踩过）。
/// </para>
///
/// <para>
/// ⚠️ 透明（无底色）：<see cref="MessageConfig.Transparent"/> 只是**用户意愿**，
/// 真正的逐像素透明要系统合成支持（Win7 经典/基本主题、关了 DWM 时并不成立）。
/// 所以显示之后**必须**按运行时拿到的 <see cref="TopLevel.ActualTransparencyLevel"/> 复核一次：
/// 拿到 <see cref="WindowTransparencyLevel.None"/> 就说明平台给不了逐像素 alpha。
/// 在那种系统上不再退回"半透明"（2026-11-02 用户明确要求全透明），改走色键：
/// 见 <see cref="ApplyLegacyTranslucency"/> —— 底色被整块抠掉，只有文字留在桌面上。
/// </para>
/// </summary>
public sealed partial class MessageWindow : Window
{
    /// <summary>同时只留一个悬浮窗（跟全屏时钟同一个道理：连点几下不该叠出一堆）。</summary>
    private static MessageWindow? _instance;

    /// <summary>面板内边距（dip）。跟着字号走，字大字小都好看 —— 见 <see cref="ApplyContent"/>。</summary>
    private const double PadYRatio = 0.45;

    /// <summary>内容最宽（dip）。超了就折行，窗口不会长成一条横跨屏幕的带子。</summary>
    private const double MaxWidthDip = 900;

    /// <summary>悬浮窗整个开着吗（给工具页那颗「收起悬浮窗」按钮判可用性）。</summary>
    public static bool IsOpen => _instance is { } w && w.IsVisible;

    private MessageConfig _cfg = new();

    /// <summary>圆角裁剪（只在"不透明"这一路上挂；透明窗口靠 Border 自己的圆角）。</summary>
    private RoundedCorners? _corners;

    /// <summary>运行时的真实透明结果（拿不到就 false → 用底色）。</summary>
    private bool _realTransparent;

    /// <summary>
    /// 色键没设上时的退路：整窗 alpha。取 200（≈78%）—— 再低文字就开始发灰、教室后几排看不清。
    /// 只在 <c>LWA_COLORKEY</c> 返回 false（极少见的驱动/合成组合）时用。
    /// </summary>
    private const byte LegacyFallbackAlpha = 200;

    /// <summary>
    /// 上次应用的分层透明状态（null=还没动过）——只用来避免反复写日志。
    /// </summary>
    private bool? _legacyAlphaState;

    private MessageWindow()
    {
        InitializeComponent();

        Title = "桌面留言";

        // 悬停露头/收起关闭键：平时连命中测试都不参与，不挡拖动
        Root.PointerEntered += (_, _) => ShowClose(true);
        Root.PointerExited += (_, _) => ShowClose(false);

        // 拖动：整窗都是把手，按到关闭键就交回给它
        Root.AddHandler(InputElement.PointerPressedEvent, OnDragPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);

        Closed += (_, _) =>
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    // ══════════ 对外入口 ══════════

    /// <summary>按这份留言配置显示（已经开着就**就地换内容**并拉回最前，不叠新窗口）。</summary>
    public static void Show(MessageConfig cfg)
    {
        try
        {
            _instance ??= new MessageWindow();
            _instance.Present(cfg);
        }
        catch (Exception ex)
        {
            AppLog.Catch("message", "显示留言悬浮窗", ex);
        }
    }

    /// <summary>
    /// 悬浮窗**开着**时把内容换掉（不移动位置、不抢焦点）。
    /// 用途：工具页里调字号/颜色/文字时，桌面上的那条留言跟着变，所见即所得。
    /// ⛔ 不激活窗口 —— 用户正在页面上打字，弹一下焦点就跑掉了。
    /// </summary>
    public static void RefreshIfOpen(MessageConfig cfg)
    {
        try
        {
            if (_instance is not { } w || !w.IsVisible) return;
            w._cfg = cfg;
            w.ApplyContent();
            w.ApplyAppearance();

            var (width, height) = w.MeasureContent();
            w.SetBounds(w.Position, width, height);      // 只改尺寸，位置原地不动
            w._corners?.Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Catch("message", "就地刷新留言悬浮窗", ex);
        }
    }

    /// <summary>收起来（下次再显示还认上次拖到的位置）。</summary>
    public static void HideNow()    {
        try
        {
            _instance?.SavePosition();
            _instance?.Hide();
        }
        catch (Exception ex)
        {
            AppLog.Catch("message", "收起留言悬浮窗", ex);
        }
    }

    /// <summary>退出应用时**真正销毁**（跟"收起来"是两回事，理由见 ToolPaletteWindow.CloseForExit）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
    }

    // ══════════ 显示流程 ══════════

    private void Present(MessageConfig cfg)
    {
        _cfg = cfg;

        ApplyContent();                 // 文字 / 字号 / 颜色 / 粗体 / 内边距
        ApplyTransparencyHint();        // ⚠️ 必须在 Show 之前声明，显示之后再改就晚了

        var (w, h) = MeasureContent();
        var pos = BestPosition(w, h);

        // ⛔⛔ 判据必须是 IsVisible，**不能**用一个"是否显示过"的标志位（本仓库踩过，见
        //     avalonia-blank-window-content 技能）：Avalonia 的 TopLevel 在没走过 Show() 之前
        //     整棵视觉树一个像素都不画；而 Hide() 之后 IsVisible 又变回 false ——
        //     用标志位的话"收起再打开"这条路上会跳过 Show()，屏幕上只剩一块白窗。
        if (!IsVisible)
        {
            var scale = RenderScaling > 0 ? RenderScaling : 1.0;
            try
            {
                // 先把尺寸位置按 DIP 摆好再 Show，免得先闪一帧默认尺寸（原版同样的做法）
                Width = w / scale;
                Height = h / scale;
                Position = pos;
            }
            catch { }

            Show();
        }
        else
        {
            var hwnd = Backdrop.TryGetHwnd(this);
            if (hwnd != IntPtr.Zero) NativeMethods.ShowWindow(hwnd, 5 /*SW_SHOW*/);
            Activate();
        }

        // 显示之后才拿得到真实透明等级 → 才能决定"底色 or 透明"和"要不要裁圆角"
        _realTransparent = cfg.Transparent
                           && ActualTransparencyLevel != WindowTransparencyLevel.None;
        ApplyAppearance();

        // 上面那次是估算（Show 前量不到真实窗口），这里按实测尺寸再对一次
        (w, h) = MeasureContent();
        pos = BestPosition(w, h);
        SetBounds(pos, w, h);

        // 显示之后收尾：去掉系统给无边框窗画的那 1px 边框 + 补一次圆角裁剪
        var hwnd2 = Backdrop.TryGetHwnd(this);
        if (hwnd2 != IntPtr.Zero)
        {
            // 色键全透明档不要圆角边框：那一圈会被抠色后留下一条半透明的描边。
            WindowChrome.RemoveBorder(hwnd2, rounded: !_realTransparent && !_cfg.Transparent, dark: false);
            WindowChrome.MarkToolWindow(hwnd2);   // 别让任务管理器把留言单列成一条「应用」
        }
        _corners?.Refresh();

        AppLog.Info("message",
            $"留言悬浮窗已显示｜尺寸={w}x{h}px｜位置={pos.X},{pos.Y}｜逐像素透明={_realTransparent}"
            + $"｜透明级别={ActualTransparencyLevel}｜整窗半透明={_legacyAlphaState == true}");
    }

    /// <summary>把留言内容铺到 TextBlock 上。</summary>
    private void ApplyContent()
    {
        var text = string.IsNullOrWhiteSpace(_cfg.Text) ? "（还没有内容）" : _cfg.Text;

        Body.Text = text;
        Body.FontSize = _cfg.FontSize;
        Body.FontWeight = _cfg.Bold ? FontWeight.Bold : FontWeight.Normal;
        Body.LineHeight = Math.Round(_cfg.FontSize * 1.32);
        Body.Foreground = Brush(_cfg.TextColor, Colors.Black);
        Body.Opacity = string.IsNullOrWhiteSpace(_cfg.Text) ? 0.5 : 1;

        // 内边距跟着字号走；Body.MaxWidth 决定折行点
        var padY = Math.Round(Math.Clamp(_cfg.FontSize * PadYRatio, 12, 46));
        var padX = Math.Round(Math.Clamp(_cfg.FontSize * 0.85, 18, 60));
        var maxW = Math.Max(200, MaxWidthDip - padX * 2);

        Panel.Padding = new Thickness(padX, padY, padX, padY);
        Body.MaxWidth = maxW;
    }

    /// <summary>显示前声明"我允许平台给我透明"（拿不到就自动降级，见类注释）。</summary>
    private void ApplyTransparencyHint()
    {
        try
        {
            // ⚠️ Win8+ 即使不要"整窗透明"，也要把 Transparent 写进 hint ——
            //    圆角要靠内容 alpha 抠（见 ApplyAppearance 里的 alpha 分支），
            //    平台不允许透明时四角就会退回窗口底色。
            TransparencyLevelHint = _cfg.Transparent || BackdropTheme.SupportsAlphaCorners
                ? new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None }
                : new[] { WindowTransparencyLevel.None };
        }
        catch { /* 平台不支持就忽略，走不透明那条路 */ }
    }

    /// <summary>按"底透不透"画面板：透明 → 无底无边（圆角交给 Border）；不透明 → 用户底色 + 淡描边 + 窗口区域裁剪。</summary>
    private void ApplyAppearance()
    {
        try
        {
            var textColor = Color(_cfg.TextColor, Colors.Black);

            if (_realTransparent)
            {
                Background = Brushes.Transparent;
                Panel.Background = Brushes.Transparent;
                Panel.BorderThickness = new Thickness(0);
            }
            else if (_cfg.Transparent)
            {
                // ── 平台给不了逐像素透明（Win7 / 无合成）→ 色键 ────────────────────
                // 整窗（含面板）只刷"键色"，系统把这一个颜色整块抠成全透明；
                // 文字主体与键色差 1 个色阶，所以文字留着。
                // ⚠️ 这一档**不画描边、不铺用户选的底色** —— 用户要的是"没有底"，不是"换个底"；
                //    描边还会在抠色后留下一条半透明的框。
                var keyBrush = new SolidColorBrush(LegacyKeyColor(textColor));
                Background = keyBrush;
                TransparencyBackgroundFallback = Brushes.Transparent;
                Panel.Background = keyBrush;
                Panel.BorderThickness = new Thickness(0);

                // 色键下文字必须整块不透明：Body 只要带一点整体 Opacity，
                // 文字色就会被"稀释"到键色附近，整段话跟着被抠掉（空内容占位符就是这么消失的）。
                Body.Opacity = 1;
            }
            else
            {
                var back = Brush(_cfg.BackColor, Colors.White);
                if (BackdropTheme.SupportsAlphaCorners)
                {
                    // ⚠️ Win8+（尤其是 Win10/11 的 GPU 渲染）：窗口带 WS_EX_NOREDIRECTIONBITMAP，
                    //    DWM 不认 SetWindowRgn，区域裁剪无效。底色只刷 Panel，窗口底保持全透，
                    //    月牙由 DWM 合成时露出桌面（见 Backdrop.EnablePerPixelCorners）。
                    Background = Brushes.Transparent;
                    TransparencyBackgroundFallback = Brushes.Transparent;
                }
                else
                {
                    Background = back;           // 窗口底也铺同色：万一区域裁剪差一个像素，露出来的也是同色
                }
                Panel.Background = back;
                Panel.BorderThickness = new Thickness(1);
                // 描边用文字色兑一点透明 —— 底色换成深色时边框自动跟着变，不会出现"黑框配黑底"
                Panel.BorderBrush = new SolidColorBrush(textColor, 0.18);
            }

            // 关闭键的叉叉跟着文字色走（默认那支是深灰，压在深色底上看不见）
            CloseGlyph.Stroke = new SolidColorBrush(textColor, 0.55);

            // Win7 等"平台不给逐像素透明"的系统：底色照旧铺不透明色，
            // 再用分层窗口把整窗调成半透明（见 ApplyLegacyTranslucency）。
            ApplyLegacyTranslucency();

            // ⚠️ 不透明才裁圆角：透明窗口裁掉四角会连"圆角之外本该透出桌面"的部分一起切掉
            if (_realTransparent)
            {
                RoundedCorners.Clear(this);
                return;
            }

            // 色键全透明档同理：四角本来就是键色、已经被系统抠透了，再裁一次只会多出一圈硬边；
            // ⚠️ 但从"不透明"切到这一档时必须**主动撤掉**旧的 SetWindowRgn，
            //    否则窗口还保持着上一轮的圆角区域（表现为四个角被切掉一块）。
            if (_cfg.Transparent)
            {
                RoundedCorners.Clear(this);
                return;
            }

            // Win8+ 且 DWM 合成时，圆角由面板 alpha 抠（见 Platform.BackdropTheme）；
            // 区域裁剪只在 Win7 / 无 DWM（此时窗口走重定向位图）才挂得上。
            if (!BackdropTheme.SupportsAlphaCorners)
                _corners ??= RoundedCorners.Attach(this, RoundedCorners.DefaultCornerRadiusDip, force: true);
        }
        catch (Exception ex)
        {
            AppLog.Catch("message", "应用留言悬浮窗外观", ex);
        }
    }

    /// <summary>
    /// 平台拿不到逐像素透明时的兜底：<c>WS_EX_LAYERED</c> + <c>SetLayeredWindowAttributes(LWA_COLORKEY)</c>。
    ///
    /// <para>
    /// ── 为什么从 LWA_ALPHA 换成 LWA_COLORKEY（2026-11-02 用户：「开 Aero 的机器上背景只能半透明，改成全透明」）
    /// <c>LWA_ALPHA</c> 是**整窗统一**的透明度：底色透出来的同时文字也一起变淡，
    /// 最好也只能做到"半透明"（原来取 200/255）。用户要的是"只有文字浮在桌面上"，
    /// 那就只有色键能做到 —— 把背景刷成一个特定的颜色，系统只抠这一个颜色，文字一点不动。
    /// </para>
    ///
    /// <para>
    /// ── 键色是怎么选的（这是整个方案能不能看的关键）
    /// 取 <b>文字色偏移 1 个色阶</b>，见 <see cref="LegacyKeyColor"/>。
    /// 差 1 阶意味着：
    /// <list type="number">
    ///   <item>文字主体永远不等于键色 → 不会被抠掉；</item>
    ///   <item>背景是纯键色 → 整块变透明；</item>
    ///   <item>抗锯齿边缘的过渡像素落在两者之间被一并抠掉 —— 而这些像素本来就该是半透明的，
    ///         所以既不会留下"一圈底色毛边"，文字也不会缺角。</item>
    /// </list>
    /// 以前注释里"色键会留一圈底色毛边"的顾虑，是因为用**用户选的底色**当键色（差得远）；
    /// 贴着文字色取键色这个问题就不存在了。
    /// </para>
    ///
    /// <para>
    /// ⛔ 仍然不用 <c>UpdateLayeredWindow</c> 自己送位图：那要接管整条渲染链路，代价远大于收益。
    /// </para>
    /// </summary>
    private void ApplyLegacyTranslucency()
    {
        var hwnd = Backdrop.TryGetHwnd(this);
        if (hwnd == IntPtr.Zero) return;

        var want = _cfg.Transparent && !_realTransparent;

        try
        {
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            var layered = (ex & NativeMethods.WS_EX_LAYERED) != 0;

            if (want)
            {
                var turnedOn = false;
                if (!layered)
                {
                    NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                        new IntPtr(ex | NativeMethods.WS_EX_LAYERED));
                    turnedOn = true;
                }

                var keyColor = LegacyKeyColor(Color(_cfg.TextColor, Colors.Black));
                var key = NativeMethodsEx.ToColorRef(keyColor.R, keyColor.G, keyColor.B);

                var ok = NativeMethods.SetLayeredWindowAttributes(
                    hwnd, key, 0, NativeMethods.LWA_COLORKEY);
                var byColorKey = ok;

                if (!ok)
                {
                    // 色键没设上（极少见）→ 退回整窗半透明：至少"能透出来"，
                    // 总比留成一块实心色板好。日志里会写明是哪一档。
                    ok = NativeMethods.SetLayeredWindowAttributes(
                        hwnd, 0, LegacyFallbackAlpha, NativeMethods.LWA_ALPHA);
                }

                // ⚠️ 刚挂上 WS_EX_LAYERED 的窗口不会自己重画，色键要等一整帧重绘才生效 ——
                //    不补这一步就会重演"刚打开还是实心的，拖一下才变透明"。
                if (turnedOn) RepaintWholeWindow(hwnd);

                if (_legacyAlphaState != true)
                {
                    _legacyAlphaState = true;
                    AppLog.Warning("message",
                        !ok ? $"透明方案降级｜逐像素=不可用｜色键=不可用｜整窗半透明=不可用｜实际=不透明底色｜透明级别={ActualTransparencyLevel}"
                        : byColorKey
                            ? $"透明方案降级｜逐像素=不可用｜实际=色键全透明｜键色=#{key:X6}｜透明级别={ActualTransparencyLevel}"
                            : $"透明方案降级｜逐像素=不可用｜色键=失败｜实际=整窗半透明 alpha={LegacyFallbackAlpha}｜透明级别={ActualTransparencyLevel}");
                }
            }
            else if (layered)
            {
                // 用户关掉「透明」后必须真的恢复不透明：撤掉色键、摘掉分层窗口样式，再重画一次。
                NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA);
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                    new IntPtr(ex & ~(long)NativeMethods.WS_EX_LAYERED));
                RepaintWholeWindow(hwnd);

                if (_legacyAlphaState != false)
                {
                    _legacyAlphaState = false;
                    AppLog.Info("message", "透明方案=已关闭色键｜实际=不透明底色");
                }
            }
        }
        catch (Exception ex2)
        {
            AppLog.Catch("message", "应用色键透明", ex2);
        }
    }

    /// <summary>
    /// 色键模式下用来"抠掉背景"的颜色：取文字色**偏移 1 个色阶**。
    /// 文字主体与键色永不相等（不会被抠），背景纯键色（整块透明），
    /// 抗锯齿边缘的过渡像素被一并抠掉 —— 详见 <see cref="ApplyLegacyTranslucency"/>。
    /// </summary>
    private static Color LegacyKeyColor(Color text)
    {
        static byte Step(byte v) => v > 0 ? (byte)(v - 1) : (byte)1;

        // ⚠️ 必须写全名 Avalonia.Media.Color：本类里有个同名方法 Color(string?, Color)，
        //    直接写 Color.FromRgb 会被解析成"在方法组上取成员"，编译报 CS0119。
        return Avalonia.Media.Color.FromRgb(Step(text.R), Step(text.G), Step(text.B));
    }

    /// <summary>改完窗口样式（分层 / 色键 / 区域）后立刻重绘整窗 —— 否则要等用户拖一下才生效。</summary>
    private static void RepaintWholeWindow(IntPtr hwnd)
    {
        try
        {
            NativeMethodsEx.InvalidateRect(hwnd, IntPtr.Zero, true);
            NativeMethodsUtil.UpdateWindow(hwnd);
        }
        catch
        {
            // 重绘失败不影响正确性：下一次正常重绘就会带上色键
        }
    }

    /// <summary>实测内容尺寸（物理像素）。</summary>
    private (int W, int H) MeasureContent()
    {
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        try
        {
            Panel.Measure(new Size(MaxWidthDip, double.PositiveInfinity));
            var d = Panel.DesiredSize;
            return (Math.Max(60, (int)Math.Ceiling(d.Width * scale)),
                    Math.Max(40, (int)Math.Ceiling(d.Height * scale)));
        }
        catch
        {
            return ((int)Math.Round(320 * scale), (int)Math.Round(120 * scale));
        }
    }

    /// <summary>
    /// 摆放：用户拖过就用他放的位置（夹回工作区）；没拖过就摆屏幕正中。
    /// 坐标全是物理像素（跟 <see cref="Position"/> / SetWindowPos 一致）。
    /// </summary>
    private PixelPoint BestPosition(int wPx, int hPx)
    {
        var work = WorkArea();
        var hasSaved = _cfg.PosVersion >= MessageConfig.CurrentPosVersion
                       && _cfg.X > -10000 && _cfg.Y > -10000 && _cfg.X < 100000 && _cfg.Y < 100000;

        var x = hasSaved ? _cfg.X : work.X + (work.Width - wPx) / 2;
        var y = hasSaved ? _cfg.Y : work.Y + (work.Height - hPx) / 2;

        x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - wPx));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - hPx));
        return new PixelPoint(x, y);
    }

    /// <summary>一次把位置和尺寸设下去（物理像素）。</summary>
    private void SetBounds(PixelPoint p, int wPx, int hPx)
    {
        var hwnd = Backdrop.TryGetHwnd(this);
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, wPx, hPx,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            return;
        }

        // 句柄还没就绪（极早期）：退回 Avalonia 属性，逻辑像素
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        Width = wPx / scale;
        Height = hPx / scale;
        Position = p;
    }

    private PixelRect WorkArea()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this)
                         ?? Screens.All.FirstOrDefault(s => s.IsPrimary)
                         ?? Screens.All.FirstOrDefault();
            if (screen is not null) return screen.WorkingArea;
        }
        catch { }

        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new PixelRect(vx, vy, Math.Max(1, vw), Math.Max(1, vh));
    }

    // ══════════ 交互 ══════════

    /// <summary>整窗拖动：按到关闭键就放行，其余一律交给系统的模态移动循环。</summary>
    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
            if (IsOverInteractive(e.Source)) return;

            BeginMoveDrag(e);            // 阻塞到松手
        }
        catch (Exception ex)
        {
            AppLog.Catch("message", "拖动留言悬浮窗", ex, Core.LogLevel.Debug);
        }
        finally
        {
            SavePosition();              // 松手即记（下次打开还在原地）
        }
    }

    /// <summary>按下的是不是"该自己处理"的控件（本窗口只有关闭键一颗）。</summary>
    private static bool IsOverInteractive(object? source)
    {
        var v = source as Visual;
        while (v is not null)
        {
            if (v is Button) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private void ShowClose(bool show)
    {
        if (CloseButton.IsVisible == show) return;
        CloseButton.IsVisible = show;
        CloseButton.Opacity = show ? 1 : 0;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => HideNow();

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HideNow();
    }

    private void Window_DoubleTapped(object? sender, TappedEventArgs e) => HideNow();

    private void SavePosition()
    {
        try
        {
            var pos = Position;
            var cfg = MessageConfig.Load();       // 先读回最新一份，别把别处刚改的字号/颜色整份覆盖掉
            cfg.X = pos.X;
            cfg.Y = pos.Y;
            cfg.PosVersion = MessageConfig.CurrentPosVersion;
            cfg.Save();
        }
        catch { }
    }

    // ══════════ 小工具 ══════════

    private static Color Color(string? hex, Color fallback)
        => MessageConfig.TryParseColor(hex, out var c) ? c : fallback;

    private static IBrush Brush(string? hex, Color fallback) => new SolidColorBrush(Color(hex, fallback));
}
