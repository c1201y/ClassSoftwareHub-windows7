using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;
using SdBitmap = System.Drawing.Bitmap;
// System.Drawing 的 Graphics/GraphicsUnit/RotateFlipType 与 Avalonia.Media 有类型名冲突
//（Color 等），照本文件已有的 SdBitmap 别名套路收进来，避免裸 using 撞名。
using SdGraphics = System.Drawing.Graphics;
using SdGraphicsUnit = System.Drawing.GraphicsUnit;
using SdRotateFlipType = System.Drawing.RotateFlipType;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 图片取色（对齐网页版 tools/ImageColorTool.vue ＋ gallery/imageColors.ts）。
/// 上传一张图 → 本机解码缩到最长边 120px → 中位切分法（median cut）取 8 个候选色 →
/// 打分选出「种子色」→ 夹进柔和区间 → 生成 7 档明暗变体（亮色 3 … 暗色 3）。
/// 点色块复制 hex，HSL 一格一格点也能复制。图片全程在本机处理，不上传。
///
/// ⚠️ 移植说明：WinRT 的 BitmapDecoder 换成 System.Drawing.Bitmap（GDI+）。
///     EXIF 方向（原版 RespectExifOrientation）改为手读 0x0112 标记旋转；
///     高质量缩放（原版 BitmapInterpolationMode.Fant）用 Graphics InterpolationMode
///     HighQualityBicubic 等价替代；像素仍按 BGRA 逐字节读，后续取色算法逐行照搬。
/// </summary>
public sealed partial class ImageColorToolPage : PageBase
{
    // ── 跟网页版一致的参数（改这里 = 改取色效果）───────────────
    /// <summary>缩放到的最长边（像素）：太大没必要，还拖慢取色。</summary>
    private const int MaxSide = 120;
    /// <summary>判定「不透明」的 alpha 阈值：半透明边缘像素往往是脏色，直接跳过。</summary>
    private const int AlphaThreshold = 125;
    /// <summary>中位切分的目标区域数（候选色个数）。</summary>
    private const int TargetClusters = 8;
    /// <summary>柔和化区间：亮度 25%–75%，饱和度 30%–85%。</summary>
    private const double BaseLMin = 25, BaseLMax = 75, BaseSMin = 30, BaseSMax = 85;

    /// <summary>7 个变体的亮度偏移（顺序 = 从最亮到最暗）。</summary>
    private static readonly (string Key, string Label, double Delta)[] Variants =
    {
        ("Light3", "亮色 3", 48),
        ("Light2", "亮色 2", 32),
        ("Light1", "亮色 1", 16),
        ("Base",   "基准色",  0),
        ("Dark1",  "暗色 1", -14),
        ("Dark2",  "暗色 2", -26),
        ("Dark3",  "暗色 3", -36),
    };

    private readonly List<(string Hex, FontIcon Check)> _checks = new();
    private bool _busy;

    public ImageColorToolPage() => InitializeComponent();



    // ══════════════════════════ 打开 / 拖入图片 ══════════════════════════

    private void Replace_Click(object? sender, RoutedEventArgs e) => _ = PickAsync();

    private void Clear_Click(object? sender, RoutedEventArgs e) => ResetAll();

    private void DropZone_Tapped(object? sender, TappedEventArgs e) => _ = PickAsync();

    private async Task PickAsync()
    {
        if (_busy) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            var exts = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff" };
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择图片",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new FilePickerFileType("图片文件") { Patterns = exts },
                },
            });
            if (files.Count == 0) return;
            await LoadAsync(files[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            ShowError("打开图片失败：" + ex.Message);
        }
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        // Avalonia 的 IDataObject 没有 WinUI DataView 那种"格式存在性"探测，
        // 文本/文件拖拽统一放行，真正取不到文件时在 Drop 里兜住。
        e.DragEffects = DragDropEffects.Copy;
        // 原版经 DragUIOverride 显示「松开鼠标即提取配色」提示；Avalonia 没有对应 API，
        // 视觉提示由下面的高亮描边承担。
        DropFrameAccent.IsVisible = true;
    }

    private void DropZone_DragLeave(object? sender, DragEventArgs e) =>
        DropFrameAccent.IsVisible = false;

    private async void DropZone_Drop(object? sender, DragEventArgs e)
    {
        DropFrameAccent.IsVisible = false;
        try
        {
            var path = e.Data?.GetFileNames()?.FirstOrDefault();
            if (path is not null)
                await LoadAsync(path);
        }
        catch (Exception ex)
        {
            ShowError("读取拖入的文件失败：" + ex.Message);
        }
    }

    // ══════════════════════════ 解码 + 提取 ══════════════════════════

    private sealed record DecodeResult(Bitmap? Preview, Palette? Result, int SrcW, int SrcH, int Bw, int Bh, string? Error);

    private async Task LoadAsync(string path)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // 原版 WinRT 解码器天然异步；GDI+ 是同步 API，等价做法是整个解码+取色丢后台线程。
            var r = await Task.Run(() => DecodeAndExtract(path));

            // 预览图（原图，界面自己缩放）
            if (r.Preview is { } image) Preview.Source = image;

            ShowInfo($"原图 {r.SrcW} × {r.SrcH} · 采样 {r.Bw} × {r.Bh}");

            if (r.Error is not null)
            {
                HideResults();
                ShowError(r.Error);
                return;
            }

            if (r.Result is null)
            {
                HideResults();
                ShowError("未提取到可用配色，请更换图片。");
                return;
            }

            HideError();
            RenderPalette(r.Result);
        }
        catch (Exception ex)
        {
            HideResults();
            ShowError("图片处理失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>后台解码 + 取色。返回 Error 时该文案已是最贴近用户语境的那句（原版的两条专属提示保留）。</summary>
    private static DecodeResult DecodeAndExtract(string path)
    {
        try
        {
            int srcW, srcH, bw, bh;
            byte[] bytes;
            Bitmap? preview = null;

            using (var sd = new SdBitmap(path))
            {
                ApplyExifOrientation(sd);
                srcW = sd.Width;
                srcH = sd.Height;
                if (srcW == 0 || srcH == 0)
                    return new DecodeResult(null, null, 0, 0, 0, 0, "无法读取图片尺寸，请更换格式。");

                var longest = Math.Max(srcW, srcH);
                var scale = Math.Min(1.0, MaxSide / (double)longest);
                var w = Math.Max(1, R(srcW * scale));
                var h = Math.Max(1, R(srcH * scale));

                // 原版 BitmapInterpolationMode.Fant → GDI+ 最接近的高质量模式
                using var scaled = new SdBitmap(w, h);
                using (var g = SdGraphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(sd, new System.Drawing.Rectangle(0, 0, w, h),
                                0, 0, sd.Width, sd.Height, SdGraphicsUnit.Pixel);
                }

                bw = scaled.Width;
                bh = scaled.Height;

                // LockBits 按 BGRA 逐字节读出（与原版 Bgra8 的字节序一致：B G R A）
                var data = scaled.LockBits(
                    new System.Drawing.Rectangle(0, 0, bw, bh),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    bytes = new byte[bw * bh * 4];
                    var stride = data.Stride;
                    for (var y = 0; y < bh; y++)
                        Marshal.Copy(data.Scan0 + y * stride, bytes, y * bw * 4, bw * 4);
                }
                finally
                {
                    scaled.UnlockBits(data);
                }
            }

            // 预览图独立解码（GDI+ 与 Avalonia 是两套解码器；预览失败不影响取色）
            try { preview = new Bitmap(path); } catch { preview = null; }

            var pixels = new List<Rgb>(bytes.Length / 4);
            for (var i = 0; i + 3 < bytes.Length; i += 4)
            {
                if (bytes[i + 3] < AlphaThreshold) continue;      // 跳过透明/半透明像素
                pixels.Add(new Rgb(bytes[i + 2], bytes[i + 1], bytes[i]));
            }

            if (pixels.Count == 0)
                return new DecodeResult(preview, null, srcW, srcH, bw, bh,
                    "该图片中没有可用的不透明像素（可能为全透明图片），请更换图片。");

            var palette = ExtractPalette(pixels);
            return new DecodeResult(preview, palette, srcW, srcH, bw, bh, null);
        }
        catch (Exception ex)
        {
            return new DecodeResult(null, null, 0, 0, 0, 0, "图片处理失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 手动应用 EXIF 方向标记（等价原版 <c>ExifOrientationMode.RespectExifOrientation</c>）。
    /// 手机竖拍的照片不转的话取出来的色是"躺着的"，虽然取色结果差异不大，但预览会歪。
    /// </summary>
    private static void ApplyExifOrientation(SdBitmap bmp)
    {
        try
        {
            const int PropertyTagOrientation = 0x0112;
            if (!bmp.PropertyIdList.Contains(PropertyTagOrientation)) return;
            var item = bmp.GetPropertyItem(PropertyTagOrientation);
            if (item.Value is not { Length: > 1 } raw) return;
            switch (BitConverter.ToUInt16(raw, 0))
            {
                case 2: bmp.RotateFlip(SdRotateFlipType.RotateNoneFlipX); break;
                case 3: bmp.RotateFlip(SdRotateFlipType.Rotate180FlipNone); break;
                case 4: bmp.RotateFlip(SdRotateFlipType.RotateNoneFlipY); break;
                case 5: bmp.RotateFlip(SdRotateFlipType.Rotate90FlipX); break;
                case 6: bmp.RotateFlip(SdRotateFlipType.Rotate90FlipNone); break;
                case 7: bmp.RotateFlip(SdRotateFlipType.Rotate270FlipX); break;
                case 8: bmp.RotateFlip(SdRotateFlipType.Rotate270FlipNone); break;
            }
        }
        catch { /* 读不到方向标记就按原始像素来 */ }
    }

    // ══════════════════════════ 界面 ══════════════════════════

    private void RenderPalette(Palette palette)
    {
        _checks.Clear();

        EmptyState.IsVisible = false;
        LoadedState.IsVisible = true;
        DropFrame.StrokeDashArray = null;            // 有图之后边框变实线
        DropFrameAccent.StrokeDashArray = null;

        SeedChip.Background = new SolidColorBrush(palette.SeedColor);
        SeedText.Text = $"{palette.SeedHex} · {HslText(palette.SeedHsl)}";
        SeedCard.IsVisible = true;

        SwatchHost.Children.Clear();
        foreach (var variant in palette.Variants)
            SwatchHost.Children.Add(BuildSwatch(variant));

        HslHost.Children.Clear();
        foreach (var variant in palette.Variants)
            HslHost.Children.Add(BuildHslChip(variant));

        PaletteSection.IsVisible = true;
        HslSection.IsVisible = true;
        ActionRow.IsVisible = true;
        Toast.Text = "";
    }

    /// <summary>原版由 VariableSizedWrapGrid 的 ItemWidth/ItemHeight 定格 —— WrapPanel 需要按钮自带尺寸。</summary>
    private const double SwatchCellWidth = 148;
    private const double SwatchCellHeight = 126;
    private const double HslCellWidth = 148;
    private const double HslCellHeight = 30;

    private Button BuildSwatch(Variant variant)
    {
        var block = new Border
        {
            Height = 60,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ActualThemeVariant == ThemeVariant.Dark          // 浅色底上用黑边、深色底上用白边
                ? Avalonia.Media.Color.FromArgb(0x1F, 255, 255, 255)
                : Avalonia.Media.Color.FromArgb(0x14, 0, 0, 0)),
            Background = new SolidColorBrush(variant.Color),
        };

        var check = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 20,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = false,
        };
        var blockHost = new Grid();
        blockHost.Children.Add(block);
        blockHost.Children.Add(check);

        var name = new TextBlock
        {
            Text = variant.Label,
            FontSize = 12,
            Opacity = 0.65,
        };
        var hex = new TextBlock
        {
            Text = variant.Hex,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("Consolas"),
        };

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(blockHost);
        stack.Children.Add(name);
        stack.Children.Add(hex);

        var button = new Button
        {
            Padding = new Thickness(9),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Width = SwatchCellWidth,
            Height = SwatchCellHeight,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Content = stack,
        };
        ToolTip.SetTip(button, $"单击复制 {variant.Hex}");
        button.Click += (_, _) => CopyHex(variant.Hex);

        _checks.Add((variant.Hex, check));
        return button;
    }

    private Button BuildHslChip(Variant variant)
    {
        var text = HslShort(variant.Hsl);
        var button = new Button
        {
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Width = HslCellWidth,
            Height = HslCellHeight,
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Opacity = 0.75,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        ToolTip.SetTip(button, $"单击复制 {HslText(variant.Hsl)}");
        button.Click += (_, _) => CopyValue(HslText(variant.Hsl));
        return button;
    }

    private void ResetAll()
    {
        Preview.Source = null;
        EmptyState.IsVisible = true;
        LoadedState.IsVisible = false;
        ImageInfo.Text = "";
        DropFrame.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 };
        DropFrameAccent.StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 };

        SwatchHost.Children.Clear();
        HslHost.Children.Clear();
        _checks.Clear();

        SeedCard.IsVisible = false;
        PaletteSection.IsVisible = false;
        HslSection.IsVisible = false;
        ActionRow.IsVisible = false;
        Toast.Text = "";
        HideError();
    }

    private void HideResults()
    {
        SeedCard.IsVisible = false;
        PaletteSection.IsVisible = false;
        HslSection.IsVisible = false;
        ActionRow.IsVisible = false;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;

    private void ShowInfo(string message)
    {
        HideError();
        Toast.Text = message;
    }

    private void CopyHex(string hex)
    {
        CopyValue(hex);
        foreach (var (value, check) in _checks)
            check.IsVisible = value == hex;
    }

    private async void CopyValue(string value)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is not { } clip) return;
            await clip.SetTextAsync(value);
            Toast.Text = "已复制 " + value;
        }
        catch
        {
            Toast.Text = "复制失败（剪贴板被占用）";
        }
    }

    // ══════════════════════════ 取色算法（移植 imageColors.ts）══════════════════════════

    private readonly record struct Rgb(int R, int G, int B);

    private readonly record struct Hsl(int H, int S, int L);

    private sealed record Variant(string Key, string Label, double Delta, Hsl Hsl, string Hex, Color Color);

    private sealed record Palette(
        string SeedHex, Hsl SeedHsl, Color SeedColor, IReadOnlyList<Variant> Variants);

    /// <summary>主入口：从缩放后的像素里提取柔和配色（跟网页版逐行对齐）。</summary>
    private static Palette? ExtractPalette(List<Rgb> pixels)
    {
        if (pixels.Count == 0) return null;

        var total = pixels.Count;
        var boxes = MedianCut(pixels, TargetClusters);

        // 候选色打分：频率 × 饱和度权重 × 亮度权重 ×「中等饱和度」加分
        var candidates = new List<(Rgb Rgb, Hsl Hsl, double Score)>();
        foreach (var box in boxes)
        {
            var rgb = AverageColor(box);
            var hsl = RgbToHsl(rgb.R, rgb.G, rgb.B);
            var s = hsl.S / 100.0;
            var l = hsl.L / 100.0;
            var frequency = box.Count / (double)total;

            var satWeight = s < 0.1 ? 0.15 : 1.0;                       // 灰扑扑的压到 0.15
            var lightWeight = l < 0.12 || l > 0.88 ? 0.25 : 1.0;        // 太暗/太亮的压到 0.25
            var midSatBonus = 1 + 0.6 * (1 - Math.Min(1, Math.Abs(s - 0.55) / 0.45));

            candidates.Add((rgb, hsl, frequency * satWeight * lightWeight * midSatBonus));
        }
        if (candidates.Count == 0) return null;

        var seed = candidates[0];
        foreach (var item in candidates)
            if (item.Score > seed.Score) seed = item;

        // 把种子色夹进柔和区间：亮度 25–75，饱和度 30–85
        var baseHsl = new Hsl(
            seed.Hsl.H,
            (int)Clamp(seed.Hsl.S, BaseSMin, BaseSMax),
            (int)Clamp(seed.Hsl.L, BaseLMin, BaseLMax));

        var variants = new List<Variant>();
        foreach (var (key, label, delta) in Variants)
        {
            var l = Clamp(baseHsl.L + delta, 0, 100);
            // 饱和度随亮度轻微反向调整：越亮越降饱和，越暗略微提饱和
            var s = Clamp(baseHsl.S - delta * 0.4, 0, 100);
            var hsl = new Hsl(baseHsl.H, R(s), R(l));
            var rgb = HslToRgb(hsl.H, hsl.S, hsl.L);
            variants.Add(new Variant(key, label, delta, hsl, HexOf(rgb), ToColor(rgb)));
        }

        return new Palette(
            HexOf(seed.Rgb), seed.Hsl, ToColor(seed.Rgb), variants);
    }

    /// <summary>
    /// 中位切分法：反复挑「颜色跨度最大」的区域，按跨度最大的通道排序后从中间切开，
    /// 直到切出 target 个区域（或再也切不动）。
    /// </summary>
    private static List<List<Rgb>> MedianCut(List<Rgb> pixels, int target)
    {
        var boxes = new List<List<Rgb>> { pixels };
        while (boxes.Count < target)
        {
            var pick = -1;
            var maxRange = -1;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                var range = BoxRange(boxes[i]).Range;
                if (range > maxRange) { maxRange = range; pick = i; }
            }
            if (pick < 0) break;

            var box = boxes[pick];
            boxes.RemoveAt(pick);
            var channel = BoxRange(box).Channel;
            box.Sort((a, b) => Channel(a, channel) - Channel(b, channel));
            var mid = box.Count / 2;
            boxes.Add(box.GetRange(0, mid));
            boxes.Add(box.GetRange(mid, box.Count - mid));
        }
        return boxes.Where(b => b.Count > 0).ToList();
    }

    private static (int Range, int Channel) BoxRange(List<Rgb> box)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
        foreach (var (r, g, b) in box)
        {
            if (r < rMin) rMin = r;
            if (r > rMax) rMax = r;
            if (g < gMin) gMin = g;
            if (g > gMax) gMax = g;
            if (b < bMin) bMin = b;
            if (b > bMax) bMax = b;
        }
        var rr = rMax - rMin;
        var gr = gMax - gMin;
        var br = bMax - bMin;
        var range = Math.Max(rr, Math.Max(gr, br));
        var channel = range == rr ? 0 : range == gr ? 1 : 2;
        return (range, channel);
    }

    private static int Channel(Rgb c, int index) => index switch
    {
        0 => c.R,
        1 => c.G,
        _ => c.B,
    };

    private static Rgb AverageColor(List<Rgb> box)
    {
        long r = 0, g = 0, b = 0;
        foreach (var (pr, pg, pb) in box) { r += pr; g += pg; b += pb; }
        var n = box.Count > 0 ? box.Count : 1;
        return new Rgb(R(r / (double)n), R(g / (double)n), R(b / (double)n));
    }

    /// <summary>RGB(0–255) → HSL(h 0–360, s/l 0–100)</summary>
    private static Hsl RgbToHsl(int r, int g, int b)
    {
        var rn = r / 255.0;
        var gn = g / 255.0;
        var bn = b / 255.0;
        var max = Math.Max(rn, Math.Max(gn, bn));
        var min = Math.Min(rn, Math.Min(gn, bn));
        var l = (max + min) / 2;
        double h = 0, s = 0;
        if (max != min)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == rn) h = (gn - bn) / d + (gn < bn ? 6 : 0);
            else if (max == gn) h = (bn - rn) / d + 2;
            else h = (rn - gn) / d + 4;
            h *= 60;
        }
        return new Hsl(R(h), R(s * 100), R(l * 100));
    }

    /// <summary>HSL(h 0–360, s/l 0–100) → RGB(0–255)</summary>
    private static Rgb HslToRgb(double h, double s, double l)
    {
        var hn = ((h % 360) + 360) % 360 / 360.0;
        var sn = Clamp(s, 0, 100) / 100.0;
        var ln = Clamp(l, 0, 100) / 100.0;
        if (sn == 0)
        {
            var v = R(ln * 255);
            return new Rgb(v, v, v);
        }
        var q = ln < 0.5 ? ln * (1 + sn) : ln + sn - ln * sn;
        var p = 2 * ln - q;

        double Hue(double t)
        {
            var tt = t;
            if (tt < 0) tt += 1;
            if (tt > 1) tt -= 1;
            if (tt < 1.0 / 6) return p + (q - p) * 6 * tt;
            if (tt < 1.0 / 2) return q;
            if (tt < 2.0 / 3) return p + (q - p) * (2.0 / 3 - tt) * 6;
            return p;
        }

        return new Rgb(
            R(Hue(hn + 1.0 / 3) * 255),
            R(Hue(hn) * 255),
            R(Hue(hn - 1.0 / 3) * 255));
    }

    // ── 小工具 ──────────────────────────────────────────────

    /// <summary>JS 的 Math.round（C# 的 Math.Round 是银行家舍入，对不上）。</summary>
    private static int R(double value) => (int)Math.Floor(value + 0.5);

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));

    private static string HexOf(Rgb rgb) =>
        $"#{Hex(rgb.R)}{Hex(rgb.G)}{Hex(rgb.B)}";

    private static string Hex(int value) =>
        Math.Clamp(value, 0, 255).ToString("X2");

    private static Color ToColor(Rgb rgb) => Color.FromArgb(
        255, (byte)Math.Clamp(rgb.R, 0, 255), (byte)Math.Clamp(rgb.G, 0, 255), (byte)Math.Clamp(rgb.B, 0, 255));

    private static string HslText(Hsl hsl) => $"hsl({hsl.H}, {hsl.S}%, {hsl.L}%)";

    private static string HslShort(Hsl hsl) => $"{hsl.H}° {hsl.S}% {hsl.L}%";
}
