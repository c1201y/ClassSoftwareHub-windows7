using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>文件选择的结果：Files 为本地路径；Error 非空时给出面向用户的说明。</summary>
public sealed record FilePickResult(IReadOnlyList<string> Files, string? Error = null);

/// <summary>
/// 文件/文件夹选择兼容层。
///
/// <para>
/// ── 为什么 Win7 不能直接用 Avalonia 的 StorageProvider（2026-10-15，用户实机报障）──────
/// Avalonia 11.2.8 的 Win32 选择器把对话框结果按 <b>桌面绝对解析名</b>（SIGDN_DESKTOPABSOLUTEPARSING）
/// 转成本地路径再 <c>new FileInfo(...)</c>。Win7 的对话框默认落在「库」（图片/文档库）里，
/// 库内条目的解析名是 <c>::{CLSID}...</c> 壳命名空间路径 —— FileInfo 直接抛
/// NotSupportedException，用户看到的就是「选完确定没反应」。Win10/11 默认视图没有库，
/// 从不触发；拖拽不经 Shell，所以拖入一直正常（同源历史问题：Avalonia #1700/#3272）。
/// </para>
///
/// <para>⚠️ 2026-10-31（用户实机报障「MD5 校验页用窗口选文件报 0x0000005，进程全崩」）：
/// 上一版（2026-10-15 ~ 10-30）走的是**手写 IFileDialog COM 互操作**。那条路在 Win7 上有一处
/// 致命的封送隐患：<c>IShellItem::GetDisplayName</c> 声明成 <c>[MarshalAs(LPWStr)] out string</c> +
/// <c>PreserveSig</c>，而 Win7 在「库 / 特殊壳条目 / 套间不对」时返回失败 HRESULT，**出参指针此时是
/// 未初始化/无效值**（SDK 不保证失败时置 NULL）。封送器却仍会把这个指针当字符串读 —— 读进野指针
/// 就是访问冲突 0xC0000005，进程直接消失，连托管异常处理都来不及介入。
/// 另外「在后台 STA 线程里弹以 UI 线程窗口为 owner 的模态框」本身也不牢靠。</para>
///
/// <para>所以现在 Win7 改走 **comdlg32 的 GetOpenFileNameW / SHBrowseForFolderW**：
/// 这是 Win9x 时代就有的经典对话框，结果直接写在调用方提供的字符缓冲区里，
/// **不经过任何 Shell 项 / COM 出参封送**，没有野指针的可能；对话框在**调用它的 UI 线程上同步运行**
/// （Win32 模态框自带消息泵），不再跨线程。代价是 Win7 上的样式是系统经典「打开」框（本来就该长这样）。
/// Win8/10/11 仍然走 Avalonia 原路径，别改。</para>
///
/// <para>⛔ 改这条路径时注意：<c>GetOpenFileNameW</c> 必须在 STA 线程上调用（Avalonia 的 UI 线程
/// 就是 STA，<see cref="Program"/> 标了 <c>[STAThread]</c>）。<c>OFN_ALLOWMULTISELECT</c> 时缓冲区
/// 是「目录\0文件1\0文件2\0\0」的多字符串，单选时才是完整路径 —— 两种都要按 NUL 拆。</para>
/// </summary>
public static class FilePickerCompat
{
    /// <summary>
    /// 打开「选择文件」对话框。Win7 走 comdlg32 兜底，其余系统走 Avalonia StorageProvider。
    /// 取消/关掉返回空列表；出错时 <see cref="FilePickResult.Error"/> 带面向用户的文案。
    /// </summary>
    public static Task<FilePickResult> PickFilesAsync(
        TopLevel top, string title, bool allowMultiple, IReadOnlyList<FilePickerFileType>? filters)
    {
        if (!OsInfo.IsWindows7)
            return PickFilesWithAvaloniaAsync(top, title, allowMultiple, filters);

        // ⚠️ 同步调用、跑在 UI 线程上：Win32 模态对话框自己泵消息，Avalonia 不会卡死。
        return Task.FromResult(Win7Picker.PickFiles(GetHwnd(top), title, allowMultiple, filters));
    }

    /// <summary>打开「选择文件夹」对话框（Win7 同样走兜底，理由同上）。</summary>
    public static Task<FilePickResult> PickFolderAsync(TopLevel top, string title)
    {
        if (!OsInfo.IsWindows7)
            return PickFolderWithAvaloniaAsync(top, title);

        return Task.FromResult(Win7Picker.PickFolder(GetHwnd(top), title));
    }

    private static async Task<FilePickResult> PickFilesWithAvaloniaAsync(
        TopLevel top, string title, bool allowMultiple, IReadOnlyList<FilePickerFileType>? filters)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = filters,
        });
        return new FilePickResult(files.Select(f => f.Path.LocalPath).ToList());
    }

    private static async Task<FilePickResult> PickFolderWithAvaloniaAsync(TopLevel top, string title)
    {
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return new FilePickResult(folders.Select(f => f.Path.LocalPath).ToList());
    }

    private static IntPtr GetHwnd(TopLevel top)
    {
        try { return top.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }
}

/// <summary>
/// Win7 的文件/文件夹选择：comdlg32 经典对话框。
///
/// 历史：2026-10-15 起这里曾是手写 IFileDialog COM（为了拿 SIGDN_FILESYSPATH 避开「库」路径问题），
/// 2026-10-31 因实机访问冲突（0xC0000005）整段换掉 —— 原因见 <see cref="FilePickerCompat"/> 的类注释。
/// <b>不要</b>再把手写的 IShellItem 出参封送加回来。
/// </summary>
internal static class Win7Picker
{
    // ══════════════════════════════════════════════════════════════════
    //  comdlg32 / shell32 / ole32
    // ══════════════════════════════════════════════════════════════════

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static extern uint CommDlgExtendedError();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void OleUninitialize();

    // OFN_*
    private const uint OfnNoChangeDir = 0x00000008;
    private const uint OfnAllowMultiSelect = 0x00000200;
    private const uint OfnPathMustExist = 0x00000800;
    private const uint OfnFileMustExist = 0x00001000;
    private const uint OfnExplorer = 0x00080000;
    private const uint OfnEnableSizing = 0x00800000;
    private const uint OfnDontAddToRecent = 0x02000000;

    // CDERR / FNERR
    private const uint FnerrBufferTooSmall = 0x3003;

    // BIF_*
    private const uint BifReturnOnlyFsDirs = 0x0001;
    private const uint BifEditBox = 0x0010;
    private const uint BifNewDialogStyle = 0x0040;

    /// <summary>多选缓冲：OFN_ALLOWMULTISELECT 建议 ≥ 32K 字符；这个大小也足够容纳几百个长路径。</summary>
    private const int FileBufferChars = 32768;

    private const int MaxPath = 520;

    /// <summary>OLE 已初始化（S_OK / S_FALSE）。RPC_E_CHANGED_MODE 时不能再 NEWDIALOGSTYLE。</summary>
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public uint FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFOW
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;     // 调用方提供的 LPWSTR 缓冲（可为 0 / 空缓冲）
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    // ── 对外 ───────────────────────────────────────────────────────

    public static FilePickResult PickFiles(
        IntPtr owner, string title, bool allowMultiple, IReadOnlyList<FilePickerFileType>? filters)
    {
        var ole = OleInitialize(IntPtr.Zero);
        var started = Environment.TickCount64;

        var fileBuffer = Marshal.AllocHGlobal(FileBufferChars * sizeof(char));
        var filter = IntPtr.Zero;

        try
        {
            // 缓冲区必须清零：多选结果靠 NUL 分隔，残留数据会被当成文件名读出来
            Marshal.Copy(new byte[FileBufferChars * sizeof(char)], 0, fileBuffer, FileBufferChars * sizeof(char));

            filter = Marshal.StringToHGlobalUni(BuildFilterString(filters));

            var flags = OfnExplorer | OfnPathMustExist | OfnFileMustExist |
                        OfnNoChangeDir | OfnEnableSizing | OfnDontAddToRecent;
            if (allowMultiple) flags |= OfnAllowMultiSelect;

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = owner,
                lpstrFilter = filter,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = FileBufferChars,
                lpstrTitle = string.IsNullOrWhiteSpace(title) ? "选择文件" : title,
                Flags = flags,
            };

            Core.AppLog.Info("picker", $"Win7 打开文件对话框（GetOpenFileNameW，多选={allowMultiple}，" +
                                       $"线程套间={Thread.CurrentThread.GetApartmentState()}）");

            if (!GetOpenFileNameW(ref ofn))
            {
                var err = CommDlgExtendedError();
                var ms = Environment.TickCount64 - started;

                if (err == 0)
                {
                    Core.AppLog.Info("picker", $"Win7 文件对话框：用户取消（{ms}ms）");
                    return new FilePickResult(Array.Empty<string>());
                }

                Core.AppLog.Warn("picker", $"Win7 文件对话框失败：CommDlgExtendedError=0x{err:X4}（{ms}ms）");
                return new FilePickResult(Array.Empty<string>(),
                    err == FnerrBufferTooSmall
                        ? "一次选择的文件太多，请分几次选择。"
                        : $"打开文件选择器失败（对话框错误 0x{err:X4}）。");
            }

            var paths = ParseSelectedFiles(fileBuffer, FileBufferChars);
            Core.AppLog.Info("picker", $"Win7 文件对话框返回 {paths.Count} 个文件（{(Environment.TickCount64 - started)}ms）");

            if (paths.Count == 0)
            {
                Core.AppLog.Warn("picker", "Win7 文件对话框返回成功，但缓冲区里没有路径");
                return new FilePickResult(paths, "未能读取所选文件，请重新选择。");
            }

            return new FilePickResult(paths);
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("picker", "Win7 文件对话框异常：" + ex);
            return new FilePickResult(Array.Empty<string>(), "打开文件选择器失败：" + ex.Message);
        }
        finally
        {
            if (filter != IntPtr.Zero) Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(fileBuffer);
            if (ole >= 0) OleUninitialize();
        }
    }

    public static FilePickResult PickFolder(IntPtr owner, string title)
    {
        var ole = OleInitialize(IntPtr.Zero);
        var newStyle = ole >= 0 && ole != RpcEChangedMode;

        var pidl = IntPtr.Zero;
        var display = Marshal.AllocHGlobal(MaxPath * sizeof(char));

        try
        {
            Marshal.Copy(new byte[MaxPath * sizeof(char)], 0, display, MaxPath * sizeof(char));

            var flags = BifReturnOnlyFsDirs | BifEditBox;
            if (newStyle) flags |= BifNewDialogStyle;

            var bi = new BROWSEINFOW
            {
                hwndOwner = owner,
                pszDisplayName = display,
                lpszTitle = string.IsNullOrWhiteSpace(title) ? "选择文件夹" : title,
                ulFlags = flags,
            };

            Core.AppLog.Info("picker", $"Win7 打开文件夹对话框（SHBrowseForFolderW，新样式={newStyle}，" +
                                       $"线程套间={Thread.CurrentThread.GetApartmentState()}）");

            pidl = SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero)
            {
                Core.AppLog.Info("picker", "Win7 文件夹对话框：用户取消");
                return new FilePickResult(Array.Empty<string>());
            }

            var sb = new StringBuilder(MaxPath);
            if (!SHGetPathFromIDListW(pidl, sb) || sb.Length == 0)
            {
                Core.AppLog.Warn("picker", "Win7 文件夹对话框：所选位置不是文件系统路径");
                return new FilePickResult(Array.Empty<string>(),
                    "所选位置不是本地文件夹，请打开具体磁盘后重新选择。");
            }

            return new FilePickResult(new[] { sb.ToString() });
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("picker", "Win7 文件夹对话框异常：" + ex);
            return new FilePickResult(Array.Empty<string>(), "打开文件夹选择器失败：" + ex.Message);
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            Marshal.FreeHGlobal(display);
            if (ole >= 0) OleUninitialize();
        }
    }

    // ── 内部 ───────────────────────────────────────────────────────

    /// <summary>
    /// 解析 GetOpenFileNameW 的结果缓冲：
    /// 单选 = 一个完整路径；多选 = 「目录\0文件1\0文件2\0\0」（目录在前，后面都是相对文件名）。
    /// </summary>
    private static List<string> ParseSelectedFiles(IntPtr buffer, int capacityChars)
    {
        var parts = ReadMultiString(buffer, capacityChars);
        if (parts.Count <= 1) return parts;     // [] 或 [完整路径]

        var dir = parts[0];
        var files = new List<string>(parts.Count - 1);
        for (var i = 1; i < parts.Count; i++)
        {
            var name = parts[i];
            if (name.Length == 0) continue;
            files.Add(System.IO.Path.Combine(dir, name));
        }
        return files;
    }

    /// <summary>按 NUL 逐个读出多字符串，遇到空串（连续两个 NUL）就停；不会越过缓冲区边界。</summary>
    private static List<string> ReadMultiString(IntPtr buffer, int capacityChars)
    {
        var result = new List<string>();
        var offset = 0;

        while (offset < capacityChars)
        {
            var s = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset * sizeof(char)));
            if (string.IsNullOrEmpty(s)) break;
            result.Add(s);
            offset += s.Length + 1;
        }

        return result;
    }

    /// <summary>
    /// 组 Win32 过滤器串：<c>名称\0*.txt;*.md\0名称\0*.png\0\0</c>。
    /// StringToHGlobalUni 会自动补最后一个 '\0'，所以每对后面再写一个 '\0' 就得到双重结尾。
    /// </summary>
    private static string BuildFilterString(IReadOnlyList<FilePickerFileType>? filters)
    {
        var sb = new StringBuilder();

        if (filters is { Count: > 0 })
        {
            foreach (var f in filters)
            {
                var name = Clean(f.Name, "文件");
                var patterns = f.Patterns is { Count: > 0 }
                    ? string.Join(";", f.Patterns.Select(p => Clean(p, "*.*")).Where(p => p.Length > 0))
                    : string.Empty;
                if (patterns.Length == 0) patterns = "*.*";

                sb.Append(name).Append('\0').Append(patterns).Append('\0');
            }
        }

        // 没给过滤器就给「所有文件」，别让对话框默认只认得文档
        if (sb.Length == 0) sb.Append("所有文件").Append('\0').Append("*.*").Append('\0');

        return sb.ToString();
    }

    private static string Clean(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return value.Replace('\0', ' ').Trim();
    }
}
