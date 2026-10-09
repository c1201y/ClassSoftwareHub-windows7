using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>下载下来的包校验不过。</summary>
public sealed class ChecksumMismatchException : Exception
{
    public ChecksumMismatchException(string algorithm, string expected, string actual)
        : base($"{algorithm} 校验不通过：期望 {expected}，实际 {actual}") { }
}

/// <summary>
/// 发布未提供任何可用校验值：既无 <c>.md5</c> 资产，接口也未返回 SHA256。
///
/// ⚠️ 界面承诺「校验通过后方可安装」。未提供校验值时必须中止更新，
///    绝不允许执行未校验的安装包。正常发布的版本均附带 <c>.md5</c>
///    （由发版脚本 <c>tools/publish-release-win7.mjs</c> 生成上传），不应触发此异常。
/// </summary>
public sealed class MissingChecksumException : Exception
{
    public MissingChecksumException(string message) : base(message) { }
}

/// <summary>下载完成后的结果（含实际算出来的校验值）。</summary>
public sealed record DownloadedPackage(string FilePath, string Md5, string Sha256, bool Verified, string VerifyNote);

/// <summary>
/// 更新流程编排：查 → 下 → 校验 → 装。
/// 只管"怎么更新"，不管"从哪儿拿"（那是 IUpdateSource 的事）。
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,   // 下载大文件不设总超时，靠 CancellationToken 控制
    };

    public IUpdateSource Source { get; }

    public UpdateService(IUpdateSource source) => Source = source;

    /// <summary>建一个默认更新源（GitHub，仓库地址从 ShellConfig 读；没配的话 IsConfigured = false）。</summary>
    public static UpdateService CreateDefault() => new(new GitHubReleaseSource(
        ShellConfig.UpdateRepoOwner,
        ShellConfig.UpdateRepoName,
        ShellConfig.UpdateToken,
        tagPrefix: ShellConfig.UpdateTagPrefix));

    // ── 查 ────────────────────────────────────────────────────

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, string currentVersion, CancellationToken ct = default)
    {
        if (!Source.IsConfigured)
            return UpdateCheckResult.None("尚未配置更新源，暂时无法检查更新。");

        var releases = await Source.GetReleasesAsync(channel, 10, ct);
        if (releases.Count == 0)
            return UpdateCheckResult.None($"{UpdateChannels.ToDisplay(channel)} 通道下暂时还没有发布。");

        var comparer = new VersionTextComparer();
        var newest = releases.OrderByDescending(r => r.Version, comparer).First();

        if (!VersionCompare.IsNewer(newest.Version, currentVersion))
            return UpdateCheckResult.None($"已是最新（{UpdateChannels.ToDisplay(channel)} 通道）。");

        return new UpdateCheckResult(true, newest,
            $"发现新版本 {newest.Tag}（{UpdateChannels.ToDisplay(channel)} 通道）");
    }

    /// <summary>
    /// 拉历史版本（新的在前），给「回滚」用，最多 max 条；<b>按用户所在通道过滤</b>：
    /// 正式版通道只列正式版，预览版通道才连预览版（Beta）一起列。
    /// 回滚不校验版本高低，用户点哪个装哪个。
    /// </summary>
    public async Task<IReadOnlyList<UpdateRelease>> GetHistoryAsync(UpdateChannel channel, int max = 20, CancellationToken ct = default)
    {
        if (!Source.IsConfigured) return Array.Empty<UpdateRelease>();

        // Source 那边已经按通道过滤过：Stable 会跳过 prerelease，Insider 则两种都收。
        var releases = await Source.GetReleasesAsync(channel, max, ct);

        var comparer = new VersionTextComparer();
        return releases
            .GroupBy(r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.Version, comparer)
            .ThenByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(max)
            .ToList();
    }

    // ── 下 + 校验 ─────────────────────────────────────────────

    /// <summary>
    /// 下载安装包并校验。校验优先级：MD5 → SHA256（GitHub digest）。
    /// <list type="bullet">
    /// <item>校验不过 → 删掉文件并抛 <see cref="ChecksumMismatchException"/>；</item>
    /// <item><b>一个校验值都拿不到 → 抛 <see cref="MissingChecksumException"/>，绝不返回"未校验"的包</b>
    /// （界面上承诺的是「校验通过后方可安装」）。</item>
    /// </list>
    /// </summary>
    public async Task<DownloadedPackage> DownloadAndVerifyAsync(
        UpdatePackage package, string destinationFile, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        EnsureInsideUpdatesDir(destinationFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

        // ── 先取得校验值，再开始下载大文件 ─────────────────────────
        // ① 无任何校验值 → 立即拒绝，不执行未校验的安装包；
        // ② 校验文件即时拉取，取不到则中止本次更新，不允许降级为跳过校验。
        var expectedSha = package.Sha256 ?? "";
        string expectedMd5;
        try
        {
            expectedMd5 = await ResolveMd5Async(package, ct);
        }
        catch (MissingChecksumException) when (expectedSha.Length > 0)
        {
            // .md5 资产拉不动（限流 / 瞬断），但接口已经给了 SHA256：
            // SHA256 已足以完成校验，不因次要校验文件拉取失败而中止更新。
            // ⚠️ OperationCanceledException 不会被这条过滤器吃掉，取消仍然正常向上传播。
            expectedMd5 = "";
        }
        if (expectedMd5.Length == 0 && expectedSha.Length == 0)
            throw new MissingChecksumException(
                $"发布「{package.Name}」没有提供任何校验值（没有 .md5 资产，接口也没给 SHA256），已拒绝安装。");

        // 同一个安装包同时只允许一个下载：前台「立即更新」和后台自动下载会并着走到这里，
        // 原来两条路径共用一个固定 .part 名，会互相截断、覆盖（见报告 MEDIUM-11）。
        var gate = DownloadGates.GetOrAdd(
            Path.GetFullPath(destinationFile), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // 按用户设置（自建加速 / 自动 / GitHub 源）把原始链接翻译成候选，逐个尝试：
            // 自建加速经本站 Worker 代签取限时直链，失败自动回落官方直链。
            var candidates = await GithubRoute.ResolveAsync(package.Url.AbsoluteUri, ct).ConfigureAwait(false);
            Exception? lastError = null;

            foreach (var candidate in candidates)
            {
                // 临时名带 GUID：就算有地方绕过了上面的 gate，也不会写到同一个 .part 上
                var temp = destinationFile + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);

                    // 下载交由 DownloadService 并行通道（与软件下载同一策略：大文件分片）。
                    // ⚠️ 分片按偏移乱序写入，无法边下载边计算增量哈希，
                    //    因此先完整落盘，再整文件读取计算 MD5/SHA256（本地磁盘开销可忽略）。
                    var pump = progress is null
                        ? null
                        : new Progress<DownloadProgress>(p =>
                            progress.Report(p.Indeterminate ? 0 : p.Percent / 100.0));
                    await DownloadService.DownloadToFileAsync(candidate, temp, pump, ct);

                    using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using (var disk = File.OpenRead(temp))
                    {
                        var buffer = new byte[256 * 1024];
                        int read;
                        while ((read = await disk.ReadAsync(buffer, ct)) > 0)
                        {
                            md5.AppendData(buffer, 0, read);
                            sha.AppendData(buffer, 0, read);
                        }
                    }

                    var actualMd5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
                    var actualSha = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

                    // 到这里 expected 至少有一项非空（上面已拦），所以 verified 必然会被置 true
                    var verified = false;
                    var note = "未提供校验值（已跳过校验）";

                    if (expectedMd5.Length > 0)
                    {
                        if (!string.Equals(expectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase))
                            throw new ChecksumMismatchException("MD5", expectedMd5, actualMd5);
                        verified = true;
                        note = "MD5 已校验通过";
                        // 如提供了 SHA256，一并比对
                        if (expectedSha.Length > 0 && !string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                            throw new ChecksumMismatchException("SHA256", expectedSha, actualSha);
                    }
                    else
                    {
                        if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                            throw new ChecksumMismatchException("SHA256", expectedSha, actualSha);
                        verified = true;
                        note = "SHA256 已校验通过（该发布未提供 MD5）";
                    }

                    if (File.Exists(destinationFile)) File.Delete(destinationFile);
                    File.Move(temp, destinationFile);

                    return new DownloadedPackage(destinationFile, actualMd5, actualSha, verified, note);
                }
                catch (OperationCanceledException)
                {
                    // 用户取消：清掉残片直接向上传播，不重试其它候选
                    TryDelete(temp);
                    throw;
                }
                catch (Exception ex)
                {
                    // 候选失败（传输中断 / 校验不过 / 磁盘满）：删除 .part，尝试下一条候选。
                    // ⚠️ .part 残片没有回收路径（InstallerCleanup 匹配不到 *.part），
                    //    必须当场清理，否则反复更新失败会耗尽 %LOCALAPPDATA% 所在分区。
                    TryDelete(temp);
                    lastError = ex;
                }
            }

            // 所有候选都失败：抛出最后一条错误（没有候选时兜一个通用错误）
            throw lastError ?? new InvalidOperationException("下载失败。");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 下载目标必须待在 <c>updates</c> 目录里。<see cref="Path.Combine"/> 遇到根路径会**静默丢掉**基目录，
    /// 所以哪怕 <c>package.Name</c> 已经在上游校验过（见 GitHubReleaseSource.IsSafeAssetName），
    /// 写盘前也再断言一次 —— 这个文件随后会被 <see cref="RunInstaller"/> 执行。
    /// </summary>
    private static void EnsureInsideUpdatesDir(string destinationFile)
    {
        var full = Path.GetFullPath(destinationFile);
        var root = Path.GetFullPath(UpdatesDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"下载目标越出了 updates 目录，已拒绝写入：{destinationFile}");
    }

    /// <summary>per-destination 下载互斥（键 = 目标安装包的绝对路径）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadGates = new();

    /// <summary>
    /// 把 package.Md5 里的 "asset:URL" 解析成真正的 32 位十六进制 MD5。
    /// 返回 "" = **这个发布确实没给校验值**；取不到校验文件（网络/限流/取消）一律抛异常。
    /// </summary>
    public static async Task<string> ResolveMd5Async(UpdatePackage package, CancellationToken ct)
    {
        var raw = package.Md5 ?? "";
        if (raw.Length == 0) return "";
        if (!raw.StartsWith("asset:", StringComparison.OrdinalIgnoreCase))
            return NormalizeMd5(raw);

        var url = raw[6..];

        // ⚠️ 此处不允许吞异常：HttpClient 无总超时，请求仅受调用方 ct 约束，
        //    任何异常（含超时与取消）都必须向上传播；
        //    若在此返回 ""，将被判定为"未提供校验值"，导致未校验的安装包被执行。
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, ct);

        // 校验文件拿不到 ≠ "没有校验值"，这是必须中止更新的网络故障
        if (!resp.IsSuccessStatusCode)
            throw new MissingChecksumException(
                $"校验文件下载失败（HTTP {(int)resp.StatusCode}），无法校验「{package.Name}」，已中止本次更新。");

        var text = await resp.Content.ReadAsStringAsync(ct);

        // 校验文件里可能是 "d41d8... *ClassSoftwareHub-Setup-dv1.0.0.exe" 这种格式
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (!package.Name.IsEmpty() && trimmed.Contains(package.Name, StringComparison.OrdinalIgnoreCase))
            {
                var hit = NormalizeMd5(trimmed);
                if (hit.Length > 0) return hit;
            }
        }

        // 无一行匹配安装包文件名时，仅当整个文件只含一段 32 位十六进制
        // （单条目 .md5 sidecar）才可使用；多条目 checksum 文件中的哈希
        // 可能属于其他包，误用将导致校验永远无法通过。
        var matches = Regex.Matches(text, @"\b[0-9a-fA-F]{32}\b");
        if (matches.Count == 1) return matches[0].Value.ToLowerInvariant();

        // 多条目且无一匹配本安装包 → 返回空，由上层拒绝安装，不做猜测
        return "";
    }

    private static string NormalizeMd5(string text)
    {
        var m = Regex.Match(text, @"\b[0-9a-fA-F]{32}\b");
        return m.Success ? m.Value.ToLowerInvariant() : "";
    }

    /// <summary>
    /// 给「本地已有安装包，复用前校验」用的：整文件流式算 MD5（+可选 SHA256）。
    /// 与下载路径同一套算法，保证校验口径一致。
    /// </summary>
    public static async Task<(string Md5, string Sha256)> HashFileAsync(string path, bool withSha256, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = withSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        await using var fs = File.OpenRead(path);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            md5.AppendData(buffer, 0, read);
            sha?.AppendData(buffer, 0, read);
        }

        return (Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant(),
                sha is null ? "" : Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── 装 ────────────────────────────────────────────────────

    /// <summary>
    /// 静默执行安装包（Inno Setup 参数；.msi 走 msiexec）。
    /// Inno 按同一 AppId 识别已安装版本并原地升级，保留用户已选安装目录。
    ///
    /// ⚠️ 对 .exe（Inno）必须延迟启动，不能直接启动：
    ///    Inno 启动即检查 .iss 的 <c>AppMutex</c>（即本应用的单实例 Mutex），
    ///    检测到应用仍在运行时，提示框会被 /SUPPRESSMSGBOXES 自动按「取消」处理，
    ///    安装静默失败且无任何提示（实测应用运行期间安装 100% 失败）。
    ///    因此借 cmd 的 ping 延迟约 3 秒，为调用方退出应用留出窗口；
    ///    调用方随后应立即退出（见 UpdateFlow.RunAsync / InstallPendingNowAsync）。
    ///
    /// ⚠️ 安装结束后退出码写入 <see cref="InstallOutcomePath"/>：
    ///    安装失败时应用即将退出，若无记录，用户将无感知地停留在旧版本；
    ///    下次启动由 <see cref="ConsumeInstallOutcome"/> 读取并提示。
    ///    .msi 同样经由本路径，保证延迟启动与退出码记录行为一致。
    ///
    /// 不支持的扩展名抛 <see cref="NotSupportedException"/>：
    ///    直接 Process.Start 非 PE 文件时调用本身不抛异常，
    ///    应用会照常退出且未安装任何内容。
    /// </summary>
    public static void RunInstaller(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
            throw new ArgumentException("安装包路径为空。", nameof(installerPath));

        var ext = Path.GetExtension(installerPath).ToLowerInvariant();

        // Inno Setup 静默参数：/SP- 不显示"准备安装"提示，/CLOSEAPPLICATIONS 自动关掉占用的应用，
        // /TASKS="desktopicon" 保证升级后桌面快捷方式还在
        const string innoArgs =
            "/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /SUPPRESSMSGBOXES /TASKS=\"desktopicon\"";

        if (ext == ".msi")
        {
            LaunchThroughCmd(installerPath, $"msiexec /i \"{installerPath}\" /qb /norestart");
            return;
        }

        if (ext != ".exe")
            throw new NotSupportedException($"不支持的安装包类型「{ext}」，已取消安装。");

        LaunchThroughCmd(installerPath, $"\"{installerPath}\" {innoArgs}");
    }

    /// <summary>
    /// 经 cmd 启动安装器：先延迟约 3 秒供应用退出，安装结束后将退出码写入结果文件。
    ///
    /// ⚠️ 必须以 <c>/V:ON</c> 启用延迟变量扩展，才能在安装器退出后取到其退出码
    ///    （<c>%errorlevel%</c> 在整行解析时即被展开，取到的是 ping 的退出码，无意义）。
    ///    路径含 <c>!</c> 时延迟扩展会破坏路径，此时退回不记录退出码；
    ///    读取方将 null 视为无记录，不产生误报。
    /// </summary>
    private static void LaunchThroughCmd(string installerPath, string commandLine)
    {
        // ⚠️ 延迟用 ping 而非 timeout：timeout 在无控制台（CreateNoWindow=true）时
        //    报"输入重定向不受支持"并立即退出，起不到等待作用。
        //    ping -n 4 即 3 次间隔，约 3 秒。
        const string delay = "ping -n 4 127.0.0.1 >nul";

        Directory.CreateDirectory(UpdatesDir);
        var outcome = InstallOutcomePath;
        try { File.Delete(outcome); } catch { /* 上一次的残留，盖不掉也无妨 */ }

        var canRecord = installerPath.IndexOf('!') < 0 && outcome.IndexOf('!') < 0;
        var arguments = canRecord
            ? $"/V:ON /c {delay} & {commandLine} & echo !errorlevel! >\"{outcome}\""
            : $"/c {delay} & {commandLine}";

        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            // 仅启动 cmd 进程即返回（调用方随后必须立即退出应用）；
            // 退出码由 cmd 在安装器结束后写入文件，本进程不等待。
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"启动安装程序失败：{ex.Message}", ex);
        }
    }

    /// <summary>上一次静默安装的退出码落在这儿（由 <see cref="LaunchThroughCmd"/> 写）。在 updates 目录里。</summary>
    public static string InstallOutcomePath => Path.Combine(UpdatesDir, "install-outcome.txt");

    /// <summary>
    /// 读走并删掉上一次安装的退出码记录。
    /// <c>null</c> = 没有记录（老版本装的 / 文件还没写完 / 已经被读过 / 内容不可解析）；
    /// <c>0</c> = 安装器报成功；<b>非 0 = 装失败了</b>，而当时应用已经退出，需要补一句提示。
    /// </summary>
    public static int? ConsumeInstallOutcome()
    {
        try
        {
            var path = InstallOutcomePath;
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path).Trim();
            try { File.Delete(path); } catch { /* 删不掉的话下次再读一次，不会死循环 */ }
            return int.TryParse(text, out var code) ? code : null;
        }
        catch
        {
            // 读取失败视为无记录：宁可漏报，不可误报
            return null;
        }
    }

    /// <summary>更新包的落地目录：%LOCALAPPDATA%\ClassSoftwareHub\updates</summary>
    public static string UpdatesDir => Path.Combine(AppPaths.DataDir, "updates");
}

/// <summary>按 SemVer 比版本字符串。</summary>
public sealed class VersionTextComparer : IComparer<string>
{
    public int Compare(string? x, string? y) => VersionCompare.Compare(x, y);
}

internal static class StringHelper
{
    public static bool IsEmpty(this string? s) => string.IsNullOrEmpty(s);
}
