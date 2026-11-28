using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 自定义背景图（2026-11-27 用户需求）：用一张用户选的图片**顶替系统亚克力**，
/// 铺满窗口底层，可选模糊与暗化，保证前景文字读得清。
///
/// <para>
/// ── 为什么不做成"系统材质" ────────────────────────────────
/// DWM 只能模糊**桌面壁纸**，没有任何 API 让它去模糊一张自定义图片（除非改系统壁纸，
/// 那是绝不做的事）。所以这里走"窗口自己画"：关掉 accent / 系统背板，
/// 把图片画进窗口最底层的面板。
/// </para>
///
/// <para>
/// ── 模糊怎么做的（关键取舍）──────────────────────────────
/// 不跑逐像素卷积，也不挂实时 <c>BlurEffect</c>（低配 + Win7 软件渲染会掉帧）。
/// 做法是**先缩到很小再放大**：Avalonia 用双线性插值放大本身就在糊，
/// 缩小倍数 = 模糊强度，观感和"高斯模糊"近似，代价只有一次缩放开销，
/// 结果按（路径, 修改时间, 模糊, 暗化, 尺寸, 深浅色）缓存，移动窗口不会重算。
/// </para>
///
/// <para>
/// ── 暗化 ─────────────────────────────────────────────────
/// 缩小时顺便在图上盖一层黑色（透明度 = 暗化值），和图片进同一个位图，
/// 这样任何窗口只要把 Background 换成它就行，不用再叠第二个控件。
/// </para>
/// </summary>
public static class BackdropImage
{
    /// <summary>推荐模糊强度（UI 上要标出来，用户点一下能回到这个值）。</summary>
    public const double RecommendedBlur = 16;

    /// <summary>推荐暗化比例（0~0.8）。</summary>
    public const double RecommendedDim = 0.25;

    /// <summary>模糊滑块范围。</summary>
    public const double MaxBlur = 30;

    /// <summary>暗化滑块范围。</summary>
    public const double MaxDim = 0.8;

    private const int CacheLimit = 8;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, IBrush> Cache = new();

    /// <summary>当前是不是"图片背景"模式。</summary>
    public static bool IsImageMode =>
        string.Equals(App.Settings.Current.Backdrop, "image", StringComparison.OrdinalIgnoreCase);

    /// <summary>按深浅色挑图：开了"深浅各一张"且深色图存在就用深色那张。</summary>
    public static string PickPath(bool dark)
    {
        var s = App.Settings.Current;
        if (s.BackdropImageThemePair)
        {
            var pair = dark ? s.BackdropImageDark : s.BackdropImage;
            if (!string.IsNullOrWhiteSpace(pair) && File.Exists(pair)) return pair;
        }
        return s.BackdropImage ?? "";
    }

    /// <summary>
    /// 给一个面板上背景图。返回 false = 没图/读不到（调用方自己退回纯色，别留白）。
    /// </summary>
    public static bool ApplyTo(Panel? root, Window win, bool? darkOverride = null)
    {
        if (root is null) return false;
        try
        {
            var dark = darkOverride ?? (win.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
            var brush = BuildBrush(win, dark);
            if (brush is null) return false;
            root.Background = brush;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info("backdrop", "⚠ 应用背景图失败：" + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>浮窗要不要跟着用同一张图（设置里那个开关）。</summary>
    public static void ApplyToFloating(Window win, Panel? root, bool? darkOverride = null)
    {
        if (!App.Settings.Current.BackdropImageOnFloating) return;
        if (!IsImageMode) return;
        ApplyTo(root, win, darkOverride);
    }

    /// <summary>设置页换了图 / 改了模糊暗化：所有窗口刷一遍。</summary>
    public static void Invalidate()
    {
        lock (Gate) Cache.Clear();
    }

    private static IBrush? BuildBrush(Window win, bool dark)
    {
        var path = PickPath(dark);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        var s = App.Settings.Current;
        var blur = Math.Clamp(s.BackdropImageBlur, 0, MaxBlur);
        var dim = Math.Clamp(s.BackdropImageDim, 0, MaxDim);
        var stretch = (s.BackdropImageStretch ?? "cover").Trim().ToLowerInvariant();

        var target = TargetSize(win);
        // 缩小倍数：模糊 0 → 原尺寸；30 → 约 1/16（再小就成色块了）
        var factor = 1 + blur / 2.0;
        var smallW = Math.Max(16, (int)Math.Round(target.Width / factor));
        var smallH = Math.Max(16, (int)Math.Round(target.Height / factor));

        var stamp = 0L;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }

        var key = path + "|" + stamp + "|" + blur.ToString("0.0") + "|" + dim.ToString("0.00")
                  + "|" + stretch + "|" + smallW + "x" + smallH;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }

        var built = Build(path, smallW, smallH, dim, stretch);
        if (built is null) return null;

        lock (Gate)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();     // 简单粗暴：换图/改参数时全清，缓存本来就小
            Cache[key] = built;
        }
        return built;
    }

    private static IBrush? Build(string path, int w, int h, double dim, string stretch)
    {
        try
        {
            using var src = new Bitmap(path);

            // 源图按"铺满"裁一块出来，避免先把整张 4K 图放大再缩（白费 CPU）
            var srcRect = CoverRect(src.PixelSize, new PixelSize(w, h));
            var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawImage(src, srcRect, new Rect(0, 0, w, h));
                if (dim > 0.001)
                    ctx.FillRectangle(new SolidColorBrush(Colors.Black, dim), new Rect(0, 0, w, h));
            }

            var mode = stretch switch
            {
                "contain" => Stretch.Uniform,
                "fill" => Stretch.Fill,
                "tile" => Stretch.None,
                _ => Stretch.UniformToFill,
            };

            var brush = new ImageBrush(rtb)
            {
                Stretch = mode,
                TileMode = stretch == "tile" ? TileMode.Tile : TileMode.None,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
                // tile 模式下按原始像素铺，别被拉伸成两格
                DestinationRect = stretch == "tile"
                    ? new RelativeRect(0, 0, w, h, RelativeUnit.Absolute)
                    : RelativeRect.Fill,
            };
            return brush;   // 直接给可变画刷：ToImmutable 会对 RenderTargetBitmap 做一次克隆，某些图会在这儿炸
        }
        catch (Exception ex)
        {
            AppLog.Info("backdrop", "⚠ 生成背景图失败：" + ex.GetType().Name + ": " + ex.Message + "｜图=" + path + "｜缩小=" + w + "x" + h);
            return null;
        }
    }

    /// <summary>把源图裁成目标比例（等价 CSS 的 object-fit: cover）。</summary>
    private static Rect CoverRect(PixelSize src, PixelSize dst)
    {
        if (src.Width <= 0 || src.Height <= 0) return new Rect(0, 0, 1, 1);
        var srcRatio = (double)src.Width / src.Height;
        var dstRatio = (double)dst.Width / Math.Max(1, dst.Height);

        if (srcRatio > dstRatio)
        {
            // 源更宽 → 左右各裁一块
            var width = src.Height * dstRatio;
            return new Rect((src.Width - width) / 2, 0, width, src.Height);
        }
        else
        {
            var height = src.Width / dstRatio;
            return new Rect(0, (src.Height - height) / 2, src.Width, height);
        }
    }

    private static PixelSize TargetSize(Window win)
    {
        try
        {
            var scale = win.RenderScaling > 0 ? win.RenderScaling : 1.0;
            var w = (int)Math.Round(win.ClientSize.Width * scale);
            var h = (int)Math.Round(win.ClientSize.Height * scale);
            if (w >= 64 && h >= 64) return new PixelSize(w, h);
        }
        catch { }
        return new PixelSize(1600, 900);
    }
}
