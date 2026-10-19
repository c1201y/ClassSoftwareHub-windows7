using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using ClassSoftwareHub.Desktop.Platform;
using Ico = ClassSoftwareHub.Desktop.Platform.NativeMethodsIcons;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 从一个文件取出能直接在界面上显示的图标。
/// 给音量合成器（一行一个进程、要显示它自己的图标）和「应用卸载」页用。
///
/// 走 Win32（<c>SHGetFileInfo</c> 拿 HICON → 复制成 32bpp ARGB → 拼成 BMP/位图），
/// 不引 System.Drawing。提取结果按路径缓存，同一个进程反复刷新时不再重复掏图标。
///
/// ⚠️ 移植说明：原版把像素写进 WinRT 的 <c>WriteableBitmap.PixelBuffer</c>；
///    Avalonia 的等价物是 <c>Avalonia.Media.Imaging.Bitmap(Stream)</c>（解码 BMP 流），
///    所以这里手搓一段 32bpp BMP（跟 Services/ScreenCapture 同一套 54 字节头写法）再交给它。
///
/// <para>2026-10-18 自上游 1.2.0 移植：
/// ① 拆成两条路 —— exe/dll 走 Win32 提取，png/jpg 等图片（商店应用图标）直接按文件解码；
/// ② 加 <see cref="Prewarm"/> 后台预热（「应用卸载」页几百个应用，冷路径上几十毫秒会让列表一滚就迟滞）。</para>
/// </summary>
public static class AppIconService
{
    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new();
    private static readonly ConcurrentDictionary<string, Pixels> _pixels = new();

    /// <summary>提取出来的原始像素（32bpp BGRA、行序自上而下）。</summary>
    private sealed record Pixels(int W, int H, byte[] Bgra);

    /// <summary>
    /// 后台预热：先把**慢的那一半**（读 exe 图标资源 → 拷成像素）跑掉。可在任意线程调。
    /// ⚠️ 预热只是优化，不是正确性依赖 —— 没预热到的话 <see cref="Get"/> 自己同步补上。
    /// </summary>
    public static void Prewarm(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (IsImageFile(path)) return;   // png 那条本来就只是读文件
        _pixels.GetOrAdd(path, p => ExtractPixels(p) ?? new Pixels(0, 0, Array.Empty<byte>()));
    }

    /// <summary>拿某个文件的图标；拿不到（路径空 / 文件没了 / 提取失败）返回 null，调用方自会显示占位。</summary>
    public static Bitmap? Get(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return _cache.GetOrAdd(exePath, path =>
        {
            if (IsImageFile(path)) return FromImageFile(path);

            var px = _pixels.GetOrAdd(path, p => ExtractPixels(p) ?? new Pixels(0, 0, Array.Empty<byte>()));
            return px.W <= 0 || px.H <= 0 ? null : DecodeBgra(px.Bgra, px.W, px.H);
        });
    }

    private static bool IsImageFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";

    /// <summary>图片文件（商店应用的 logo png）直接交给 Avalonia 的 Bitmap 解码。</summary>
    private static Bitmap? FromImageFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        }
        catch
        {
            return null;
        }
    }

    private static Pixels? ExtractPixels(string exePath)
    {
        var hIcon = IntPtr.Zero;
        try
        {
            var shfi = new Ico.SHFILEINFO();
            // SHGFI_ICON | SHGFI_LARGEICON：拿 32×32 大图标。拿不到 hIcon 就放弃（不抛异常）。
            var res = Ico.SHGetFileInfo(exePath, 0, ref shfi, (uint)Marshal.SizeOf<Ico.SHFILEINFO>(),
                                        Ico.SHGFI_ICON | Ico.SHGFI_LARGEICON);
            // ⚠️ SHGetFileInfo 返回的是 DWORD_PTR，.NET 映射成 IntPtr，比较用 IntPtr.Zero（不是 0）。
            if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
            hIcon = shfi.hIcon;

            if (!Ico.GetIconInfo(hIcon, out var ii)) return null;
            try
            {
                if (ii.hbmColor == IntPtr.Zero) return null;   // 罕见：只有掩码没有彩色位图

                if (Ico.GetObject(ii.hbmColor, Marshal.SizeOf<Ico.BITMAP>(), out var bmp) == 0) return null;
                int w = bmp.bmWidth, h = bmp.bmHeight;
                if (w <= 0 || h <= 0) return null;

                // 图标位图是 32bpp DIB section，直接按 BGRA 读出来。
                // ⚠️ 负高度 = 行序自上而下，免去自己翻一遍（Windows 位图默认自下而上）。
                var pixels = new byte[w * h * 4];
                var bih = new Ico.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Ico.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,          // BI_RGB
                };

                var hdc = NativeMethods.GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return null;
                try
                {
                    if (Ico.GetDIBits(hdc, ii.hbmColor, 0, (uint)h, pixels, ref bih, Ico.DIB_RGB_COLORS) == 0)
                        return null;
                }
                finally
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
                }

                return new Pixels(w, h, pixels);
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
            }
        }
        catch
        {
            return null;                       // 任何一步失败都当「没图标」，别把浮窗带崩
        }
        finally
        {
            if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// 裸 32bpp BGRA（行自上而下）→ 可显示的位图：
    /// 手搓一段 54 字节头的 BMP（行序翻成自下而上），再让 Avalonia 解码。
    /// </summary>
    private static Bitmap? DecodeBgra(byte[] bgra, int w, int h)
    {
        try
        {
            var stride = w * 4;
            const int headerSize = 14 + 40;
            var size = headerSize + stride * h;

            using var ms = new MemoryStream(size);
            using var bw = new BinaryWriter(ms);

            bw.Write((byte)'B');
            bw.Write((byte)'M');
            bw.Write(size);                              // 文件总大小
            bw.Write(0);                                 // 保留
            bw.Write(headerSize);                        // 像素数据偏移

            bw.Write(40);                                // 信息头大小
            bw.Write(w);
            bw.Write(h);                                 // 正数 = 行自下而上
            bw.Write((short)1);                          // planes
            bw.Write((short)32);                         // bpp
            bw.Write(0);                                 // BI_RGB
            bw.Write(stride * h);                        // 像素数据大小
            bw.Write(2835);                              // 水平分辨率（72dpi）
            bw.Write(2835);                              // 垂直分辨率
            bw.Write(0);                                 // 调色板
            bw.Write(0);                                 // 重要颜色数

            for (var row = h - 1; row >= 0; row--)       // BMP 行自下而上
                bw.Write(bgra, row * stride, stride);

            bw.Flush();

            ms.Position = 0;
            // 原版走 WinRT 的 WriteableBitmap.PixelBuffer；Avalonia 等价物是 Bitmap(Stream)，
            // 解码是立即完成的，用完可以直接把流关掉。
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
