using System;
using System.Runtime.InteropServices;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 「幽灵文件」清理用到的原生接口（kernel32 的目录枚举 / 删除，shell32 的回收站与刷新）。
///
/// <para>
/// 为什么单独开一个文件：<c>Platform/NativeMethods.cs</c> 有别的功能在并发修改，
/// 这里的方法又都是这一块专用的（含 <c>WIN32_FIND_DATAW</c> 这种数据结构），放一起更好认。
/// 移植契约允许在 <c>Platform/</c> 下**新增**文件，故独立成 <c>GhostNative</c>。
/// </para>
///
/// <para>
/// ⛔ 不要改成 <c>LibraryImport</c>：net6 的源生成器不支持，工程也锁在 <c>LangVersion 10</c>。
/// </para>
/// </summary>
internal static class GhostNative
{
    // ── 常量 ────────────────────────────────────────────────

    /// <summary><c>FindFirstFileW</c> 失败时的返回值。</summary>
    public static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary><c>GetFileAttributesW</c> 失败时的返回值。</summary>
    public const uint InvalidFileAttributes = 0xFFFFFFFF;

    public const uint FileAttributeReadOnly = 0x0001;
    public const uint FileAttributeDirectory = 0x0010;
    public const uint FileAttributeNormal = 0x0080;
    public const uint FileAttributeReparsePoint = 0x0400;

    // SHChangeNotify
    public const int ShcneUpdateDir = 0x1000;
    public const uint ShcnfPathW = 0x0005;
    public const uint ShcnfFlush = 0x1000;

    // SHFileOperation
    /// <summary><c>FO_DELETE</c>：删除操作。</summary>
    public const uint FoDelete = 0x0003;

    /// <summary><c>FOF_ALLOWUNDO</c>：进回收站（这一位就是「可还原」的全部实现）。</summary>
    public const ushort FofAllowUndo = 0x0040;

    /// <summary><c>FOF_SILENT</c>：不显示进度框。</summary>
    public const ushort FofSilent = 0x0004;

    /// <summary><c>FOF_NOCONFIRMATION</c>：不问「确实要删除吗」（确认由我们自己的对话框负责）。</summary>
    public const ushort FofNoConfirmation = 0x0010;

    /// <summary><c>FOF_NOCONFIRMMKDIR</c>：需要新建目录时不确认。</summary>
    public const ushort FofNoConfirmMkDir = 0x0200;

    /// <summary><c>FOF_NOERRORUI</c>：出错不弹系统错误框（错误码交回调用方处理）。</summary>
    public const ushort FofNoErrorUi = 0x0400;

    // ── 结构 ────────────────────────────────────────────────

    /// <summary>
    /// <c>WIN32_FIND_DATAW</c>。
    ///
    /// <para>
    /// ⚠️ **必须加 <c>Pack = 4</c>**：里面的 FILETIME 是按 4 字节对齐的（两个 DWORD），
    /// 而 C# 的 <c>long</c> 默认按 8 字节对齐 —— 不加的话第一个时间字段就从 offset 8 开始，
    /// 后面 cFileName 全部错位，读出来是空串（2026-11-03 上游测试就是这么挂的）。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct Win32FindData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    /// <summary><c>SHFILEOPSTRUCTW</c>。只有走回收站那一条路用得上。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;

        /// <summary>
        /// 源路径，**必须是双 null 结尾**。封送 <c>string</c> 时运行时会补一个 <c>\0</c>，
        /// 所以调用方要在字符串末尾自己再拼一个（见 <c>GhostFile.TryRecycle</c>）。
        /// </summary>
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;

        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    // ── kernel32：长路径下的枚举与删除 ──────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileW")]
    public static extern IntPtr FindFirstFileW(string pattern, out Win32FindData data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFileW(IntPtr handle, out Win32FindData data);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "FindClose")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")]
    public static extern uint GetFileAttributesW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetFileAttributesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFileAttributesW(string path, uint attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DeleteFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteFileW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RemoveDirectoryW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveDirectoryW(string path);

    /// <summary>
    /// 改名 / 移动（同一目录内改名就是它）。幽灵文件名不合规矩时，靠它先改成合法名字，
    /// 才走得通回收站 —— 见 <c>GhostFile.Delete</c>。
    /// <c>\\?\</c> 前缀在这条路上是有效的（文件系统层，不经过 Shell）。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileW(string from, string to);

    // ── shell32：回收站 + 刷新视图 ───────────────────────────

    /// <summary>
    /// 带 <c>FOF_ALLOWUNDO</c> 的删除 = 移入回收站。返回值为 0 表示成功（不是 Win32 错误码语义）。
    ///
    /// <para>
    /// ⚠️ 它**不接受 <c>\\?\</c> 前缀**（回收站是 Shell 层的概念，Shell 不认这种裸路径），
    /// 所以只能用 8.3 短名或规范名去调 —— 见 <c>GhostFile.Delete</c> 的三层回退。
    /// </para>
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    public static extern int SHFileOperationW(ref ShFileOpStruct op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHChangeNotify")]
    public static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
}
