using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>updates 目录里的一个安装包（给设置页列表用的只读视图）。</summary>
public sealed record InstallerFileInfo(
    string Name,
    string FullPath,
    long Length,
    DateTimeOffset ModifiedUtc,
    bool HasMd5File);

/// <summary>
/// 更新安装包自动清理：更新流程把安装包统一下到 <c>%LOCALAPPDATA%\ClassSoftwareHub\updates</c>，
/// 一版一个、越攒越大。这里在启动时把「保留数量」（<see cref="AppSettings.InstallerKeepCount"/>，默认 3）
/// 之外的旧包删掉 —— 留下的正好给「版本记录 → 装回旧版本」用：本地已有同名包且校验通过就不再下载（见 UpdateFlow）。
///
/// ⛔ 安全边界（这条绝不能松）：
///  - 只碰 <c>updates</c> 目录下的 <c>*.exe</c> / <c>*.msi</c> 及其 <c>.md5</c> 校验文件，
///    外加**超过 2 小时没人写过的 <c>*.part</c> 下载残片**，其余文件一律不碰；
///  - 只在自家 <c>updates</c> 目录里动手，绝不波及系统「下载」文件夹；
///  - 文件被占用（安装程序还在跑 / 下载正在进行中）删不掉就跳过，下次启动再试；
///  - 每一步的异常都落日志（<see cref="ScreenCapture.Log"/>），禁止空 catch。
/// </summary>
public static class InstallerCleanup
{
    /// <summary>
    /// 认哪些文件是"安装包"。
    ///
    /// ⚠️ 原来是 <c>^ClassSoftwareHub-Setup-.+\.exe$</c> —— 与下载侧的白名单
    ///    （<c>GitHubReleaseSource.InstallerExts</c> = .exe / .msi，认**任意**名字）对不上：
    ///    名字稍有不同的安装包（<c>csh-setup-1.1.0.exe</c>、<c>...-Setup-dv1.1.0.msi</c>）
    ///    对 Scan / Clean **永久不可见** → 「保留最近 N 个」静默失效、目录无限增长，
    ///    设置页的本地安装包列表也不完整，用户用不了文档里宣称的"本地覆盖安装"。
    ///
    /// 现在认本目录下任何 <c>.exe</c> / <c>.msi</c>。安全边界靠**目录本身**保证：
    /// 这里只可能是 <c>UpdateService.UpdatesDir</c>（应用私有目录），绝不会波及系统的"下载"文件夹。
    /// <c>*.exe.part</c> / <c>*.md5</c> 不会匹配上（正则以 <c>.exe|.msi</c> 结尾），残片另有清理路径。
    /// </summary>
    private static readonly Regex InstallerPattern =
        new(@"^.+\.(exe|msi)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 安装包的同名校验文件到底叫什么 —— **发版脚本是"追加"**：
    /// <c>tools/publish-release-win7.mjs</c> 里是 <c>path + '.md5'</c> → <c>X.exe.md5</c>。
    ///
    /// ⚠️ 这里原来用 <c>Path.ChangeExtension(..., ".md5")</c>（"**替换**"扩展名）得到 <c>X.md5</c>，
    ///    两边根本对不上，后果有两条：
    ///    ① <see cref="Scan"/> 的 <c>HasMd5File</c> 恒为 false —— 界面上"带校验文件"的标记永远不亮；
    ///    ② <see cref="Clean"/> 的孤儿判定算出 <c>X.exe.exe</c>（把 <c>X.exe.md5</c> 的 ".md5"
    ///       换成 ".exe"）→ 永远找不到 → **把仍然有效的 X.exe.md5 当孤儿删掉**。
    ///    现在以"追加"为准，同时兼容老的"替换"命名。
    /// </summary>
    private static string SidecarPath(string packagePath) => packagePath + ".md5";

    /// <summary>找这个安装包的校验文件：先认发版脚本的"追加"命名，再兼容老的"替换"命名。没有则返回 null。</summary>
    private static string? FindSidecar(string packagePath)
    {
        var appended = SidecarPath(packagePath);
        if (File.Exists(appended)) return appended;
        var replaced = Path.ChangeExtension(packagePath, ".md5");
        return File.Exists(replaced) ? replaced : null;
    }

    /// <summary>这个 .md5 对应的安装包还在不在（两种命名都认）。</summary>
    private static bool SidecarOwnerExists(string md5FileName, string dir)
    {
        var owner = md5FileName[..^".md5".Length];                 // x.exe.md5 → x.exe ／ x.md5 → x
        if (owner.Length == 0) return false;
        if (File.Exists(Path.Combine(dir, owner))) return true;    // 追加命名（发版脚本用的就是这个）

        var exe = Path.ChangeExtension(owner, ".exe");             // x → x.exe（老的替换命名）
        if (File.Exists(Path.Combine(dir, exe))) return true;
        var msi = Path.ChangeExtension(owner, ".msi");
        return File.Exists(Path.Combine(dir, msi));
    }

    /// <summary>安装包存放目录（与更新流程同源：UpdateService.UpdatesDir）。</summary>
    public static string InstallerDirectory => UpdateService.UpdatesDir;

    /// <summary>
    /// 列目录，枚举器自身抛异常（目录在遍历期间被删掉）也不让它逃出 Scan/Clean。
    /// ⚠️ 不能写成 <c>yield return</c> 的迭代器 —— C# 不允许在带 catch 的 try 里 yield。
    /// </summary>
    private static List<string> EnumerateQuiet(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).ToList();
        }
        catch (Exception ex)
        {
            ScreenCapture.Log($"[cleanup] 枚举安装包目录失败（{dir}）: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>列出本地安装包，新的在前。目录不存在时返回空表。</summary>
    public static IReadOnlyList<InstallerFileInfo> Scan()
    {
        var dir = InstallerDirectory;
        if (!Directory.Exists(dir)) return Array.Empty<InstallerFileInfo>();

        var list = new List<InstallerFileInfo>();
        foreach (var path in EnumerateQuiet(dir))
        {
            var name = Path.GetFileName(path);
            if (!InstallerPattern.IsMatch(name)) continue;
            try
            {
                var fi = new FileInfo(path);
                list.Add(new InstallerFileInfo(
                    name, path, fi.Length,
                    new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                    FindSidecar(path) is not null));
            }
            catch (Exception ex)
            {
                ScreenCapture.Log($"[cleanup] 读取安装包信息失败（{name}）: {ex.Message}");
            }
        }

        return list.OrderByDescending(f => f.ModifiedUtc).ToList();
    }

    /// <summary>
    /// 按保留数量清理：按修改时间从新到旧留 <paramref name="keep"/> 个，其余删除（连带同名 .md5）。
    /// 顺手清掉「安装包已不在、只剩 .md5」的孤儿校验文件，以及**超过 2 小时没人写过的下载残片**。
    /// 返回删除的安装包个数。
    /// </summary>
    public static int Clean(int keep)
    {
        keep = Math.Clamp(keep, 1, 10);
        var dir = InstallerDirectory;
        if (!Directory.Exists(dir)) return 0;

        var all = EnumerateQuiet(dir);

        // ⚠️ 这里原来是 .Select( new FileInfo ) 后直接 OrderByDescending(fi => fi.LastWriteTimeUtc)：
        //    FileInfo 的属性是惰性读入并缓存的，文件在「枚举」与「排序」之间被删掉
        //    （本目录正被下载流程与 Clean 同时使用）就会抛 FileNotFoundException，整轮清理失败。
        var files = all
            .Where(p => InstallerPattern.IsMatch(Path.GetFileName(p)))
            .Select(p => (Path: p, WriteUtc: SafeWriteUtc(p)))
            .OrderByDescending(f => f.WriteUtc)
            .ToList();

        var removed = 0;
        for (var i = keep; i < files.Count; i++)
            if (TryDeleteWithMd5(files[i].Path)) removed++;

        // 孤儿校验文件：对应的安装包已经不在了，校验文件留着只会让人困惑。
        // ⚠️ 认两种命名（追加 X.exe.md5 ／ 老的 X.md5），别把仍然有效的那份当孤儿删了。
        foreach (var path in all)
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase)) continue;
            if (SidecarOwnerExists(name, dir)) continue;
            TryDelete(path);
        }

        CleanStaleParts(all);

        return removed;
    }

    private static DateTime SafeWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// 半截的下载残片（<c>*.part</c>）：更新下载失败 / 被取消时留下的东西。
    ///
    /// ⚠️ 上面的 <see cref="InstallerPattern"/> 正则匹配不到它们，而它们**以前没有任何回收路径**
    ///    —— 反复几次失败的更新就能在 %LOCALAPPDATA% 里堆出上百 MB，教室机的系统盘通常很小，
    ///    %LOCALAPPDATA% 满了会连带设置、内容缓存和整个应用一起完蛋。
    ///    ⚠️ 只清「超过 2 小时没人写过」的：正在下载的那一份绝不能碰。
    /// </summary>
    private static void CleanStaleParts(IEnumerable<string> all)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(2);
        foreach (var path in all)
        {
            if (!path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(path) > cutoff) continue;   // 还在写
                File.Delete(path);
            }
            catch (Exception ex)
            {
                ScreenCapture.Log($"[cleanup] 删除下载残片失败（{Path.GetFileName(path)}）: {ex.Message}");
            }
        }
    }

    /// <summary>删除安装包及其校验文件。单个文件失败只记日志、不打断整轮清理。</summary>
    public static bool TryDeleteWithMd5(string packagePath)
    {
        var ok = TryDelete(packagePath);
        // 追加命名（发版脚本用的）与老的替换命名都要收掉
        var appended = SidecarPath(packagePath);
        if (File.Exists(appended)) TryDelete(appended);
        var replaced = Path.ChangeExtension(packagePath, ".md5");
        if (!string.Equals(replaced, appended, StringComparison.OrdinalIgnoreCase) && File.Exists(replaced))
            TryDelete(replaced);
        return ok;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            // 被占用（安装程序正在运行）属正常情况：跳过，下次启动再试
            ScreenCapture.Log($"[cleanup] 删除失败（{Path.GetFileName(path)}）: {ex.Message}");
            return false;
        }
    }

    /// <summary>应用启动时挂后台清一轮（不阻塞首帧；新版本装完后的第一次启动会把旧包收掉）。</summary>
    public static void Start()
    {
        // ⚠️ 设置必须在**调用线程**（UI 线程，App.OnFrameworkInitializationCompleted）上读好再传进去：
        //    原来是在 Task.Run 里读 App.Settings.Current.InstallerKeepCount，与 UI 线程的初始化并发、
        //    没有任何同步 —— 撕裂读 / null 会被下面的 catch 吞成"本次会话静默跳过清理"，故障不可见。
        int keep;
        try
        {
            keep = App.Settings.Current.InstallerKeepCount;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log("[cleanup] 读取「安装包保留数量」失败，按默认值 3 处理: " + ex.Message);
            keep = 3;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var removed = Clean(keep);
                if (removed > 0)
                    ScreenCapture.Log($"[cleanup] 已清理 {removed} 个旧安装包（保留最近 {Math.Clamp(keep, 1, 10)} 个）");
            }
            catch (Exception ex)
            {
                ScreenCapture.Log("[cleanup] 安装包清理失败: " + ex.Message);
            }
        });
    }
}
