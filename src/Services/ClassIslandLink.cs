using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一次探测出来的 ClassIsland 现状。字段都取自磁盘 / 进程，不缓存。</summary>
public sealed class CiInstallInfo
{
    public string Root { get; init; } = "";
    public string DataRoot { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Running { get; init; }
    public string PluginDir { get; init; } = "";
    public string ConfigDir { get; init; } = "";
    public string PluginVersion { get; init; } = "";

    /// <summary>
    /// 这一代 ClassIsland 要用的插件 id：2.x 是 <c>ClassSlideSync.Plugin</c>，
    /// **1.x 是 <c>ClassSlideSync.Plugin.Legacy</c>**（插件 API 是上一代，2.x 的包装不进 1.x，
    /// 反之亦然。1.x 那份的插件配置目录在 %APPDATA%，见 <see cref="ConfigDir"/>）。
    /// </summary>
    public string PluginId { get; init; } = ClassIslandLink.PluginId;

    /// <summary>是不是 ClassIsland 1.x 那一代。</summary>
    public bool Legacy => string.Equals(PluginId, ClassIslandLink.LegacyPluginId, StringComparison.OrdinalIgnoreCase) && PluginId.Length > 0;

    public bool Found => Root.Length > 0;

    /// <summary>这一代插件的主 dll 文件名（1.x 叫 ClassSlideSync.Plugin.Legacy.dll）。</summary>
    public string PluginDll => PluginId + ".dll";

    public bool PluginInstalled =>
        PluginVersion.Length > 0 || (PluginDir.Length > 0 && File.Exists(Path.Combine(PluginDir, PluginDll)));

    /// <summary>ClassIsland 2.2 起插件要求 .NET 10（只有 2.x 这一代有 net10 包）。</summary>
    public bool UsesNet10
    {
        get
        {
            if (Legacy) return false;
            var v = ClassIslandLink.ParseVersion(Version);
            return v != null && (v.Major > 2 || (v.Major == 2 && v.Minor >= 2));
        }
    }
}

/// <summary>
/// ClassIsland 联动：找安装位置、往它里面装「课件续页」插件，以及让 CSH 的课件库
/// 和插件共用同一份台账。
///
/// <para>
/// ── 目录约定（都是 ClassIsland 自己的，不是 CSH 的）──────────────
/// 便携版：<c>&lt;root&gt;\ClassIsland.exe</c>；2.x 的数据在 <c>&lt;root&gt;\data\</c>
/// （1.x 直接在 root 下）。插件本体装到 <c>&lt;data&gt;\Plugins\ClassSlideSync.Plugin</c>，
/// 插件的台账 / 设置 / 停留时间在 <c>&lt;data&gt;\Config\Plugins\ClassSlideSync.Plugin</c>。
/// 两处必须分开：装插件那层升级时会被整目录替换，台账放进去就会丢（Ledger 自己的注释也这么说）。
/// </para>
///
/// <para>
/// ── 为什么要有「共用台账」开关 ──────────────────────────────
/// 插件没有对外 IPC / HTTP 接口，CSH 没法直接叫它做事。能共享的是文件：两边的 Ledger
/// 本来就是同一份代码（见 NOTICE.md），文件锁与跨进程替换都做过了。开关打开后，
/// CSH 的引擎读写的 ledger.json 直接落在插件配置目录里 —— 老师在 ClassIsland 那边记的进度，
/// CSH 课件库立刻能看到，反过来也一样。设置和外观仍然各用各的，避免两边互相覆盖。
/// </para>
///
/// <para>
/// ── Win7 ───────────────────────────────────────────────────
/// 不碰新 API：解包用 <see cref="ZipFile"/>，下载走 <see cref="HttpTls"/>（显式 TLS 1.2）。
/// 在线安装失败时，页面上永远留着「从安装包安装…」这条路。
/// </para>
/// </summary>
public static class ClassIslandLink
{
    public const string PluginId = "ClassSlideSync.Plugin";

    /// <summary>
    /// ClassIsland **1.x 那一代**的插件 id：1.x 的插件 API 是上一代（PluginSdk 1.x），
    /// 2.x 的 cipx 装进去也不会被加载，所以那边一直是一个独立的插件（同样是我们自己的）。
    /// 它的配置目录在 <c>%APPDATA%\ClassSlideSync.Plugin.Legacy</c>（1.x SDK 给的位置），
    /// 不在 CI 的 data\Config 下面。</summary>
    public const string LegacyPluginId = "ClassSlideSync.Plugin.Legacy";

    public const string PluginName = "课件续页";

    private const string StandardAsset = "ClassSlideSync.Plugin.cipx";
    private const string Net10Asset = "ClassSlideSync.Plugin.net10.cipx";
    private const string LegacyAsset = "ClassSlideSync.Plugin.Legacy.cipx";
    private const string LatestBase = "https://github.com/KKT446/ClassSlideSync/releases/latest/download/";

    private static readonly object Gate = new();
    private static LinkState? _state;

    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassSoftwareHub");

    private static string StatePath => Path.Combine(BaseDir, "classisland.json");

    // ── 记住的位置 / 开关 ──────────────────────────────────────────────

    private sealed class LinkState
    {
        public string CiRoot { get; set; } = "";
        public bool ShareLedger { get; set; }
    }

    private static LinkState State
    {
        get
        {
            lock (Gate)
            {
                if (_state != null) return _state;
                try
                {
                    if (File.Exists(StatePath))
                        _state = JsonSerializer.Deserialize<LinkState>(File.ReadAllText(StatePath)) ?? new LinkState();
                    else
                        _state = new LinkState();
                }
                catch (Exception ex)
                {
                    AppLog.Catch("classisland", "读取联动配置", ex, LogLevel.Debug);
                    _state = new LinkState();
                }
                return _state;
            }
        }
    }

    private static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Catch("classisland", "保存联动配置", ex, LogLevel.Debug);
        }
    }

    /// <summary>记住的 ClassIsland 根目录（没记住 / 失效时为空）。</summary>
    public static string SavedRoot { get { lock (Gate) return State.CiRoot; } }

    /// <summary>CSH 课件库是否和插件共用台账。改完需要重启 CSH。</summary>
    public static bool ShareLedger
    {
        get { lock (Gate) return State.ShareLedger; }
        set
        {
            lock (Gate) State.ShareLedger = value;
            SaveState();
        }
    }

    /// <summary>
    /// 启动时给 Ledger 用的目录：没开共用、没找到 ClassIsland、插件没装，都返回空，
    /// 调用方退回 CSH 自己的目录。只在启动时读一次（运行中切换要重启）。
    /// </summary>
    public static string? SharedLedgerDir()
    {
        if (!ShareLedger) return null;
        var ci = Detect();
        if (!ci.PluginInstalled || ci.ConfigDir.Length == 0) return null;

        // 目录得**可写**才算能共用：CI 装在 Program Files 时 data\Config 普通用户写不进去，
        // 硬指过去就是"共用开关打开了、台账却一直在报只读"，比不共用更迷惑。
        if (!CanWriteTo(ci.ConfigDir))
        {
            AppLog.Warn("classisland",
                "共用台账已关闭使用：插件配置目录不可写（" + ci.ConfigDir + "），本次仍用 CSH 自己的记录目录");
            return null;
        }
        AppLog.Info("classisland",
            "共用台账｜代=" + (ci.Legacy ? "1.x(Legacy)" : "2.x") + "｜目录=" + ci.ConfigDir);
        return ci.ConfigDir;
    }

    /// <summary>探测目录可写（不存在就先建）。只做一次轻量写删，别去碰台账文件本体。</summary>
    private static bool CanWriteTo(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".csh-write-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── 探测 ──────────────────────────────────────────────────────────

    public static CiInstallInfo Detect()
    {
        var running = IsRunning();
        var root = PickRoot();
        if (root.Length == 0)
            return new CiInstallInfo { Running = running };

        var dataRoot = ResolveDataRoot(root);
        var version = ReadCiVersion(root);
        var legacy = IsLegacyGeneration(version, root, dataRoot);
        var pluginId = legacy ? LegacyPluginId : PluginId;

        var pluginDir = Path.Combine(dataRoot, "Plugins", pluginId);
        // 1.x SDK 给的插件配置目录是 %APPDATA%\<插件 id>；2.x 在 data\Config\Plugins\<插件 id>。
        var configDir = legacy
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), pluginId)
            : Path.Combine(dataRoot, "Config", "Plugins", pluginId);

        LogDetectOnce(root, version, legacy, pluginId, pluginDir, configDir);

        return new CiInstallInfo
        {
            Root = root,
            DataRoot = dataRoot,
            Version = version,
            Running = running,
            PluginId = pluginId,
            PluginDir = pluginDir,
            ConfigDir = configDir,
            PluginVersion = ReadManifestVersion(pluginDir),
        };
    }

    /// <summary>
    /// 判定该用哪一代插件。
    /// <para>
    /// ⚠️ 2026-11-28 修正：**1.6 起 ClassIsland 就已经是 .NET 8 + Avalonia 11.x**，
    /// 插件 API 版本（manifest 里的 <c>apiVersion: 2.0.0.0</c>）从我们 1.0.1.0 起就没变过 ——
    /// 也就是说 1.6/1.7 直接装 GitHub 上那份默认包就行，不需要单独的 Legacy 包。
    /// 只有 **1.5 及更早**（net6/net7 时代、插件 API 更老）才需要 Legacy 那份。
    /// </para>
    /// <para>
    /// 版本读不到时：先看盘上装的到底是哪个 id 的插件目录，再拿它当证据；都没有就按"现代"处理
    /// （宁可用默认包被试装拒绝，也别把 Legacy 包装进 2.x）。
    /// </para>
    /// </summary>
    private static bool IsLegacyGeneration(string version, string root, string dataRoot)
    {
        var v = ParseVersion(version);
        if (v != null)
        {
            if (v.Major >= 2) return false;
            return v.Minor < 6;                       // 1.6 起与 2.x 同一代运行时/插件 API
        }

        try
        {
            if (Directory.Exists(Path.Combine(dataRoot, "Plugins", LegacyPluginId))) return true;
            if (Directory.Exists(Path.Combine(dataRoot, "Plugins", PluginId))) return false;
        }
        catch { }
        return false;                                  // 兜底：按现代处理
    }

    private static string _lastDetectSignature = "";

    /// <summary>同一次会话里只在"根/版本/代"变化时打一条，排障要看代和两条目录。</summary>
    private static void LogDetectOnce(string root, string version, bool legacy, string pluginId,
        string pluginDir, string configDir)
    {
        try
        {
            var sig = root + "|" + version + "|" + legacy;
            if (string.Equals(sig, _lastDetectSignature, StringComparison.Ordinal)) return;
            _lastDetectSignature = sig;
            var probe = new CiInstallInfo { Version = version, PluginId = pluginId };
            AppLog.Info("classisland",
                "探测｜根=" + root + "｜版本=" + (string.IsNullOrWhiteSpace(version) ? "未知" : version)
                + "｜代=" + (legacy ? "1.x(Legacy)" : "2.x")
                + "｜安装包=" + AssetName(probe)
                + "｜插件目录=" + pluginDir + "｜配置目录=" + configDir);
        }
        catch { }
    }

    /// <summary>用户手动指定位置；接受 ClassIsland 根目录、data 目录或 exe 所在的目录。</summary>
    public static bool SetRoot(string path, out string message)
    {
        var root = NormalizeRoot(path);
        if (root.Length == 0 || !IsRoot(root))
        {
            message = "这个文件夹里没有 ClassIsland.exe，也没有 Config / Plugins 数据目录。";
            return false;
        }

        lock (Gate) State.CiRoot = root;
        SaveState();
        message = "已记住 ClassIsland 位置。";
        return true;
    }

    public static bool IsRoot(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        if (File.Exists(Path.Combine(dir, "ClassIsland.exe"))) return true;
        if (Directory.Exists(Path.Combine(dir, "Config")) && Directory.Exists(Path.Combine(dir, "Plugins"))) return true;

        var data = Path.Combine(dir, "data");
        return Directory.Exists(Path.Combine(data, "Config")) || Directory.Exists(Path.Combine(data, "Plugins"));
    }

    private static string PickRoot()
    {
        var saved = SavedRoot;
        if (IsRoot(saved)) return saved;

        var running = RunningRoot();
        if (IsRoot(running)) return running;

        foreach (var guess in Guesses())
        {
            if (IsRoot(guess)) return guess;
        }
        return "";
    }

    private static string NormalizeRoot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var dir = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(dir)) dir = Path.GetDirectoryName(dir) ?? "";

            // 选到 data / Config / Plugins 层时往上退到带 exe 的根。
            if (IsRoot(dir)) return dir;
            for (var i = 0; i < 3; i++)
            {
                var parent = Directory.GetParent(dir)?.FullName ?? "";
                if (parent.Length == 0) break;
                if (IsRoot(parent)) return parent;
                dir = parent;
            }
            return path;
        }
        catch
        {
            return path;
        }
    }

    private static string ResolveDataRoot(string root)
    {
        var data = Path.Combine(root, "data");
        if (Directory.Exists(Path.Combine(data, "Config")) || Directory.Exists(Path.Combine(data, "Plugins")))
            return data;
        return root;
    }

    private static IEnumerable<string> Guesses()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        yield return Path.Combine(local, "Programs", "ClassIsland");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClassIsland");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ClassIsland");
        yield return Path.Combine(desktop, "ClassIsland");
        yield return Path.Combine(downloads, "ClassIsland");

        // 桌面上常放的是「ClassIsland-2.1.0」这种带版本号的文件夹，扫一层就够，不递归。
        // yield 不能写在带 catch 的 try 里，所以先把命中的目录收进列表。
        List<string> desktopHits = new();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(desktop))
            {
                var name = Path.GetFileName(dir);
                if (name.IndexOf("classisland", StringComparison.OrdinalIgnoreCase) >= 0)
                    desktopHits.Add(dir);
            }
        }
        catch { }
        foreach (var dir in desktopHits) yield return dir;
    }

    private static bool IsRunning()
    {
        try { return Process.GetProcessesByName("ClassIsland").Length > 0; }
        catch { return false; }
    }

    private static string RunningRoot()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ClassIsland"))
            {
                try
                {
                    var file = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(file)) return Path.GetDirectoryName(file) ?? "";
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return "";
    }

    private static string ReadCiVersion(string root)
    {
        try
        {
            var exe = Path.Combine(root, "ClassIsland.exe");
            if (File.Exists(exe))
            {
                var v = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }

            // 更新器布局：根目录只有启动器，真正的版本号在 app-2.1.0.1-0 这种目录名里。
            foreach (var dir in Directory.EnumerateDirectories(root, "app-*"))
            {
                var v = Path.GetFileName(dir).Substring(4);
                var dash = v.IndexOf('-');
                if (dash > 0) v = v.Substring(0, dash);
                if (ParseVersion(v) != null) return v;
            }
        }
        catch { }
        return "";
    }

    internal static Version? ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash > 0) s = s.Substring(0, dash);
        return Version.TryParse(s, out var v) ? v : null;
    }

    private static string ReadManifestVersion(string pluginDir)
    {
        try
        {
            var file = Path.Combine(pluginDir, "manifest.yml");
            if (!File.Exists(file)) return "";
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (!line.StartsWith("version:", StringComparison.OrdinalIgnoreCase)) continue;
                return line.Substring("version:".Length).Trim().Trim('"', '\'');
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 读 manifest 里的插件 id（精确那一行）。⚠️ 不能用"包含 id 子串"来判断 ——
    /// <c>id: ClassSlideSync.Plugin.Legacy</c> 也含 <c>id: ClassSlideSync.Plugin</c>，
    /// 子串判断会让 1.x 与 2.x 的包互相认错。
    /// </summary>
    private static string ReadManifestId(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return "";
            foreach (var raw in File.ReadAllLines(manifestPath))
            {
                var line = raw.Trim();
                if (!line.StartsWith("id:", StringComparison.OrdinalIgnoreCase)) continue;
                return line.Substring("id:".Length).Trim().Trim('"', '\'');
            }
        }
        catch { }
        return "";
    }

    // ── 安装 ──────────────────────────────────────────────────────────

    public static string AssetName(bool net10) => net10 ? Net10Asset : StandardAsset;

    /// <summary>
    /// 该装哪个安装包：1.x → Legacy 包；2.2+ → net10 包；其余 2.x → 标准包。
    /// ⚠️ 认错一代就是"装完仍显示未安装"，所以在线安装这条路只吃 <see cref="CiInstallInfo"/>。
    /// </summary>
    public static string AssetName(CiInstallInfo ci)
        => ci.Legacy ? LegacyAsset : (ci.UsesNet10 ? Net10Asset : StandardAsset);

    /// <summary>从 GitHub 的 latest release 下载对应安装包，返回临时文件路径（调用方负责删）。</summary>
    public static async Task<string> DownloadLatestAsync(CiInstallInfo ci, Action<string>? progress,
                                                         CancellationToken ct = default)
    {
        var asset = AssetName(ci);
        var url = LatestBase + asset;
        var dest = Path.Combine(Path.GetTempPath(), "csh-" + asset);

        progress?.Invoke("正在连接 GitHub…");
        using var http = new HttpClient(HttpTls.Handler()) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClassSoftwareHub");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var hint = ci.Legacy
                ? "ClassIsland 1.x 要装「课件续页 1.x（Legacy）」那份 .cipx；仓库目前还没发布它，请用「从安装包…」手动选那个文件。"
                : "可以在浏览器里下载安装包，再用「从安装包安装…」。";
            throw new InvalidOperationException("下载 " + asset + " 失败（HTTP " + (int)resp.StatusCode + "）。" + hint);
        }

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await dst.WriteAsync(buffer, 0, n, ct);
            read += n;
            if (total > 0) progress?.Invoke("正在下载 " + (read * 100 / total) + "%");
        }
        return dest;
    }

    /// <summary>
    /// 把 .cipx 解到插件安装目录。ClassIsland 必须已退出（dll 被占用时解不进去）。
    /// 旧的主 dll 会改名成 .old-HHmmss.dll，和插件自己升级时的做法一致，方便回退。
    /// </summary>
    public static string Install(string packagePath)
    {
        var ci = Detect();
        if (!ci.Found) throw new InvalidOperationException("还没找到 ClassIsland，先在上面选一下它的安装目录。");
        if (ci.Running) throw new InvalidOperationException("ClassIsland 正在运行，先退出再安装。");
        if (!File.Exists(packagePath)) throw new InvalidOperationException("安装包不存在：" + packagePath);

        var temp = Path.Combine(Path.GetTempPath(), "csh-cipx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            ZipFile.ExtractToDirectory(packagePath, temp);

            var manifest = Path.Combine(temp, "manifest.yml");
            var manifestId = ReadManifestId(manifest);
            if (manifestId.Length == 0)
                throw new InvalidOperationException("这个文件不是「" + PluginName + "」的安装包（manifest.yml 里没有 id）。");
            if (!string.Equals(manifestId, ci.PluginId, StringComparison.OrdinalIgnoreCase))
            {
                // 典型误装：把 2.x 的包装进 1.x、或把 Legacy 包装进 2.x —— 装进去也不会被宿主加载。
                var want = ci.Legacy ? "ClassIsland 1.x（Legacy）" : "ClassIsland 2.x";
                throw new InvalidOperationException(
                    "安装包不匹配：这份是 " + manifestId + "，而当前 " + want + " 需要 " + ci.PluginId
                    + " 的包。1.x 与 2.x 的插件 API 不是同一代，不能混装。");
            }

            Directory.CreateDirectory(ci.PluginDir);

            var mainDll = Path.Combine(ci.PluginDir, PluginId + ".dll");
            if (File.Exists(mainDll))
            {
                var backup = Path.Combine(ci.PluginDir, PluginId + ".old-" + DateTime.Now.ToString("HHmmss") + ".dll");
                try { File.Copy(mainDll, backup, true); } catch { }
            }

            CopyTree(temp, ci.PluginDir);
            var version = ReadManifestVersion(ci.PluginDir);
            AppLog.Info("classisland", "插件已安装｜目录=" + ci.PluginDir + "｜版本=" + (version.Length > 0 ? version : "未知"));
            return version.Length > 0 ? version : "未知版本";
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("安装包不是有效的 .cipx（zip）文件，可能没下载完整。");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = file.Substring(from.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? to);
            File.Copy(file, dest, true);
        }
    }
}