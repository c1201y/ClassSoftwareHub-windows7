using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
/// <para>解法：Win7 上改走自己声明的 IFileDialog COM 互操作（Vista 起就有，界面与系统一致），
/// 路径一律取 SIGDN_FILESYSPATH；真碰到库等非文件系统条目时回退解析名并给出明确提示，
/// 不再静默吞掉。Win8/10/11 维持 Avalonia 原路径不动。</para>
/// </summary>
public static class FilePickerCompat
{
    /// <summary>
    /// 打开「选择文件」对话框。Win7 走自带 IFileDialog 兜底，其余系统走 Avalonia StorageProvider。
    /// 取消/关掉返回空列表；出错时 <see cref="FilePickResult.Error"/> 带面向用户的文案。
    /// </summary>
    public static async Task<FilePickResult> PickFilesAsync(
        TopLevel top, string title, bool allowMultiple, IReadOnlyList<FilePickerFileType>? filters)
    {
        if (!OsInfo.IsWindows7)
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = allowMultiple,
                FileTypeFilter = filters,
            });
            return new FilePickResult(files.Select(f => f.Path.LocalPath).ToList());
        }

        var hwnd = GetHwnd(top);
        return await Task.Run(() => FileDialog7.ShowFiles(hwnd, title, allowMultiple, filters));
    }

    /// <summary>打开「选择文件夹」对话框（Win7 同样走兜底，理由同上）。</summary>
    public static async Task<FilePickResult> PickFolderAsync(TopLevel top, string title)
    {
        if (!OsInfo.IsWindows7)
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return new FilePickResult(folders.Select(f => f.Path.LocalPath).ToList());
        }

        var hwnd = GetHwnd(top);
        return await Task.Run(() => FileDialog7.ShowFolders(hwnd, title));
    }

    private static IntPtr GetHwnd(TopLevel top)
    {
        try { return top.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }
}

/// <summary>IFileDialog 的原始 COM 声明（只声明用到的成员，vtable 顺序必须与 SDK 一致）。</summary>
internal static class FileDialog7
{
    private const uint SigdnFilesystemPath = 0x80058000;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;
    private static readonly int HrCancelled = unchecked((int)0x800704C7);   // ERROR_CANCELLED

    // ── COM 类与接口 ────────────────────────────────────────────────

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr hwndOwner);
        // IFileDialog（vtable 顺序照 shobjidl_core.h，一个都不能少）
        [PreserveSig] int SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        [PreserveSig] int SetFolder(IShellItem psi);
        [PreserveSig] int GetFolder(out IShellItem ppsi);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] int GetResult(out IShellItem ppsi);
        [PreserveSig] int AddPlace(IShellItem psi, int fdap);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr pFilter);
        // IFileOpenDialog
        [PreserveSig] int GetResults(out IShellItemArray ppenum);
        [PreserveSig] int GetSelectedItems(out IShellItemArray ppsai);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, Guid bhid, Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        // 前几个方法不使用，但必须占住 vtable 位置
        [PreserveSig] int BindToHandler(IntPtr pbc, Guid bhid, Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStore(int flags, Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int GetCount(out uint pdwNumItems);
        [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
        [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMDLG_FILTERSPEC
    {
        public IntPtr pszName;
        public IntPtr pszSpec;
    }

    // FOS_*
    private const uint FosOverwritePrompt = 0x2;
    private const uint FosNoTestFileCreate = 0x10000;
    private const uint FosForceFilesystem = 0x40;
    private const uint FosPickFolders = 0x20;
    private const uint FosAllowMultiSelect = 0x200;
    private const uint FosPathMustExist = 0x800;
    private const uint FosDontAddToRecent = 0x2000000;

    // ── 对外 ───────────────────────────────────────────────────────

    public static FilePickResult ShowFiles(IntPtr owner, string title, bool allowMultiple, IReadOnlyList<FilePickerFileType>? filters)
        => Show(owner, title, allowMultiple, filters, pickFolders: false);

    public static FilePickResult ShowFolders(IntPtr owner, string title)
        => Show(owner, title, allowMultiple: false, filters: null, pickFolders: true);

    private static FilePickResult Show(IntPtr owner, string title, bool allowMultiple, IReadOnlyList<FilePickerFileType>? filters, bool pickFolders)
    {
        try
        {
            // CLSID_FileOpenDialog = DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7（CoClass 直转接口在本工程的
            // LangVersion 10 下报 CS0030，改走 GetTypeFromCLSID + 运行时 QI，语义等价）
            var clsid = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
            var dialog = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true))!;

            try
            {
                dialog.GetOptions(out var fos);
                fos |= FosOverwritePrompt | FosNoTestFileCreate | FosForceFilesystem | FosPathMustExist | FosDontAddToRecent;
                if (pickFolders) fos |= FosPickFolders;
                if (allowMultiple) fos |= FosAllowMultiSelect;
                dialog.SetOptions(fos);
                dialog.SetTitle(title ?? "选择");

                if (!pickFolders)
                {
                    var specs = BuildFilters(filters);
                    try
                    {
                        if (specs.Length > 0)
                        {
                            dialog.SetFileTypes((uint)specs.Length, specs);
                            dialog.SetFileTypeIndex(1);      // ⚠️ 1 起始（Avalonia 写 0 是个无效值）
                        }
                    }
                    finally { FreeFilters(specs); }
                }

                var hr = dialog.Show(owner);
                if (hr == HrCancelled) return new FilePickResult(Array.Empty<string>());
                Marshal.ThrowExceptionForHR(hr);

                // 取路径：优先文件系统路径；拿不到（库等壳条目）退回解析名，解析名还是壳路径就明说
                var paths = new List<string>();
                string? shellName = null;

                if (allowMultiple)
                {
                    dialog.GetResults(out var array);
                    try
                    {
                        array.GetCount(out var count);
                        for (uint i = 0; i < count; i++)
                        {
                            array.GetItemAt(i, out var item);
                            if (ResolvePath(item, ref shellName) is { } p) paths.Add(p);
                        }
                    }
                    finally { Marshal.ReleaseComObject(array); }
                }
                else
                {
                    dialog.GetResult(out var item);
                    if (ResolvePath(item, ref shellName) is { } p) paths.Add(p);
                }

                if (paths.Count == 0)
                {
                    return new FilePickResult(paths,
                        shellName is null
                            ? "未能读取所选位置的路径，请从具体文件夹（如桌面、D 盘）重新选择。"
                            : "所选位置是系统「库」等特殊位置，请打开具体文件夹后再选文件。");
                }
                return new FilePickResult(paths);
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
        catch (COMException ex)
        {
            Core.AppLog.Warn("picker", $"Win7 文件对话框失败 0x{ex.HResult:X8} {ex.Message}");
            return new FilePickResult(Array.Empty<string>(), "打开文件选择器失败（0x" + ex.HResult.ToString("X8") + " " + ex.Message + "）。");
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("picker", "Win7 文件对话框异常：" + ex.Message);
            return new FilePickResult(Array.Empty<string>(), "打开文件选择器失败：" + ex.Message);
        }
    }

    /// <summary>文件系统路径优先；拿不到就用解析名兜底，但把壳路径（:: 开头）记下来交给上层提示。</summary>
    private static string? ResolvePath(IShellItem item, ref string? shellName)
    {
        try
        {
            if (item.GetDisplayName(SigdnFilesystemPath, out var path) == 0 && !string.IsNullOrEmpty(path))
                return path;
        }
        catch { /* 某些条目会直接抛，继续走兜底 */ }

        try
        {
            if (item.GetDisplayName(SigdnDesktopAbsoluteParsing, out var parsing) == 0 && !string.IsNullOrEmpty(parsing))
            {
                if (shellName is null && parsing.StartsWith("::")) shellName = parsing;
                return parsing.StartsWith("::") ? null : parsing;
            }
        }
        catch { }
        return null;
    }

    private static COMDLG_FILTERSPEC[] BuildFilters(IReadOnlyList<FilePickerFileType>? filters)
    {
        // 没给过滤器就给「所有文件」，别让对话框默认只认得文档
        IEnumerable<FilePickerFileType> source = filters is { Count: > 0 }
            ? filters
            : new[] { new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } } };

        var list = new List<COMDLG_FILTERSPEC>();
        foreach (var f in source)
        {
            var patterns = f.Patterns is { Count: > 0 } ? string.Join(";", f.Patterns) : "*.*";
            list.Add(new COMDLG_FILTERSPEC
            {
                pszName = Marshal.StringToHGlobalUni(f.Name ?? "文件"),
                pszSpec = Marshal.StringToHGlobalUni(patterns),
            });
        }
        return list.ToArray();
    }

    private static void FreeFilters(COMDLG_FILTERSPEC[] specs)
    {
        foreach (var s in specs)
        {
            if (s.pszName != IntPtr.Zero) Marshal.FreeHGlobal(s.pszName);
            if (s.pszSpec != IntPtr.Zero) Marshal.FreeHGlobal(s.pszSpec);
        }
    }
}
