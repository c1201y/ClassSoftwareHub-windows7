using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 磨砂底色：把桌面壁纸缩到极小尺寸后重采样回一张低分辨率位图，并在生成时混入主题色，
/// 作为窗口的**不透明**底色。供系统材质不可用的环境使用：Win7（无 DWM 材质、无透明窗口），
/// 以及远程会话 / 虚拟显卡（DWM 会整体关掉 Mica 与亚克力）。
///
/// <para>
/// 性能约定：位图只在「壁纸文件或深浅色变化」时生成一次，生成成本为两次 160px 以下的重采样；
/// 运行期没有计时器、不抓屏、不使用 <c>Effect</c>、不逐帧重算 —— 窗口底色就是这张小位图的一次绘制。
/// 模糊强度由第一遍缩到多小决定（<see cref="TinyWidth"/>），与窗口尺寸无关，因此拖动/缩放窗口
/// 不需要重建。底色按窗口拉伸（<c>UniformToFill</c>），不做屏幕坐标对齐：模糊后已看不出偏移。
/// </para>
/// </summary>
internal static class FrostedGlass
{
    private const uint SpiGetDeskWallpaper = 0x0073;
    private const int WallpaperBuffer = 1024;

    /// <summary>第一遍缩到多小。缩得越小模糊越强，这是零成本模糊的来源。</summary>
    private const int TinyWidth = 24;

    /// <summary>成品位图宽度。再大也看不出差别，只会增加每帧采样面积。</summary>
    private const int SmallWidth = 160;

    /// <summary>主题色罩的不透明度。壁纸已模糊，九成左右即可压住图案又保留材质感。</summary>
    private const byte TintAlpha = 0xE0;

    private static readonly RenderOptions Smooth = new()
    {
        BitmapInterpolationMode = BitmapInterpolationMode.HighQuality
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool SystemParametersInfoW(uint action, uint param, StringBuilder buffer, uint winIni);

    // 位图很小，缓存条数有上限即可；淘汰时直接丢弃引用，由 GC 回收（不 Dispose，避免窗口仍持有旧画笔时踩到已释放位图）。
    private static readonly Dictionary<string, ImageBrush> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private const int CacheLimit = 4;

    /// <summary>按当前深浅色取磨砂底色；壁纸不可用（纯色桌面、文件丢失）返回 null。</summary>
    public static IBrush? TryCreate(bool dark)
    {
        var path = WallpaperPath();
        if (path.Length == 0) return null;

        string key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{(dark ? "dark" : "light")}";
        }
        catch { return null; }

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }

        var brush = Build(path, dark);
        if (brush is null) return null;

        CachePut(key, brush);
        return brush;
    }

    /// <summary>
    /// 异步取磨砂底色：把最耗时的壁纸文件解码放到后台线程，重采样与画刷创建回到 UI 线程，
    /// 首帧由调用方先用占位底色，完成后通过 <paramref name="onReady"/> 在 UI 线程替换并刷新。
    /// 缓存命中或生成失败同样经 UI 线程回调，调用方无需关心线程切换。
    /// </summary>
    /// <remarks>
    /// ⚠️ RenderTargetBitmap 依赖平台渲染接口，Avalonia 不保证其 DrawingContext 可跨线程使用，
    ///    ImageBrush 与 Window.Background 更必须在 UI 线程创建/赋值；故这里只把
    ///    new Bitmap(path)（4K 壁纸解码，真正耗时的部分）放进 Task.Run，重采样仍留在 UI 线程。
    /// </remarks>
    public static void TryCreateAsync(bool dark, Action<IBrush?> onReady)
    {
        void Post(IBrush? brush) => Dispatcher.UIThread.Post(() => onReady(brush));

        _ = Task.Run(() =>
        {
            string key = "";
            Bitmap? decoded = null;

            try
            {
                var path = WallpaperPath();
                if (path.Length == 0) { Post(null); return; }

                var info = new FileInfo(path);
                if (!info.Exists) { Post(null); return; }
                key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{(dark ? "dark" : "light")}";

                lock (Gate)
                {
                    if (Cache.TryGetValue(key, out var hit)) { Post(hit); return; }
                }

                decoded = new Bitmap(path);   // 后台线程只做文件解码（最耗时）
            }
            catch
            {
                decoded?.Dispose();
                Post(null);
                return;
            }

            // 重采样与画刷创建放回 UI 线程，生成的成品进缓存后回调。
            Dispatcher.UIThread.Post(() =>
            {
                IBrush? brush = null;
                try { brush = BuildCore(decoded!, dark); }
                catch { }
                finally { decoded!.Dispose(); }

                if (brush is ImageBrush image && key.Length > 0) CachePut(key, image);
                onReady(brush);
            });
        });
    }

    private static void CachePut(string key, ImageBrush brush)
    {
        lock (Gate)
        {
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[key] = brush;
        }
    }

    private static ImageBrush? Build(string path, bool dark)
    {
        try
        {
            using var src = new Bitmap(path);
            return BuildCore(src, dark);
        }
        catch { return null; }
    }

    /// <summary>重采样 + 主题色罩 + 生成画刷；须在 UI 线程调用。</summary>
    private static ImageBrush? BuildCore(Bitmap src, bool dark)
    {
        try
        {
            int w = src.PixelSize.Width;
            int h = src.PixelSize.Height;
            if (w <= 0 || h <= 0) return null;

            int tinyH = Math.Max(1, (int)Math.Round(TinyWidth * h / (double)w));
            using var tiny = new RenderTargetBitmap(new PixelSize(TinyWidth, tinyH), new Vector(96, 96));
            using (var ctx = tiny.CreateDrawingContext())
            {
                using (ctx.PushRenderOptions(Smooth))
                    ctx.DrawImage(src, new Rect(0, 0, w, h), new Rect(0, 0, TinyWidth, tinyH));
            }

            int smallH = Math.Max(1, (int)Math.Round(SmallWidth * h / (double)w));
            var target = new RenderTargetBitmap(new PixelSize(SmallWidth, smallH), new Vector(96, 96));
            using (var ctx = target.CreateDrawingContext())
            {
                using (ctx.PushRenderOptions(Smooth))
                {
                    // 放大这一步的插值把 24px 的小块抹成柔和的雾面
                    ctx.DrawImage(tiny, new Rect(0, 0, TinyWidth, tinyH), new Rect(0, 0, SmallWidth, smallH));
                    ctx.FillRectangle(new SolidColorBrush(dark
                        ? Color.FromArgb(TintAlpha, 32, 32, 32)
                        : Color.FromArgb(TintAlpha, 243, 243, 243)), new Rect(0, 0, SmallWidth, smallH));
                }
            }

            return new ImageBrush(target) { Stretch = Stretch.UniformToFill };
        }
        catch { return null; }
    }

    private static string WallpaperPath()
    {
        try
        {
            var buffer = new StringBuilder(WallpaperBuffer);
            if (!SystemParametersInfoW(SpiGetDeskWallpaper, WallpaperBuffer, buffer, 0)) return "";
            return buffer.ToString().Trim();
        }
        catch { return ""; }
    }
}
