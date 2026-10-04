using System;
using System.Collections.Generic;
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

            var prerelease = GetBool(item, "prerelease");
            // 正式版通道：只要非预发布；预览版通道：预发布 + 正式版（预览用户也能跟上正式版）
            if (channel == UpdateChannel.Stable && prerelease) continue;

            var tag = GetString(item, "tag_name");
            if (tag.Length == 0) continue;

            // ── tag 前缀过滤（当前未启用）────────────────────────────
            // Win7 版 2026-10-04 起改用独立仓库，天然与 WinUI 版隔离，所以 UpdateTagPrefix 为空、
            // 这一段不生效。留着它是给「万一又得合库」留个挂点：届时填上前缀，
            // 本版就只认带前缀的 Release，认下来把前缀剥掉，下游拿到的还是 dv1.1.0-insider1.2 形状。
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

    private static readonly string[] InstallerExts = { ".exe", ".msi", ".zip" };

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

            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            var digest = GetString(a, "digest");   // 形如 "sha256:xxxx"（新版 API 才有）
            var sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "";
            all.Add((name, new Uri(url), size, sha));
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
        // 1) 独立的 checksums.md5 / MD5SUMS 之类：记下 URL，下载后解析（这里先返回 URL，由 UpdateService 解析）
        foreach (var marker in new[] { "checksum", "md5" })
        {
            var file = all.FirstOrDefault(a => a.Name.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (file.Name is not null) return "asset:" + file.Url;
        }
        // 2) 同名 sidecar（<安装包>.md5）：同上，交给下载层解析
        var side = all.FirstOrDefault(a =>
            a.Name.Equals(installerName + ".md5", StringComparison.OrdinalIgnoreCase));
        if (side.Name is not null) return "asset:" + side.Url;

        return "";
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
