using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
/// <para>解法：Win7 上改走自己声明的 IFileDialog COM 互操作（Vista 起就有，界面与系统一致），
/// 路径一律取 SIGDN_FILESYSPATH；真碰到库等非文件系统条目时回退解析名并给出明确提示，
/// 不再静默吞掉。Win8/10/11 维持 Avalonia 原路径不动。</para>
///
/// <para>⚠️ 2026-10-16（用户实机二次报障「选完文件＝没选 / 未能读取图片路径」）：
/// Win7 这条 COM 路径**必须在 STA 线程上跑**（见 <c>RunOnStaThread</c> 的注释）——
/// 之前放在 <c>Task.Run</c>（线程池 = MTA）里，Win7 上 <c>GetDisplayName</c> 取不到路径，
/// 而 Win10/11 侥幸能用，开发机上怎么试都正常。改这条路径时**不要**再退回线程池。</para>
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
        return await RunOnStaThread(() => FileDialog7.ShowFiles(hwnd, title, allowMultiple, filters));
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
        return await RunOnStaThread(() => FileDialog7.ShowFolders(hwnd, title));
    }

    /// <summary>
    /// 在**单线程套间（STA）**线程上跑一次 COM 调用。
    ///
    /// <para>⚠️ 2026-10-16（用户实机报障：「Win7 选完文件却提示读不到路径 / 未能读取图片路径」，
    /// 图片取色、全屏时钟背景图等所有选择入口一致，拖拽却正常）：
    /// Win7 上把 <c>IFileDialog</c> 丢进 <c>Task.Run</c>（线程池 = <b>MTA</b>）里跑，对话框照样弹得出来、
    /// 也能选文件，但**取结果时 <c>IShellItem::GetDisplayName(SIGDN_FILESYSPATH)</c> 会失败**
    /// （E_INVALIDARG），连 <c>SIGDN_DESKTOPABSOLUTEPARSING</c> 兜底也拿不到 —— 用户看到的就是
    /// 「选完文件＝没选」，而拖拽不经过 Shell，所以一直正常。根因是套间不匹配：
    /// <c>CLSID_FileOpenDialog</c> 是 Apartment 模型对象，从 MTA 调它、再跨套间取结果时，
    /// Win7 的 shell 会直接拒绝（Win10/11 侥幸能跑通，所以开发机 + <c>--simulate-win7</c> 复现不出来）。
    /// 参考：<c>IFileDialog / IShellItem::GetDisplayName doesn't work with Windows 7 libraries</c>
    /// —— 提问者最后自己定位到 <c>CoInitializeEx</c> 用了 <c>COINIT_MULTITHREADED</c>，改成
    /// <c>COINIT_APARTMENTTHREADED</c> 就好了。</para>
    ///
    /// <para>所以 Win7 这条路径必须专门开一个 STA 线程。CLR 会在 STA 线程启动时替我们
    /// <c>CoInitializeEx(COINIT_APARTMENTTHREADED)</c>；这里只做一次套间自检并记日志，
    /// 万一没生效，port.log 里能直接看到。</para>
    /// </summary>
    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var apt = Thread.CurrentThread.GetApartmentState();
                if (apt != ApartmentState.STA)
                    Core.AppLog.Warn("picker", $"Win7 文件选择器线程套间不是 STA（{apt}）—— 取结果可能失败");
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "csh-win7-file-picker",
        };

        try { thread.SetApartmentState(ApartmentState.STA); }
        catch (Exception ex) { Core.AppLog.Warn("picker", "设置 STA 套间失败：" + ex.Message); }

        thread.Start();
        return tcs.Task;
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

            // ⚠️ 过滤器字符串（COMDLG_FILTERSPEC）必须活到**对话框关掉之后**才能释放：Win7 的 shell
            //    不保证在 SetFileTypes 时就把它们复制走，提前 Free 有可能让它读到已释放的内存。
            var specs = Array.Empty<COMDLG_FILTERSPEC>();

            try
            {
                dialog.GetOptions(out var fos);
                fos |= FosOverwritePrompt | FosNoTestFileCreate | FosForceFilesystem | FosPathMustExist | FosDontAddToRecent;
                if (pickFolders) fos |= FosPickFolders;
                if (allowMultiple) fos |= FosAllowMultiSelect;
                var hrOptions = dialog.SetOptions(fos);
                if (hrOptions != 0) Core.AppLog.Warn("picker", $"SetOptions 失败 0x{hrOptions:X8}");
                dialog.SetTitle(title ?? "选择");

                if (!pickFolders)
                {
                    specs = BuildFilters(filters);
                    if (specs.Length > 0)
                    {
                        var hrTypes = dialog.SetFileTypes((uint)specs.Length, specs);
                        if (hrTypes != 0) Core.AppLog.Warn("picker", $"SetFileTypes 失败 0x{hrTypes:X8}");
                        var hrIndex = dialog.SetFileTypeIndex(1);      // ⚠️ 1 起始（Avalonia 写 0 是个无效值）
                        if (hrIndex != 0) Core.AppLog.Warn("picker", $"SetFileTypeIndex 失败 0x{hrIndex:X8}");
                    }
                }

                var hr = dialog.Show(owner);
                if (hr == HrCancelled) return new FilePickResult(Array.Empty<string>());
                Marshal.ThrowExceptionForHR(hr);

                // 取路径：优先文件系统路径；拿不到（库等壳条目）退回解析名，解析名还是壳路径就明说
                var paths = new List<string>();
                string? shellName = null;

                if (allowMultiple)
                {
                    var hrResults = dialog.GetResults(out var array);
                    if (hrResults != 0 || array is null)
                    {
                        Core.AppLog.Warn("picker", $"GetResults 失败 0x{hrResults:X8}");
                    }
                    else
                    {
                        try
                        {
                            var hrCount = array.GetCount(out var count);
                            if (hrCount != 0) Core.AppLog.Warn("picker", $"IShellItemArray.GetCount 失败 0x{hrCount:X8}");
                            for (uint i = 0; i < count; i++)
                            {
                                var hrItem = array.GetItemAt(i, out var item);
                                if (hrItem != 0)
                                {
                                    Core.AppLog.Warn("picker", $"IShellItemArray.GetItemAt({i}) 失败 0x{hrItem:X8}");
                                    continue;
                                }
                                if (ResolvePath(item, ref shellName) is { } p) paths.Add(p);
                            }
                        }
                        finally { Marshal.ReleaseComObject(array); }
                    }
                }
                else
                {
                    var hrResult = dialog.GetResult(out var item);
                    if (hrResult != 0) Core.AppLog.Warn("picker", $"GetResult 失败 0x{hrResult:X8}");
                    if (ResolvePath(item, ref shellName) is { } p)
                    {
                        paths.Add(p);
                    }
                    else if (PathFromFolderAndName(dialog) is { } viaFolder)
                    {
                        // 兜底：个别情况下 IShellItem 的显示名取不到（套间 / 壳条目异常），
                        // 退回「当前文件夹 + 文件名」自己拼一个 —— 总比让用户白选一次强。
                        Core.AppLog.Warn("picker", "GetResult 取不到文件系统路径，已用 GetFolder+GetFileName 兜底：" + viaFolder);
                        paths.Add(viaFolder);
                    }
                }

                if (paths.Count == 0)
                {
                    Core.AppLog.Warn("picker", $"选完文件却取不到路径（shellName={shellName ?? "<null>"}）");
                    return new FilePickResult(paths,
                        shellName is null
                            ? "未能读取所选位置的路径，请从具体文件夹（如桌面、D 盘）重新选择。"
                            : "所选位置是系统「库」等特殊位置，请打开具体文件夹后再选文件。");
                }
                return new FilePickResult(paths);
            }
            finally
            {
                FreeFilters(specs);
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
    private static string? ResolvePath(IShellItem? item, ref string? shellName)
    {
        if (item is null) return null;

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

    /// <summary>
    /// 最后一道兜底：用「对话框当前文件夹的文件系统路径 + 用户填/选的文件名」自己拼一个绝对路径。
    ///
    /// <para>只在 <c>GetResult</c> 那条路彻底取不到路径时才会走到（正常情况一次都不会执行）。
    /// 目的是不让 Win7 上任何一处 Shell 取名字的怪毛病把「选文件」变成「白选一次」——
    /// 拼出来还要过一遍 <see cref="System.IO.File.Exists(string)"/>，宁可不兜也不能塞错路径。</para>
    /// </summary>
    private static string? PathFromFolderAndName(IFileOpenDialog dialog)
    {
        try
        {
            if (dialog.GetFolder(out var folder) != 0 || folder is null) return null;
            if (folder.GetDisplayName(SigdnFilesystemPath, out var dir) != 0 || string.IsNullOrEmpty(dir)) return null;
            if (dialog.GetFileName(out var name) != 0 || string.IsNullOrEmpty(name)) return null;

            // 只接纯文件名：真拿到带路径的东西说明状态不对，宁可放弃
            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0) return null;

            var full = System.IO.Path.Combine(dir, name);
            return System.IO.File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
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
