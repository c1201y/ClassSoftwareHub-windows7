using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>缓存里的一个「版本实例」目录（给设置页列表用的只读视图）。</summary>
public sealed record CacheInstanceInfo(
    string Name,
    string FullPath,
    DateTime CreatedLocal,
    long Bytes,
    int FileCount,
    bool IsCurrent);

/// <summary>
/// 版本化素材缓存目录：
/// <c>%LOCALAPPDATA%\ClassSoftwareHub\cache\dv{版本}-{yyyyMMddHHmm}\</c>，例如
/// <c>dv1.2.0-insider1.1-202610101842</c>。
///
/// 为什么要一个版本一个目录：所有版本的解包素材（图标 / 首页 banner / 引导图 / 铃声…）
/// 若堆在 <c>cache\</c> 根下、按文件名区分，会越攒越乱、也没法「按版本回收」。
/// 拆开之后：每个版本一份，删一个目录就是干净地回收一个版本。
///
/// 为什么目录名尾上还要跟日期时间：用户可能在版本间来回（装了新版又想装回旧版）。只按版本号命名的话，
/// 「同名目录」会在来回切换时互相覆盖；缀上「首次建立该目录的时间」就天然唯一，
/// 而且目录名按字符串排序 == 按时间排序，回收时直接取尾部时间即可。
///
/// 复用规则：启动时先找同版本已有的目录（有就复用最新的那个），**没有才新建** ——
/// 同一版本反复启动不会每次都开新目录，不会越攒越多。
///
/// ⛔ 安全边界（这条绝不松）：
///  - 只认名字匹配 <c>^dv(.+)-(\d{12})$</c> 的**目录**，其余一律不碰；
///  - 只在自家 <c>cache\</c> 目录里动手，删除前再校验一次「路径确实在 cache\ 之下」；
///  - **当前正在用的实例目录，任何情况下都不删**；
///  - 每一步异常都落日志，禁止空 catch。
/// </summary>
public static class CacheHome
{
    /// <summary>缓存根目录的文件夹名（数据根下）。</summary>
    public const string FolderName = "cache";

    /// <summary>实例目录名：<c>dv</c> + 版本号 + <c>-</c> + 12 位本地时间（yyyyMMddHHmm）。</summary>
    private static readonly Regex InstancePattern =
        new(@"^dv(.+)-(\d{12})$", RegexOptions.Compiled);

    private const string StampFormat = "yyyyMMddHHmm";

    private static readonly object Gate = new();
    private static string? _instanceName;

    // 初始化失败的可重试状态：瞬时抖动不再一次失败就整会话降级（见 Ensure）。
    private static int _initFailures;
    private static DateTime _initRetryAfterUtc;
    private const int MaxInitFailures = 3;
    private static readonly TimeSpan InitRetryBackoff = TimeSpan.FromSeconds(2);

    /// <summary>缓存根目录：<c>%LOCALAPPDATA%\ClassSoftwareHub\cache</c>。</summary>
    public static string Root => Path.Combine(SettingsStore.Dir, FolderName);

    /// <summary>当前版本实例目录名（如 <c>dv1.2.0-insider1.1-202610101842</c>）。取用即确保已初始化。</summary>
    public static string InstanceName => Ensure();

    /// <summary>当前版本实例目录的完整路径。初始化失败时退回 <c>cache\</c> 根（不至于让素材写不出去）。</summary>
    public static string InstanceDir
    {
        get
        {
            var name = Ensure();
            return name.Length == 0 ? Root : Path.Combine(Root, name);
        }
    }

    /// <summary>
    /// 幂等：已决定过就直接返回。否则<strong>先复用</strong>同版本已有的实例目录（取名字最大 = 时间最新），
    /// 没有才用当前时间新建一个。返回实例目录名（初始化失败返回空串）。
    /// </summary>
    public static string Ensure()
    {
        if (_instanceName is not null) return _instanceName;

        lock (Gate)
        {
            if (_instanceName is not null) return _instanceName;
            // 退避：启动期 Ensure 会被高频调用，瞬时失败后短时间内不再反复撞盘
            if (DateTime.UtcNow < _initRetryAfterUtc) return "";

            try
            {
                Directory.CreateDirectory(Root);

                // 同版本前缀：dv1.2.0-insider1.1-
                var prefix = "dv" + ShellConfig.ShellVersion + "-";

                // 目录名尾部就是 12 位时间 ⇒ 字符串倒序 == 时间倒序，取第一个即最新
                var reuse = Directory.EnumerateDirectories(Root)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null
                                && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                && InstancePattern.IsMatch(n))
                    .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                var name = reuse ?? prefix + DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture);
                Directory.CreateDirectory(Path.Combine(Root, name));
                _instanceName = name;
                _initFailures = 0;
            }
            catch (Exception ex)
            {
                _initFailures++;
                _initRetryAfterUtc = DateTime.UtcNow + InitRetryBackoff;
                if (_initFailures >= MaxInitFailures)
                {
                    // 连续失败才降级：整会话退回 cache 根（Clean 会因此拒绝动手）
                    _instanceName = "";
                    AppLog.Catch("cache", "初始化版本缓存目录（连续失败，已退回 cache 根）", ex);
                }
                else
                {
                    // 瞬时失败：保持 null + 退避计时，下次调用可重试，不再一次失败就整会话降级
                    AppLog.Catch("cache", $"初始化版本缓存目录（瞬时失败，稍后重试｜次数={_initFailures}）",
                        ex, Core.LogLevel.Debug);
                }
            }

            return _instanceName ?? "";
        }
    }

    /// <summary>列出 <c>cache\</c> 下所有版本实例目录，新的在前。</summary>
    public static IReadOnlyList<CacheInstanceInfo> Scan()
    {
        var root = Root;
        if (!Directory.Exists(root)) return Array.Empty<CacheInstanceInfo>();

        var current = Ensure();
        var list = new List<CacheInstanceInfo>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var m = InstancePattern.Match(name);
            if (!m.Success) continue;

            if (!DateTime.TryParseExact(m.Groups[2].Value, StampFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
                continue;

            long bytes = 0;
            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(file).Length; count++; }
                    catch { /* 单个文件读不到就跳过，不影响整目录统计 */ }
                }
            }
            catch (Exception ex)
            {
                AppLog.Catch("cache", $"统计缓存目录体积｜目录={name}", ex, Core.LogLevel.Debug);
            }

            list.Add(new CacheInstanceInfo(
                name, dir, created, bytes, count,
                string.Equals(name, current, StringComparison.OrdinalIgnoreCase)));
        }

        return list.OrderByDescending(x => x.CreatedLocal).ToList();
    }

    /// <summary>
    /// 按「保留数量」回收：保留最近 <paramref name="keep"/> 个版本实例目录，更早的删掉。
    /// 当前正在用的实例**永不删**（即便它排在保留名额之外）。返回删掉的目录个数。
    /// </summary>
    public static int Clean(int keep)
    {
        keep = Math.Clamp(keep, 1, 10);

        var current = Ensure();
        if (current.Length == 0) return 0;   // 目录都没建起来 ⇒ 不动手（安全第一）

        var removed = 0;
        var rank = 0;
        foreach (var inst in Scan())     // Scan 已按时间倒序
        {
            rank++;
            if (rank <= keep) continue;                       // 保留名额内
            if (inst.IsCurrent) continue;                     // 当前实例永不删
            if (!IsInsideRoot(inst.FullPath)) continue;       // 安全边界：必须在 cache\ 之下

            try
            {
                Directory.Delete(inst.FullPath, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                // 被占用（应用正开着、图片正被 BitmapImage 持有）属正常：跳过，下次启动再试
                AppLog.Catch("cache", $"删除过期缓存目录｜目录={inst.Name}", ex);
            }
        }

        return removed;
    }

    /// <summary>删掉指定的一个实例目录（设置页「删除」用）。当前实例、非法名字、越界路径一律拒绝。</summary>
    public static bool TryDelete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (!InstancePattern.IsMatch(name)) return false;
        if (string.Equals(name, Ensure(), StringComparison.OrdinalIgnoreCase)) return false;

        var path = Path.Combine(Root, name);
        if (!IsInsideRoot(path)) return false;

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Catch("cache", $"删除缓存目录｜目录={name}", ex);
            return false;
        }
    }

    /// <summary>当前全部实例目录合计占用（字节数与文件数）。</summary>
    public static (long Bytes, int Files) Measure()
    {
        long bytes = 0;
        var files = 0;
        foreach (var inst in Scan())
        {
            bytes += inst.Bytes;
            files += inst.FileCount;
        }
        return (bytes, files);
    }

    /// <summary>
    /// 应用启动时挂后台清一轮（不阻塞首帧）：按「缓存保留数量」回收更早的版本目录。
    /// </summary>
    public static void Start()
    {
        // ⚠️ 设置必须在**调用线程**（UI 线程）上读好再传进 Task.Run：与 UI 线程初始化并发
        //    读 App.Settings.Current 会撕裂读 / 读到 0，静默把用户设的「保留 N 个版本」
        //    降成 1，删掉本该留着的缓存目录（与 InstallerCleanup.Start 同一规矩）。
        int keep;
        try
        {
            keep = App.Settings.Current.CacheKeepCount;
        }
        catch (Exception ex)
        {
            AppLog.Catch("cache", "读取「缓存保留数量」（按默认值 3 处理）", ex, Core.LogLevel.Debug);
            keep = 3;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var removed = Clean(keep);
                if (removed > 0)
                    AppLog.Info("cache", $"清理过期缓存目录｜已删除={removed}｜保留={Math.Clamp(keep, 1, 10)} 个");
            }
            catch (Exception ex)
            {
                AppLog.Catch("cache", "清理过期缓存目录", ex);
            }
        });
    }

    /// <summary>目标路径确实落在 <c>cache\</c> 之下（删除前的最后一道闸）。</summary>
    private static bool IsInsideRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
