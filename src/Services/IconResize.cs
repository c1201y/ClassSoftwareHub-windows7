using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 投稿选的图标，在上传前先缩小、重编码（对应网页端 <c>src/gallery/iconResize.ts</c>）。
///
/// 为什么非缩不可：图标在站内只以 44 / 72 px 的磁贴出现，但用户随手选的多半是相机原图或几百 KB 的 PNG。
/// 原图会被原样存进 OSS，再被 <c>/api/icon</c> 原样转发给每一个访客 —— 换不来任何清晰度，
/// 只让卡片白等、让中继白出一次流量。所以进桶之前先压到 <see cref="MaxEdge"/>。
///
/// 为什么是 128 而不是 64：多留一倍余量，方便维护者后续再本地化一次。
///
/// 降级策略：压缩只是优化，绝不能变成上传的门槛 —— 解码不了、编不出来、
/// 压完反而更大，任何一步出问题都返回 null，让调用方直接用原文件。
///
/// ⚠️ 移植说明：上游用 WinUI 的 BitmapDecoder/BitmapEncoder（Windows.Graphics.Imaging），
/// 本仓库没有这套 API，改用 Avalonia 的 Bitmap 解码 + RenderTargetBitmap（Skia）缩放重编码，
/// 语义与上游一致（Fant 高质量插值 → Skia 的默认高质量采样）。
/// </summary>
public static class IconResize
{
    /// <summary>缩放后的最长边（像素）。</summary>
    public const int MaxEdge = 128;

    /// <summary>原图已经这么小就不值得重新编码了（再编一次很可能反而变大）。</summary>
    private const long SkipBelowBytes = 48 * 1024;

    /// <summary>
    /// 把图片缩到 <see cref="MaxEdge"/> 以内并重编码成 PNG，写到临时文件。
    /// 解码/编码在后台线程做（Skia 位图操作别占 UI 线程）。
    /// </summary>
    /// <returns>
    /// 压缩后的**临时文件路径**（调用方用完可删）；不需要或压不动时返回 <c>null</c>，
    /// 此时请直接用原文件上传。
    /// </returns>
    public static Task<string?> ShrinkToTempAsync(string sourcePath, CancellationToken ct = default)
        => Task.Run(() => Shrink(sourcePath), ct);

    private static string? Shrink(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            var sourceBytes = new FileInfo(sourcePath).Length;
            if (sourceBytes <= SkipBelowBytes) return null;

            Bitmap decoded;
            using (var input = File.OpenRead(sourcePath))
            {
                decoded = new Bitmap(input);   // 不是图片 / 格式不认 → 这里直接抛
            }

            var width = decoded.PixelSize.Width;
            var height = decoded.PixelSize.Height;
            if (width == 0 || height == 0)
            {
                decoded.Dispose();
                return null;
            }

            var scale = Math.Min(1.0, MaxEdge / (double)Math.Max(width, height));
            var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

            var tempPath = Path.Combine(Path.GetTempPath(), "csh-icon-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                // RenderTargetBitmap 默认透明底：图标透明通道原样保留（与上游 Bgra8+Premultiplied 一致）
                using var target = new RenderTargetBitmap(new PixelSize(targetWidth, targetHeight));
                using (var ctx = target.CreateDrawingContext())
                {
                    ctx.DrawImage(decoded, new Rect(0, 0, width, height), new Rect(0, 0, targetWidth, targetHeight));
                }

                using (var output = File.Create(tempPath))
                {
                    target.Save(output);
                }
            }
            finally
            {
                decoded.Dispose();
            }

            // 压完反而更大（小尺寸高细节图会这样）就别折腾了
            if (new FileInfo(tempPath).Length >= sourceBytes)
            {
                TryDelete(tempPath);
                return null;
            }
            return tempPath;
        }
        catch
        {
            // 任何一步失败（不是图片、格式不认、没有编解码器）都退回原文件
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就留给系统清 */ }
    }
}
