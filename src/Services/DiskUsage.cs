using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>磁盘可视化里的一块：一个子项及其占用。</summary>
public sealed record DiskEntry(string Name, string FullPath, bool IsDirectory, long Bytes, int FileCount)
{
    /// <summary>
    /// 子项（自上游 dv1.2.1 移植，2026-11-14）。
    ///
    /// <para>
    /// 以前只算顶层：进入某一层就要**重新扫描**那个目录，深一点的路径要点好几次、
    /// 每次都要等一遍磁盘。现在一次扫描就把整棵目录树建出来，进入任意一层直接从
    /// 这里取子项，立刻就能画出来，不再碰磁盘。
    /// </para>
    /// <para>
    /// ⚠️ 只为目录填充；文件项恒为空。为控制内存，只有**占空间的目录**才保留子项
    /// （0 字节目录本来也不画面积）。
    /// </para>
    /// </summary>
    public List<DiskEntry> Children { get; init; } = new();
}

public sealed class DiskScanResult
{
    public string Root { get; init; } = "";
    public List<DiskEntry> Children { get; init; } = new();
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }
    /// <summary>没有权限、读不了的目录数（界面上要说出来，否则数字对不上会让人以为漏算）。</summary>
    public int SkippedDirectories { get; init; }

    /// <summary>
    /// 扫描到了时间预算就停止，**结果是不完整的**。
    /// ⚠️ 用它的地方必须把这件事告诉用户/模型 —— 不标注的话，"总占用"看着像个准确数。
    /// </summary>
    public bool Truncated { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 磁盘占用扫描：把某个目录的**直接子项**各自递归求和。
///
/// <para>
/// ⛔ 性能是这一块的命门（需求评估里就写了）：全盘几十万个文件，
/// 用 <c>DirectoryInfo</c> 逐个 <c>GetFiles()</c> 会慢一倍以上。
/// 这里每一层只调一次 <see cref="DirectoryInfo.EnumerateFileSystemInfos()"/>，
/// 文件直接用枚举回来的 <c>Length</c>（不额外 stat），目录压栈继续。
/// </para>
/// <para>
/// ⛔ 必须跳过重解析点（junction / 符号链接）：<c>C:\Users\All Users</c> 这种指向别处，
/// 跟进去要么重复计数、要么无限递归。
/// </para>
/// </summary>
public static class DiskUsage
{
    public static IReadOnlyList<DriveInfo> FixedDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .ToList();

    /// <summary>
    /// 扫描 <paramref name="root"/> 的每个直接子项。
    /// </summary>
    /// <param name="onProgress">扫描进度（当前目录路径、已累计字节、已数文件数）。</param>
    /// <param name="budget">
    /// 时间预算。到点就停止递归，返回**已统计到的部分**并置 <see cref="DiskScanResult.Truncated"/>。
    /// ⚠️ 整盘扫描动辄上百万文件，靠"超时就整条失败"体验极差 —— 让用户/模型先拿到部分结果，
    /// 比等一分半然后什么也没有强。传 <c>null</c> 表示不设限（老行为）。
    /// </param>
    public static Task<DiskScanResult> ScanAsync(
        string root,
        CancellationToken token,
        Action<string, long, int>? onProgress = null,
        TimeSpan? budget = null)
    {
        return Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var children = new List<DiskEntry>();
            var skipped = 0;
            long total = 0;
            var files = 0;
            var truncated = false;

            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists) throw new DirectoryNotFoundException(root);

            foreach (var child in SafeEntries(rootInfo, ref skipped))
            {
                token.ThrowIfCancellationRequested();

                if (child is FileInfo file)
                {
                    children.Add(new DiskEntry(file.Name, file.FullName, false, file.Length, 1));
                    total += file.Length;
                    files++;
                    continue;
                }

                if (child is not DirectoryInfo dir) continue;

                if (IsReparse(dir)) continue;   // junction：跳过，否则重复计数

                var node = MeasureNode(dir, token, ref skipped, watch, onProgress, budget, depth: 0,
                    out var cut);
                children.Add(node);
                total += node.Bytes;
                files += node.FileCount;

                if (cut) truncated = true;
            }

            children.RemoveAll(c => c.Bytes <= 0);   // 0 字节的东西在树图上没有面积，画不出来
            children.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            watch.Stop();

            return new DiskScanResult
            {
                Root = root,
                Children = children,
                TotalBytes = total,
                FileCount = files,
                SkippedDirectories = skipped,
                Truncated = truncated,
                Elapsed = watch.Elapsed,
            };
        }, token);
    }

    /// <summary>
    /// 递归求一个目录的大小，**同时把子项建出来**（自上游 dv1.2.1 移植，2026-11-14）。
    ///
    /// <para>
    /// 以前进入子目录要重新扫一遍磁盘；现在树在第一次扫描时就建好了，
    /// 进任意一层直接取 <see cref="DiskEntry.Children"/>，立刻能画。
    /// </para>
    /// <para>
    /// ⚠️ 用递归而不是显式栈：树形结果必须知道自己属于哪一层，显式栈也能做但要维护
    /// 一个映射表，反而更绕。深度上限 <see cref="MaxDepth"/> 兜住深目录爆栈的风险 ——
    /// 超过上限的层只统计字节数、不再展开子项（那些层用户也很少真的点进去）。
    /// </para>
    /// <para>
    /// ⚠️ 为控制内存，0 字节的子项不保留（树图上本来也没有面积），
    /// 也只在目录上挂子项 —— 文件项恒为空。
    /// </para>
    /// </summary>
    private static DiskEntry MeasureNode(
        DirectoryInfo dir,
        CancellationToken token,
        ref int skipped,
        Stopwatch watch,
        Action<string, long, int>? onProgress,
        TimeSpan? budget,
        int depth,
        out bool truncated)
    {
        truncated = false;

        long bytes = 0;
        var files = 0;
        var kids = new List<DiskEntry>();
        var localSkipped = 0;

        token.ThrowIfCancellationRequested();

        // 预算到点就停：把已统计的部分交出去，由调用方标 Truncated
        if (budget is { } limit && watch.Elapsed >= limit)
        {
            truncated = true;
            skipped += localSkipped;
            return new DiskEntry(dir.Name, dir.FullName, true, 0, 0);
        }

        if (onProgress is not null) onProgress(dir.FullName, bytes, files);

        foreach (var entry in SafeEntries(dir, ref localSkipped))
        {
            token.ThrowIfCancellationRequested();

            if (entry is FileInfo file)
            {
                bytes += file.Length;
                files++;
                continue;
            }

            if (entry is not DirectoryInfo sub) continue;
            if (IsReparse(sub)) continue;        // junction：跳过，否则重复计数

            if (depth + 1 >= MaxDepth)
            {
                // 到底了：只量大小，不再挂子项
                var (b, c, cut) = Measure(sub, token, ref localSkipped, watch, onProgress, budget);
                bytes += b;
                files += c;
                if (cut) truncated = true;
                continue;
            }

            var child = MeasureNode(sub, token, ref localSkipped, watch, onProgress, budget, depth + 1,
                out var childCut);
            if (childCut) truncated = true;

            if (child.Bytes > 0) kids.Add(child);
            bytes += child.Bytes;
            files += child.FileCount;
        }

        skipped += localSkipped;

        kids.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        return new DiskEntry(dir.Name, dir.FullName, true, bytes, files) { Children = kids };
    }

    /// <summary>
    /// 树的最大展开层数。超过这个深度的只统计大小、不建子项 ——
    /// 深目录多为缓存/依赖树，用户很少真的要点到第十层去看。
    /// </summary>
    private const int MaxDepth = 12;

    /// <summary>递归求一个目录的大小（显式栈，不用递归调用以防深目录爆栈）。</summary>
    private static (long Bytes, int Files, bool Truncated) Measure(
        DirectoryInfo start,
        CancellationToken token,
        ref int skipped,
        Stopwatch watch,
        Action<string, long, int>? onProgress,
        TimeSpan? budget)
    {
        long bytes = 0;
        var files = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(start);

        var lastReport = 0L;
        var visited = 0;
        var localSkipped = 0;
        var truncated = false;

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            visited++;

            // 每 64 个目录看一次取消/进度/预算，太频繁反而拖慢
            if ((visited & 0x3F) == 0)
            {
                token.ThrowIfCancellationRequested();

                var now = watch.ElapsedMilliseconds;
                if (onProgress is not null && now - lastReport > 250)
                {
                    lastReport = now;
                    onProgress(dir.FullName, bytes, files);
                }

                // 到预算就停，把已统计的部分交出去 —— 注意这是**完整目录量之和**，
                // 中途停下会让"总占用"偏小，所以必须置 Truncated 让调用方标注。
                if (budget is { } limit && watch.Elapsed >= limit)
                {
                    truncated = true;
                    break;
                }
            }

            foreach (var entry in SafeEntries(dir, ref localSkipped))
            {
                if (entry is FileInfo file)
                {
                    bytes += file.Length;
                    files++;
                    continue;
                }

                if (entry is DirectoryInfo sub && !IsReparse(sub))
                    stack.Push(sub);
            }
        }

        skipped += localSkipped;
        return (bytes, files, truncated);
    }

    /// <summary>
    /// 枚举一层，出异常（无权限、设备未就绪）就当这一层读不到：
    /// 记一笔跳过，不让整次扫描崩掉。
    /// </summary>
    private static IEnumerable<FileSystemInfo> SafeEntries(DirectoryInfo dir, ref int skipped)
    {
        FileSystemInfo[] items;
        try
        {
            items = dir.GetFileSystemInfos();
        }
        catch
        {
            skipped++;
            return Array.Empty<FileSystemInfo>();
        }

        return items;
    }

    private static bool IsReparse(DirectoryInfo dir)
    {
        try
        {
            return (dir.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;   // 读不到属性就当它是，别跟进去
        }
    }

    /// <summary>把字节数写成 "12.3 GB" 这种（磁盘容量按 1024 进制，和资源管理器一致）。</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
