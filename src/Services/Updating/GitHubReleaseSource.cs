using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>
/// GitHub Releases 更新源。
/// · 正式版 = 「Latest」（非预发布）的 Release
/// · 预览版 = 打了「Pre-release」的 Release
/// 只读公开仓库，不需要令牌；带令牌时能拿到更高的接口配额（也顺便避免 60 次/小时的匿名限制）。
/// </summary>
public sealed class GitHubReleaseSource : IUpdateSource
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly string _owner;
    private readonly string _repo;
    private readonly string _apiBase;
    private readonly string? _token;

    /// <summary>
    /// 只认带这个前缀的 tag，认下来后**把前缀剥掉**再当成版本号。
    /// 空串 = 不隔离（<b>Win7 版独立成库后的当前状态</b>，见 <see cref="ShellConfig.UpdateTagPrefix"/>）。
    /// </summary>
    private readonly string _tagPrefix;

    public GitHubReleaseSource(string owner, string repo, string? token = null, string apiBase = "https://api.github.com", string tagPrefix = "")
    {
        _owner = owner?.Trim() ?? "";
        _repo = repo?.Trim() ?? "";
        _token = string.IsNullOrWhiteSpace(token) ? null : token!.Trim();
        _apiBase = string.IsNullOrWhiteSpace(apiBase) ? "https://api.github.com" : apiBase.TrimEnd('/');
        _tagPrefix = tagPrefix?.Trim() ?? "";
    }

    public string DisplayName => IsConfigured ? $"GitHub（{_owner}/{_repo}）" : "GitHub（未配置）";

    public bool IsConfigured => _owner.Length > 0 && _repo.Length > 0;

    public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(UpdateChannel channel, int max, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("尚未配置更新仓库（owner/repo）。");

        // ⚠️ per_page 拉满：本仓库目前只有 Win7 版自己的发布，正常用不满；
        //    但一次拉够总没坏处 —— 万一将来又跟别的发布线合库，拉太少会被别人的 Release 挤掉。
        var url = $"{_apiBase}/repos/{_owner}/{_repo}/releases?per_page=100";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.TryAddWithoutValidation("User-Agent", ShellConfig.AppName);
        if (_token is not null)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"GitHub 接口返回 {(int)resp.StatusCode}：{Trim(body)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var list = new List<UpdateRelease>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (GetBool(item, "draft")) continue;

            var tag = GetString(item, "tag_name");
            if (tag.Length == 0) continue;

            // GitHub 那个「Pre-release」复选框 **和** tag 名里读出来的预发布，两者取或。
            // 只信复选框的话，发版时忘了勾 → `dv1.2.0-insider1.0` 会被当成正式版推给 stable 通道
            // 的所有用户，而界面还会把它标成"正式版"，客户端无从察觉。
            var prerelease = GetBool(item, "prerelease") || LooksPrerelease(tag);

            // 正式版通道：只要非预发布；预览版通道：预发布 + 正式版（预览用户也能跟上正式版）
            if (channel == UpdateChannel.Stable && prerelease) continue;

            // ── tag 前缀过滤（当前未启用）────────────────────────────
            // Win7 版 2026-10-04 起改用独立仓库，天然与 WinUI 版隔离，所以 UpdateTagPrefix 为空、
            // 这一段不生效。留着它是给「万一又得合库」留个挂点：届时填上前缀，
            // 本版就只认带前缀的 Release，认下来把前缀剥掉，下游拿到的还是 dv1.0.0 形状。
            if (_tagPrefix.Length > 0)
            {
                if (!tag.StartsWith(_tagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                tag = tag[_tagPrefix.Length..];
            }

            var packages = ParsePackages(item);
            if (packages.Count == 0) continue;

            list.Add(new UpdateRelease(
                Tag: tag,
                Version: tag,
                Channel: prerelease ? UpdateChannel.Insider : UpdateChannel.Stable,
                Prerelease: prerelease,
                Notes: GetString(item, "body"),
                PublishedAt: ParseTime(GetString(item, "published_at")),
                Packages: packages));
        }

        return list
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(max <= 0 ? 10 : max)
            .ToList();
    }

    // ── 资产挑选 ────────────────────────────────────────────────

    /// <summary>安装包候选名字（以后发版时按这个起名就能被自动识别）。</summary>
    private static readonly string[] InstallerHints = { "setup", "installer", "-install" };

    /// <summary>
    /// 安装包扩展名白名单。
    ///
    /// ⚠️ 这里**原本还有 ".zip"**：README 说发布形态有两种（安装包 .exe / 便携包 .zip），
    ///    而 <see cref="PickInstallers"/> 里的 <c>portable</c> 过滤只挡名字里带 "portable" 的 zip ——
    ///    一个叫 <c>ClassSoftwareHub-Setup-dv1.1.0.zip</c> 的包会同时通过扩展名与 setup 两道过滤，
    ///    被选成主安装包。可整个工程里**没有任何解压代码**，RunInstaller 只特判 .msi，
    ///    其余全走 Inno 分支 → CreateProcess 返回 ERROR_BAD_EXE_FORMAT，
    ///    而 Process.Start 本身是成功的、不抛异常 → 应用照常退出、什么都没装、毫无提示。
    ///    代码没有"解压 → 执行"的能力，就别把它当安装包候选。
    /// </summary>
    private static readonly string[] InstallerExts = { ".exe", ".msi" };

    private static List<UpdatePackage> ParsePackages(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return new List<UpdatePackage>();

        var all = new List<(string Name, Uri Url, long Size, string Sha256)>();
        foreach (var a in assets.EnumerateArray())
        {
            var name = GetString(a, "name");
            var url = GetString(a, "browser_download_url");
            if (name.Length == 0 || url.Length == 0) continue;

            // 资产名原样来自远端，而它随后会被拼成落盘路径（Path.Combine 遇根路径会丢掉基目录）、
            // 最后还会被执行 —— 所以在这里就拒绝掉路径穿越与非法文件名字符。
            // 同一份代码库里 ContentUpdater.ParseManifest 恰恰做了同样的校验，这里不能漏。
            if (!IsSafeAssetName(name)) continue;

            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            var digest = GetString(a, "digest");   // 形如 "sha256:xxxx"（新版 API 才有）
            var sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "";

            // ⚠️ 别用裸 new Uri(url)：畸形 URL 会抛 UriFormatException 中止整次检查且不带上下文
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            // 这个 URL 随后会被直接下载并执行，只认 http(s)
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;

            all.Add((name, uri, size, sha));
        }

        var result = new List<UpdatePackage>();
        foreach (var (name, url, size, sha) in PickInstallers(all))
        {
            var md5 = FindMd5(all, name);
            result.Add(new UpdatePackage(name, url, size, sha, md5));
        }
        return result;
    }

    private static IEnumerable<(string Name, Uri Url, long Size, string Sha256)> PickInstallers(
        List<(string Name, Uri Url, long Size, string Sha256)> all)
    {
        var candidates = all
            .Where(a => InstallerExts.Any(e => a.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Where(a => !a.Name.Contains("checksum", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 优先带 setup/installer 关键词的；没有就退而求其次取最大的
        var prefer = candidates
            .Where(a => InstallerHints.Any(h => a.Name.Contains(h, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.Size)
            .ToList();

        return prefer.Count > 0 ? prefer : candidates.OrderByDescending(a => a.Size).Take(1);
    }

    /// <summary>找 MD5：同名的 &lt;安装包&gt;.md5，或常见的校验文件里的一行。</summary>
    private static string FindMd5(List<(string Name, Uri Url, long Size, string Sha256)> all, string installerName)
    {
        // 1) **同名 sidecar**（<安装包>.md5）：最精确，必须排在最前面。
        //    ⚠️ 原来这条排在"通用校验文件"后面。发版脚本 publish-release-win7.mjs 会同时上传
        //       安装包和便携包各自的 .md5，通用那条 FirstOrDefault 拿到的可能是**便携包**的哈希
        //       → 校验永远 mismatch，而期望值每次都重新拉取 → 这次更新永远成功不了。
        var side = all.FirstOrDefault(a =>
            a.Name.Equals(installerName + ".md5", StringComparison.OrdinalIgnoreCase));
        if (side.Name is not null) return "asset:" + side.Url;

        // 2) 独立的 checksums.md5 / MD5SUMS 之类：记下 URL，下载后解析（这里先返回 URL，由 UpdateService 解析）
        foreach (var marker in new[] { "checksum", "md5" })
        {
            // 别捡到**别人**的 sidecar —— 那份哈希记的是另一个文件
            var file = all.FirstOrDefault(a =>
                a.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) && !IsSidecarOfOther(a.Name, all));
            if (file.Name is not null) return "asset:" + file.Url;
        }

        return "";
    }

    /// <summary>这个 <c>.md5</c> 是不是同 Release 里**另一个真实资产**的 sidecar（那就不是我们要的那份）。</summary>
    private static bool IsSidecarOfOther(
        string name, List<(string Name, Uri Url, long Size, string Sha256)> all)
    {
        if (!name.EndsWith(".md5", StringComparison.OrdinalIgnoreCase)) return false;
        var owner = name[..^".md5".Length];
        return all.Any(a => string.Equals(a.Name, owner, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 从 tag 读出"这是预发布"：<c>dv1.0.0-insider1.1</c> 这种。
    /// 与 GitHub 的 Pre-release 复选框**取或**，避免发版时忘了勾就把内部构建推给正式版用户。
    ///
    /// ⚠️ 刻意**不用**"名字里有 <c>-</c> 就算预发布"：历史上存在 <c>win7-dv1.0.0</c> 这种
    ///    带前缀的 tag，那会让正式版被误判成预发布（前缀当前为空，但别把这条路堵死）。
    ///    所以只认明确的预发布记号。
    /// </summary>
    private static bool LooksPrerelease(string tag) =>
        tag.Contains("insider", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("-beta", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("-rc", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("-preview", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("-pre", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 资产名能不能安全地当文件名用（它会被 Path.Combine 成落盘路径、随后被执行）。
    /// 拒绝：根路径、<c>..</c> 段、非法文件名字符、过长的名字。
    /// </summary>
    private static bool IsSafeAssetName(string name)
    {
        if (name.Length == 0 || name.Length > 180) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (Path.IsPathRooted(name)) return false;

        // 这个名字最终会被拼进 cmd 命令行（UpdateService.LaunchThroughCmd）：
        //  - '%' 是 cmd 在**引号里也照样展开**的（%PATH% 之类），会把路径替换掉；
        //  - '!' 在延迟扩展下会被吃掉（RunInstaller 为此做了降级，但能避免就避免）；
        //  - '"' 由 GetInvalidFileNameChars 挡住，剩下的 & | < > 因为整段被引号包住而无害。
        if (name.IndexOfAny(new[] { '%', '^', '!' }) >= 0) return false;

        return true;
    }

    // ── 小工具 ──────────────────────────────────────────────────

    private static string GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool GetBool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ParseTime(string iso) =>
        DateTimeOffset.TryParse(iso, out var t) ? t : null;

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
