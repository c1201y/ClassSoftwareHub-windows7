using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// <see cref="NativeMethods"/> 的**补充**：从 exe 提取图标（<c>Services.AppIconService</c> 专用）。
///
/// ⚠️ 为什么另开文件：<see cref="NativeMethods"/> / <see cref="NativeMethodsEx"/> 都不是 partial
///    且移植期要求不修改已就绪的 Platform 文件，所以按伴生类的惯例新开一个同命名空间的类。
///    调用方一律走 <c>Platform.NativeMethods*</c>。
///
/// ⚠️ 通用约定：<c>UpdateLayeredWindow</c> 那条路没用上，这里只覆盖
///    <c>SHGetFileInfo → HICON → 取位图 → 读像素</c> 这一条（Win7 完整可用）。
///
/// 每个导出仍按 <see cref="NativeMethods"/> 的惯例，在注释里标**最低可用系统**。
/// </summary>
internal static class NativeMethodsIcons
{
    // ══════════════════════════════════════════════════════════════════
    //  shell32 —— 取文件/文件夹图标
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。拿 exe 的图标句柄（<c>SHGFI_ICON</c>）。用完必须 <c>DestroyIcon</c>。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
                                                ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    internal const uint SHGFI_ICON = 0x000000100;
    internal const uint SHGFI_LARGEICON = 0x000000000;
    internal const uint SHGFI_SMALLICON = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    // ══════════════════════════════════════════════════════════════════
    //  user32 / gdi32 —— 从 HICON 抠出 32bpp ARGB 像素
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最低 Win2000。把 HICON 拆成掩码位图 + 彩色位图（两个都要自己 DeleteObject）。</summary>
    [DllImport("user32.dll")]
    internal static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    /// <summary>最低 Win2000。读 GDI 对象的基本属性（这里用来问位图的宽高）。W 版取 BITMAP。</summary>
    [DllImport("gdi32.dll")]
    internal static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    /// <summary>
    /// 最低 Win2000。把位图内容按指定格式读成裸像素（这里要 32bpp 的 BGRA）。
    /// ⚠️ 需要一块 DC；位图的 <c>biHeight</c> 给**负数**表示行序自上而下，省得自己翻。
    /// </summary>
    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
                                         byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint uUsage);

    internal const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
