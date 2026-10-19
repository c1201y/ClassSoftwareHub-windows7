using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 把快捷入口那几个 SVG 图标按**当前主题色**染出来。
///
/// ⚠️ 为什么不是直接原样加载：那三个 SVG 是单色图标（GitHub/QQ 是填充、主页是描边），
/// 原色是黑 / <c>#141520</c>，深色界面上直接糊成一片看不见。所以这里把 SVG 里的
/// <c>__C__</c> 占位符替换成主题文字色（跟同页 FontIcon 用的 <c>TextFillColorPrimaryBrush</c>
/// 同一套），浅色/深色各自一套，结果按「资源名 + 颜色」缓存，主题切换时换一张即可。
///
/// <para>⚠️ Win7 移植说明（2026-10-18 自上游 1.2.0 移植）：Avalonia 核心没有 SVG 解码器
/// （SvgImageSource 是 WinUI 的；SkiaSharp 的 SKSvg 在扩展包里，本移植版不引第三方包）。
/// 所以渲染端改成两条路：**首选**把替换过颜色的 SVG 文本用内嵌 SkiaSharp 路径解析
/// （若运行时有 SkiaSharp.Svg 扩展则用之，反射探测），**兜底**画一个首字色块位图，
/// 与软件卡片「图标加载失败 → 首字色块」同一套观感 —— 视觉不断档，比黑块好。</para>
/// </summary>
public static class QuickLinkIconLoader
{
    private static readonly object Gate = new();
    private static readonly System.Collections.Generic.Dictionary<string, IImage> Cache = new();

    /// <summary>位图边长（与上游 FontIcon 16/20 的显示尺寸 ×2 留出 DPI 余量）。</summary>
    private const double Side = 32;

    /// <summary>反射缓存的 SkiaSharp.Svg 解码入口（若运行时程序集里有就用真矢量，没有就兜底）。</summary>
    private static System.Reflection.MethodInfo? _svgDecodeMethod;

    /// <summary>取染好色的图标源；资源缺失或染不出来返回 <c>null</c>（调用方退回 FontIcon）。</summary>
    public static IImage? GetIcon(string assetName, Color color)
    {
        var hex = "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        var key = assetName + "|" + hex;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = "ClassSoftwareHub.Desktop.Assets.QuickLinks." + assetName;
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream is null) return null;

            string svg;
            using (var reader = new StreamReader(stream))
                svg = reader.ReadToEnd();

            svg = svg.Replace("__C__", hex);

            // 落盘一份染好色的 SVG：给"打开所在目录/复制"这类后续功能留原始素材（与上游一致）
            var dir = Path.Combine(ShellConfig.CachedContentDir, "quickicons");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, key.Replace("|", "_") + ".svg");
            File.WriteAllText(file, svg);

            // ⚠️ Win7 端没有 SkiaSharp.Svg 扩展时 Render 返回 null —— 这里**直接放弃**，
            //    让调用方退回模板里的 FontIcon（系统字体的正经图标）。不用"首字兜底"：
            //    那会把「项目仓库」的仓库图标换成一个字母 G，比 FontIcon 难看且认不出来。
            //    （2026-10-18 移植时的取舍，与上游 WinUI 的 SvgImageSource 情形不同。）
            var rendered = Render(svg);
            if (rendered is null) return null;
            lock (Gate) Cache[key] = rendered;
            return rendered;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 优先用 SkiaSharp.Svg（反射探测，避免硬依赖扩展包）把 SVG 画成位图；
    /// 没有该扩展就返回 null，由调用方走首字兜底。
    /// </summary>
    private static IImage? Render(string svgText)
    {
        try
        {
            if (_svgDecodeMethod is null)
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var a in asm)
                {
                    if (!a.GetName().Name.StartsWith("SkiaSharp", StringComparison.OrdinalIgnoreCase)) continue;
                    var t = a.GetType("SkiaSharp.SKSvg");
                    if (t is null) continue;
                    _svgDecodeMethod = t.GetMethod("Open", new[] { typeof(SkiaSharp.SKStream) });
                    break;
                }
                // 探测结果（哪怕 null）记下来，别每次都扫
            }
            if (_svgDecodeMethod is null) return null;

            using var data = SkiaSharp.SKData.CreateCopy(System.Text.Encoding.UTF8.GetBytes(svgText));
            using var skStream = new SkiaSharp.SKManagedStream(data.AsStream());
            var skSvg = Activator.CreateInstance(_svgDecodeMethod.DeclaringType!);
            if (skSvg is null) return null;
            if (_svgDecodeMethod.Invoke(skSvg, new object[] { skStream }) is not SkiaSharp.SKPicture pic
                || pic is null) return null;

            var w = pic.CullRect.Width;
            var h = pic.CullRect.Height;
            if (w <= 0 || h <= 0) return null;

            var scale = Math.Min(Side / w, Side / h);
            var pw = (int)Math.Ceiling(w * scale);
            var ph = (int)Math.Ceiling(h * scale);
            if (pw <= 0 || ph <= 0) return null;

            using var bmp = new SkiaSharp.SKBitmap(pw, ph);
            using (var canvas = new SkiaSharp.SKCanvas(bmp))
            using (var paint = new SkiaSharp.SKPaint { IsAntialias = true })
            {
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                canvas.Scale((float)scale);
                canvas.DrawPicture(pic, paint);
            }

            using var skImage = SkiaSharp.SKImage.FromBitmap(bmp);
            using var encoded = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var png = encoded.AsStream();
            return new Bitmap(png);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>兜底：资源名首字（去掉扩展名）画成主题色色块位图。</summary>
    private static IImage? FallbackLetter(string assetName, Color color)
    {
        try
        {
            var letter = Path.GetFileNameWithoutExtension(assetName);
            letter = letter.Length > 0 ? letter.Substring(0, 1).ToUpperInvariant() : "?";

            var rtb = new RenderTargetBitmap(new PixelSize((int)Side, (int)Side), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                var bg = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
                ctx.FillRectangle(bg, new Rect(0, 0, Side, Side), (float)(Side / 6));
                var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
                var ft = new FormattedText(
                    letter,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    Side * 0.62,
                    new SolidColorBrush(color));
                var size = new Size(ft.Width, ft.Height);
                ctx.DrawText(ft, new Point((Side - size.Width) / 2, (Side - size.Height) / 2));
            }
            return rtb;
        }
        catch
        {
            return null;
        }
    }
}
