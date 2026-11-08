using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 「幽灵文件」的检测与清理（自上游 dv1.2.0-insider1.10 移植，实验性功能）。
///
/// <para>
/// 什么算幽灵文件：**目录能列出来，但按正常路径打不开**。典型来源是名字不合 Win32 规矩 ——
/// 末尾带空格或点、名字里有 <c>&lt; &gt; : " | ? *</c> 这类字符、或者撞上 <c>CON</c> / <c>NUL</c>
/// 这种系统保留名。资源管理器用非规范化的方式列目录，所以照样画出图标；
/// 双击或删除时走的是 Win32 规范化路径（末尾空格被吃掉），于是提示「找不到路径」。
/// </para>
///
/// <para>
/// 判定与清理都靠长路径前缀 <c>\\?\</c>：它让系统**不规范化**路径，原样交给文件系统。
/// 8.3 短名（<c>SHORTN~1</c>）作为第二道兜底 —— 老系统、老文件系统上偶尔只有短名走得通。
/// </para>
///
/// <para>
/// ⚠️ 回收站接口（<c>SHFileOperation</c> + <c>FOF_ALLOWUNDO</c>）**不接受 <c>\\?\</c> 前缀**：
/// 回收站是 Shell 层的概念，Shell 不认这种裸路径；而且名字不合规时它连 8.3 短名都不收
/// （一律 <c>DE_INVALIDFILES</c>）。所以删除分三步（见 <see cref="Delete"/>）：
/// 先按原名进回收站 → 不行就用长路径**改成合法名**再进回收站 → 还不行才退回上游的
/// 「长路径直接删」（不可还原）。
/// </para>
///
/// <para>
/// ⚠️ 另一种「删了还在」是**资源管理器视图缓存**，那种情况文件系统里根本没有这个条目，
/// <see cref="Scan"/> 什么也扫不到（不是漏扫，是真的不存在）。这种情况只能刷新视图，
/// 见 <see cref="RefreshExplorer"/>。
/// </para>
/// </summary>
public static class GhostFile
{
    /// <summary>一个被判定为幽灵的条目。</summary>
    public sealed class GhostEntry
    {
        /// <summary>父目录（用户给的、可直接显示的普通路径）。</summary>
        public string Parent { get; init; } = "";

        /// <summary>真实名字，**原样保留**（可能末尾带空格/点）。</summary>
        public string Name { get; init; } = "";

        /// <summary>带 <c>\\?\</c> 前缀的完整路径，访问与删除都用它。</summary>
        public string LongPath { get; init; } = "";

        /// <summary>8.3 短名。拿不到就是空串。</summary>
        public string ShortName { get; init; } = "";

        public bool IsDirectory { get; init; }

        /// <summary>字节数；目录为 -1。</summary>
        public long Size { get; init; }

        /// <summary>给人看的名字：末尾的空格画成 ␣、末尾的点画成 ·，不然肉眼根本分不出来。</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>为什么判成幽灵，一句话。</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>扫描结果。</summary>
    public sealed class ScanResult
    {
        public List<GhostEntry> Items { get; init; } = new();

        /// <summary>扫描失败的原因（成功时为空）。</summary>
        public string Error { get; init; } = "";

        /// <summary>父目录下总共列出来多少个条目（含正常的），用来区分「目录是空的」和「都不异常」。</summary>
        public int Total { get; init; }
    }

    // ── 对外 ────────────────────────────────────────────────

    /// <summary>
    /// 扫描指定目录（**只扫这一级**），找出名字不合规矩、正常路径打不开的条目。
    ///
    /// <para>
    /// 移植新增：<paramref name="token"/>。这一趟是纯 IO，目录里条目多的时候能跑几十秒，
    /// 页面离开时要把 <c>Task.Run</c> 里那一趟停掉，不然它会攥着句柄继续跑完。
    /// 取消时抛 <see cref="OperationCanceledException"/>（由页面接住，不当错误处理）。
    /// </para>
    /// </summary>
    public static ScanResult Scan(string parentDir, CancellationToken token = default)
    {
        var items = new List<GhostEntry>();

        try
        {
            var parent = Path.GetFullPath(parentDir.Trim().Trim('"'));
            if (!Directory.Exists(parent))
                return new ScanResult { Error = "这个目录不存在，或者没有访问权限。" };

            var longParent = ToLongPath(parent);
            var pattern = longParent.TrimEnd('\\') + @"\*";

            var total = 0;
            var handle = GhostNative.FindFirstFileW(pattern, out var data);
            if (handle == GhostNative.InvalidHandleValue)
                return new ScanResult { Error = DescribeWin32(Marshal.GetLastWin32Error()) };

            try
            {
                do
                {
                    token.ThrowIfCancellationRequested();

                    var name = data.cFileName;
                    if (name is "." or "..") continue;
                    total++;

                    var entry = Inspect(parent, longParent, name, data);
                    if (entry is not null) items.Add(entry);
                }
                while (GhostNative.FindNextFileW(handle, out data));
            }
            finally
            {
                GhostNative.FindClose(handle);
            }

            items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new ScanResult { Items = items, Total = total };
        }
        catch (OperationCanceledException)
        {
            // 用户离开了页面，不是错误：原样抛给页面
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Exception("ghost", ex, "扫描幽灵文件");
            return new ScanResult { Error = "扫描时出错：" + ex.Message };
        }
    }

    /// <summary>
    /// 删除一个幽灵条目。**优先送进回收站**，实在不行才退回长路径直接删。
    ///
    /// <para>
    /// 走法（顺序很重要，每一步都是上一步行不通才轮到）：
    /// <list type="number">
    /// <item>名字本身就合法 → 直接 <c>SHFileOperation + FOF_ALLOWUNDO</c>。</item>
    /// <item>名字不合法（末尾空格 / 保留名 / 含非法字符）→ 用 <c>\\?\</c> 长路径把它**改成**
    ///   同目录下一个合法临时名，再送回收站。这一步是实测出来的：Shell 对不合规名字
    ///   一律返回 <c>DE_INVALIDFILES</c>（2），连 8.3 短名都不收（2026-11-05 本机验证）。</item>
    /// <item>改名也失败 / 回收站还失败 → 退回上游口径：长路径直接删（明确不可还原）。</item>
    /// </list>
    /// ⚠️ 第 ①②层走完即视为「已尽力送回收站，但无法确认是否真进回收站」（可能不可还原），
    ///    与第 ③层一样把 <paramref name="permanent"/> 置 true。
    /// </para>
    ///
    /// <para>
    /// 相对上游的两处收紧（教室机场景）：
    /// ① 上游的 <c>Delete</c> 只有硬删；这里优先回收站。
    /// ② 上游不检查目标目录，这里加 <see cref="IsProtected"/>：磁盘根、Windows / Program Files /
    ///    ProgramData 及其子目录、用户目录根，一律不动。
    /// </para>
    /// </summary>
    /// <param name="permanent">
    /// true 表示**无法确认**已进回收站（可能是被静默永久删除，可能不可还原）。
    /// ⚠️ SHFileOperation 带 FOF_NOCONFIRMATION 时，「回收站被组策略关闭」和「文件太大」
    ///    都会被自动应答为永久删除且仍返回 0，代码无法区分，故凡走回收站路径一律置 true。
    ///    界面文案统一写「可能不可还原」，不得承诺可以还原。
    /// </param>
    public static bool Delete(GhostEntry entry, out string error, out bool permanent)
    {
        error = "";
        permanent = false;

        // ⛔ 护栏一：条目本身必须就在 Parent 下面一层。名字里带分隔符 / 是 . 或 ..
        //    说明这个 GhostEntry 不是 Scan 出来的（或者被别处伪造了），一律不删。
        if (!IsSafeEntry(entry, out error)) return false;

        // ⛔ 护栏二：系统目录不碰
        if (IsProtected(entry.Parent, out var why))
        {
            error = why;
            return false;
        }

        // ── 第一层：名字合法就直接进回收站 ──
        //    ⚠️ 先过 ShellCanResolve：CON / NUL 这类保留名走普通路径会落到**设备**上，
        //       名字里带 : < > | ? * 的则会被 Shell 当成通配或数据流解析。这两种一律不交给 Shell。
        if (ShellCanResolve(entry.Name))
        {
            if (TryRecycle(Path.Combine(entry.Parent, entry.Name), out var whyDirect))
            {
                // ⛔ SHFileOperation 带 FOF_NOCONFIRMATION 时，回收站被禁用 / 配额不足等情形
                //    会被 Shell 静默永久删除，返回值无法区分。既然无法确认是否真进了
                //    回收站，一律按「可能不可还原」处理，绝不承诺可还原。
                AppLog.Info("ghost", $"「{entry.Name}」已尽力送回收站｜无法确认是否进回收站（可能不可还原）");
                permanent = true;
                return true;
            }
            AppLog.Warning("ghost", $"「{entry.Name}」原名进不了回收站：{whyDirect}");
        }

        // ── 第二层：先改成合法名字，再进回收站（名字不合规时唯一可行的进回收站走法） ──
        if (TryRenameAndRecycle(entry, out var whyRename))
        {
            AppLog.Info("ghost", $"「{entry.Name}」改名后已尽力送回收站｜无法确认是否进回收站（可能不可还原）");
            permanent = true;
            return true;
        }
        AppLog.Warning("ghost", $"「{entry.Name}」改名后仍进不了回收站：{whyRename}，改用长路径直接删");

        // ── 第三层：长路径直接删（上游原口径，不可还原） ──
        if (TryDelete(entry.LongPath, entry.IsDirectory, out error))
        {
            permanent = true;
            return true;
        }

        // ── 第四层：长路径下挂短名再删一次 ──
        if (entry.ShortName.Length > 0)
        {
            var dirPart = entry.LongPath[..(entry.LongPath.Length - entry.Name.Length)];
            if (TryDelete(dirPart + entry.ShortName, entry.IsDirectory, out error))
            {
                permanent = true;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这个目录是不是**不该在这里做清理**的（磁盘根 / 系统目录 / 用户目录根）。
    ///
    /// <para>
    /// 上游没有这层判断（它假定用户只会填某个具体文件夹）。教室机上误填 <c>C:\Windows</c>
    /// 之后「扫描 → 全选 → 删除」的破坏面太大，所以这里按目录挡一道：
    /// 只挡清理，不挡扫描（扫描是只读的，页面上会提示「只检查、不会删」）。
    /// </para>
    /// </summary>
    public static bool IsProtected(string parentDir, out string reason)
    {
        reason = "";

        string full;
        try
        {
            full = Normalize(parentDir);
        }
        catch (Exception ex)
        {
            reason = "路径格式不正确：" + ex.Message;
            return true;
        }

        foreach (var exact in ForbiddenExact)
        {
            if (Same(full, exact))
            {
                reason = $"「{exact}」是磁盘或用户目录的根，不在这一层做清理。请填要清理的那一级子文件夹。";
                return true;
            }
        }

        foreach (var tree in ForbiddenTrees)
        {
            if (Same(full, tree) || IsUnder(full, tree))
            {
                reason = $"「{tree}」属于系统目录，出于安全考虑不在这里做任何清理。";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 让资源管理器重新读一遍这个目录（对付「文件其实早没了、图标还挂着」的显示缓存）。
    ///
    /// ⚠️ 这是温和做法，只会让已打开的窗口重绘。真要彻底清掉，得重启资源管理器；
    ///    那会关掉用户所有文件夹窗口，所以不放在这里自动做。
    /// </summary>
    public static void RefreshExplorer(string parentDir)
    {
        try
        {
            var parent = Path.GetFullPath(parentDir.Trim().Trim('"'));
            GhostNative.SHChangeNotify(GhostNative.ShcneUpdateDir, GhostNative.ShcnfPathW | GhostNative.ShcnfFlush,
                parent, IntPtr.Zero);
            AppLog.Info("ghost", "已请求资源管理器刷新：" + parent);
        }
        catch (Exception ex)
        {
            AppLog.Warning("ghost", "刷新资源管理器失败：" + ex.Message);
        }
    }

    /// <summary>把名字里的不可见字符画出来（末尾空格 → ␣，末尾点 → ·）。</summary>
    public static string Visualize(string name, out int trailingSpaces, out int trailingDots)
    {
        trailingSpaces = 0;
        trailingDots = 0;

        var end = name.Length;
        while (end > 0 && (name[end - 1] == ' ' || name[end - 1] == '.'))
        {
            if (name[end - 1] == ' ') trailingSpaces++;
            else trailingDots++;
            end--;
        }

        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < end; i++)
        {
            var c = name[i];
            sb.Append(c < 32 || c == 127 ? '·' : c);
        }

        sb.Append('␣', trailingSpaces);
        sb.Append('·', trailingDots);
        return sb.ToString();
    }

    // ── 内部 ────────────────────────────────────────────────

    /// <summary>判断单个条目是不是幽灵；是就返回描述，不是返回 null。</summary>
    private static GhostEntry? Inspect(string parent, string longParent, string name, GhostNative.Win32FindData data)
    {
        var normalPath = Path.Combine(parent, name);

        // 正常路径打得开 → 普通条目，不看
        if (GhostNative.GetFileAttributesW(normalPath) != GhostNative.InvalidFileAttributes) return null;

        // 保留名特殊处理：GetFileAttributesW("C:\dir\NUL") 会去打开设备并"成功"，
        // 不特判的话 NUL / CON 这类会被当成正常文件。
        var reserved = IsReservedName(name);

        // 正常路径打不开，再用长路径确认一次：连 \\?\ 都打不开的，不是幽灵（大概率是权限）
        var longPath = longParent.TrimEnd('\\') + "\\" + name;
        var attr = GhostNative.GetFileAttributesW(longPath);
        if (attr == GhostNative.InvalidFileAttributes && !reserved) return null;

        var isDir = attr != GhostNative.InvalidFileAttributes
                    && (attr & GhostNative.FileAttributeDirectory) != 0;

        var size = isDir ? -1L : ((long)data.FileSizeHigh << 32) | data.FileSizeLow;

        var display = Visualize(name, out var spaces, out var dots);

        var reasons = new List<string>();
        if (spaces > 0) reasons.Add($"末尾有 {spaces} 个空格");
        if (dots > 0) reasons.Add($"末尾有 {dots} 个点");
        if (reserved) reasons.Add("是系统保留名");
        if (reasons.Count == 0) reasons.Add("名字含系统不接受的字符");

        return new GhostEntry
        {
            Parent = parent,
            Name = name,
            LongPath = longPath,
            ShortName = data.AlternateFileName ?? "",
            IsDirectory = isDir,
            Size = size,
            DisplayName = display,
            Reason = string.Join("、", reasons),
        };
    }

    /// <summary>Win32 保留设备名。这类名字正常路径访问会落到设备上，必须单独认。</summary>
    private static bool IsReservedName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot >= 0 ? name[..dot] : name).TrimEnd(' ').ToUpperInvariant();

        if (stem is "CON" or "PRN" or "AUX" or "NUL") return true;

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
               && stem[3] is >= '1' and <= '9';
    }

    /// <summary>
    /// 这个名字交给 Shell（回收站 / <c>SHFileOperation</c>）解析安不安全。
    /// 保留名会落到设备上，含非法字符的会被当成通配符 / 数据流 —— 都不行。
    /// </summary>
    private static bool ShellCanResolve(string name)
    {
        if (name.Length == 0 || IsReservedName(name)) return false;

        foreach (var c in name)
        {
            if (c < 32) return false;
            if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*') return false;
        }

        return true;
    }

    /// <summary>
    /// 先把它改成同目录下一个合法名字，再送进回收站。
    ///
    /// <para>
    /// 为什么要绕这一圈：<c>SHFileOperation</c> 只认 Shell 能解析的路径（它内部要先把字符串
    /// 转成 PIDL），名字末尾带空格 / 是保留名 / 含 <c>: | ? *</c> 时它一律返回
    /// <c>DE_INVALIDFILES</c>(2) —— 实测**连 8.3 短名也不收**。而改名走 <c>\\?\</c> 长路径，
    /// 文件系统层不看名字规矩，改完之后 Shell 就认得这个新名字了。
    /// </para>
    ///
    /// <para>
    /// 代价：从回收站还原出来的名字是那个临时名（<c>~ghost-xxxxxxxx</c> + 原后缀），
    /// 内容原样。比起「永久删除、不可还原」，这个代价值得。
    /// 改名成功但回收站仍不收时，**把名字改回去**，不留下改了一半的现场。
    /// </para>
    /// </summary>
    private static bool TryRenameAndRecycle(GhostEntry entry, out string error)
    {
        error = "";

        var tempName = SuggestLegalName(entry);
        var tempLong = ToLongPath(Normalize(entry.Parent)).TrimEnd('\\') + "\\" + tempName;
        var tempPlain = Path.Combine(entry.Parent, tempName);

        if (!GhostNative.MoveFileW(entry.LongPath, tempLong))
        {
            error = DescribeWin32(Marshal.GetLastWin32Error());
            return false;
        }

        if (TryRecycle(tempPlain, out var why))
            return true;

        // 回收站还是不收：改回原名，交给上层硬删（否则文件会以临时名留在原地）
        if (!GhostNative.MoveFileW(tempLong, entry.LongPath))
        {
            AppLog.Warning("ghost",
                $"「{entry.Name}」已改名为「{tempName}」但改不回去（{DescribeWin32(Marshal.GetLastWin32Error())}），"
                + "它会以这个临时名留在原目录。");
        }

        error = why;
        return false;
    }

    /// <summary>
    /// 给幽灵条目起一个合法临时名：<c>~ghost-xxxxxxxx</c>，原后缀能留就留（还原后还认得出是什么文件）。
    /// </summary>
    private static string SuggestLegalName(GhostEntry entry)
    {
        var suffix = "";

        var name = entry.Name.TrimEnd(' ', '.');
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1)
        {
            var ext = name[dot..];
            var legal = ext.Length <= 12;
            for (var i = 1; legal && i < ext.Length; i++)
            {
                var c = ext[i];
                if (!char.IsLetterOrDigit(c)) legal = false;
            }
            if (legal) suffix = ext;
        }

        // GUID 前 8 位：同目录内撞名概率可以忽略，真撞上就让系统返回「已存在」，上层会改回原名
        return "~ghost-" + Guid.NewGuid().ToString("N")[..8] + suffix;
    }

    /// <summary>
    /// 走回收站（<c>SHFileOperation</c> + <c>FOF_ALLOWUNDO</c>）。返回 true 只表示
    /// Shell 调用未报错，**不表示**条目确实进了回收站。
    ///
    /// <para>
    /// ⚠️ 回收站被关掉（组策略 / 「不将文件移到回收站」勾上）或文件超过配额时，
    /// Shell 会**直接永久删除**，<c>fAnyOperationsAborted</c> 体现不出区别。
    /// 因此调用方一律按「无法确认、可能不可还原」处理（<c>permanent = true</c>），
    /// 页面确认文案与删完提示都不得承诺「可还原」。
    /// </para>
    /// </summary>
    private static bool TryRecycle(string path, out string error)
    {
        error = "";

        try
        {
            var op = new GhostNative.ShFileOpStruct
            {
                hwnd = IntPtr.Zero,
                wFunc = GhostNative.FoDelete,

                // 结构体封送 string 会补一个 \0，这里再补一个 → 凑成双 null 结尾
                pFrom = path + "\0",
                pTo = null,
                fFlags = GhostNative.FofAllowUndo
                         | GhostNative.FofNoConfirmation
                         | GhostNative.FofNoConfirmMkDir
                         | GhostNative.FofSilent
                         | GhostNative.FofNoErrorUi,
            };

            var code = GhostNative.SHFileOperationW(ref op);

            if (code == 0 && !op.fAnyOperationsAborted) return true;

            error = code != 0 ? DescribeWin32(code) : "操作被中止";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 真正执行删除（上游口径：长路径直接删，**不可还原**）。目录走自己写的递归 ——
    /// .NET 的递归删除碰到这些怪名字和只读属性容易半途而废，而且失败了也说不清是哪一个删不掉。
    /// </summary>
    private static bool TryDelete(string path, bool isDirectory, out string error)
    {
        error = "";

        try
        {
            if (!isDirectory)
            {
                MakeWritable(path);
                if (GhostNative.DeleteFileW(path)) return true;

                var code = Marshal.GetLastWin32Error();
                error = DescribeWin32(code);
                return false;
            }

            // 目录：先把里面的东西删干净
            var pattern = path.TrimEnd('\\') + @"\*";
            var handle = GhostNative.FindFirstFileW(pattern, out var data);
            if (handle != GhostNative.InvalidHandleValue)
            {
                try
                {
                    do
                    {
                        var child = data.cFileName;
                        if (child is "." or "..") continue;

                        var childPath = path.TrimEnd('\\') + "\\" + child;
                        var childIsDir = (data.FileAttributes & GhostNative.FileAttributeDirectory) != 0
                                         && (data.FileAttributes & GhostNative.FileAttributeReparsePoint) == 0;

                        TryDelete(childPath, childIsDir, out _);   // 单个删不掉不阻断整体
                    }
                    while (GhostNative.FindNextFileW(handle, out data));
                }
                finally
                {
                    GhostNative.FindClose(handle);
                }
            }

            if (GhostNative.RemoveDirectoryW(path)) return true;

            error = DescribeWin32(Marshal.GetLastWin32Error());
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void MakeWritable(string path)
    {
        var attr = GhostNative.GetFileAttributesW(path);
        if (attr == GhostNative.InvalidFileAttributes) return;
        if ((attr & GhostNative.FileAttributeReadOnly) == 0) return;

        GhostNative.SetFileAttributesW(path, GhostNative.FileAttributeNormal);
    }

    /// <summary>普通路径 → <c>\\?\</c> 长路径（UNC 走 <c>\\?\UNC\</c>）。</summary>
    private static string ToLongPath(string full)
    {
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    /// <summary>把 Win32 错误码翻成能看懂的一句话（常见那几个手写，其余交给系统）。</summary>
    private static string DescribeWin32(int code) => code switch
    {
        2 => "找不到路径",
        3 => "找不到路径",
        5 => "没有权限",
        32 => "文件正被别的程序占用",
        123 => "名字格式不正确",
        _ => new Win32Exception(code).Message,
    };

    // ── 安全护栏 ────────────────────────────────────────────

    /// <summary>其下（含自身）一律不清理的系统目录树。</summary>
    private static readonly string[] ForbiddenTrees = BuildForbiddenTrees();

    /// <summary>只挡自身、不挡其子目录的（磁盘根、用户目录根 —— 桌面这类子目录还要能清理）。</summary>
    private static readonly string[] ForbiddenExact = BuildForbiddenExact();

    private static string[] BuildForbiddenTrees()
    {
        var list = new List<string>();

        void Add(Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) list.Add(Normalize(path));
        }

        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.System);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonApplicationData);

        // System32 已经包含在 Windows 下；这里再补一个 SysWOW64（不在 SpecialFolder 里）
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
            list.Add(Normalize(Path.Combine(windows, "SysWOW64")));

        return list.ToArray();
    }

    private static string[] BuildForbiddenExact()
    {
        var list = new List<string>();

        // 所有**就绪**的盘符根（C: / D: ...）
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady) list.Add(Normalize(drive.RootDirectory.FullName));
                }
                catch
                {
                    // 个别盘符读状态就会抛（空的读卡器），跳过
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ghost", "枚举磁盘失败，磁盘根保护可能不完整：" + ex.Message);
        }

        // 用户目录根与它上一层（C:\Users）：桌面 / 下载这些**子目录**必须还能清理，所以只挡自身
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            var full = Normalize(profile);
            list.Add(full);

            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) list.Add(Normalize(parent));
        }

        return list.ToArray();
    }

    /// <summary>整条路径去掉末尾分隔符，用于比较（不改变大小写，比较时忽略）。</summary>
    private static string Normalize(string path) => Path.GetFullPath(path.Trim()).TrimEnd('\\');

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> 是不是在 <paramref name="root"/> 下面（不含 root 自身）。</summary>
    private static bool IsUnder(string path, string root) =>
        path.Length > root.Length
        && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && path[root.Length] == '\\';

    /// <summary>条目必须是 Parent 的**直接子项**，名字里不许有分隔符。</summary>
    private static bool IsSafeEntry(GhostEntry entry, out string error)
    {
        error = "";

        if (string.IsNullOrEmpty(entry.Parent) || string.IsNullOrEmpty(entry.Name))
        {
            error = "条目信息不完整，已跳过。";
            return false;
        }

        if (entry.Name is "." or ".." || entry.Name.IndexOfAny(new[] { '\\', '/' }) >= 0)
        {
            error = "条目的名字里带路径分隔符，已跳过。";
            return false;
        }

        // LongPath 必须正好是「父目录长路径 + \ + 名字」，否则说明这条记录不是本页扫出来的
        var expected = ToLongPath(Normalize(entry.Parent)).TrimEnd('\\') + "\\" + entry.Name;
        if (!string.Equals(entry.LongPath, expected, StringComparison.OrdinalIgnoreCase))
        {
            error = "条目路径与所在目录对不上，已跳过。";
            return false;
        }

        return true;
    }
}
