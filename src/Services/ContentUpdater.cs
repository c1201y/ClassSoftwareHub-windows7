using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一次内容同步的结果。</summary>
public sealed record ContentSyncResult(
    bool Updated,
    int Downloaded,
    int Total,
    string? Error,
    string BaseUrl,
    string? Note = null)
{
    public string Message => Error
        ?? Note
        ?? (Updated
            ? $"内容已更新（{Downloaded}/{Total} 个文件）"
            : $"内容已是最新（{Total} 个文件）");
}

/// <summary>
/// 软件数据的同步入口：
///   ① 首选 **GitHub 仓库直读**（见 GithubContentSync：软件数据就在公开仓库里）
///   ② 备胎：站点 content/manifest.json（列了每个文件的 path + sha256），逐个比对本地
///      %LOCALAPPDATA%\ClassSoftwareHub\content 里的文件，只下载 sha256 不一致的
///      （先落 .part，校验通过再改名）
/// 两条都拿不到就安静地什么都不做 —— 此时 ContentStore 只能读到空目录（**没有自带内容包兜底**），
/// 界面会显示"尚未获取到内容"并给出重试入口。
/// </summary>
public static class ContentUpdater
{
    /// <summary>本地缓存目录（ContentStore 优先读它）。</summary>
    public static string Dir => ShellConfig.CachedContentDir;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // ⚠️ 版本别写死：这里原来是 "ClassSoftwareHub/1.2"，而 ShellConfig.ShellVersion 早就是 1.1.0 ——
        //    同步客户端向内容服务器自报了一个**不存在的版本**，排查服务端日志时会误导人。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"ClassSoftwareHub/{ShellConfig.ShellVersion} content-sync");
        return client;
    }

    /// <summary>
    /// 同步软件数据。失败不抛异常，返回带 Error 的结果（界面自己决定要不要提示）。
    ///
    /// 顺序：
    ///   ① **GitHub 仓库直读**（首选 —— 软件数据就在公开仓库里，永远最新，不依赖站点有没有发布清单）
    ///   ② 站点 content/manifest.json（等站点以后发布了就走这条；GitHub 被墙时也是它的备胎）
    /// </summary>
    public static async Task<ContentSyncResult> SyncAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // ① GitHub 仓库（一次 ref 请求就知道有没有变，没变一个文件都不下）
        var repo = await GithubContentSync.SyncAsync(progress, ct).ConfigureAwait(false);
        if (repo.Ok)
            return new ContentSyncResult(repo.Updated, 0, repo.Files, null, GithubContentSync.RepoUrl, repo.Message);

        // ② 站点清单（增量 sha256）
        return await SyncFromManifestAsync(progress, ct).ConfigureAwait(false);
    }

    /// <summary>老路子：按站点 content/manifest.json 的文件 sha256 增量拉。</summary>
    private static async Task<ContentSyncResult> SyncFromManifestAsync(
        IProgress<string>? progress,
        CancellationToken ct)
    {
        Manifest? manifest = null;
        string manifestUrl = "";
        string manifestText = "";

        foreach (var candidate in new[] { ShellConfig.ContentManifestUrl, ShellConfig.ContentManifestUrlFallback })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var text = await Http.GetStringAsync(candidate, ct).ConfigureAwait(false);
                manifest = ParseManifest(text);
                if (manifest is { Files.Count: > 0 })
                {
                    manifestUrl = candidate;
                    manifestText = text;
                    break;
                }
            }
            catch (Exception ex)
            {
                progress?.Report("无法获取内容清单：" + ex.Message);
            }
        }

        if (manifest is null || manifestUrl.Length == 0)
            return new ContentSyncResult(false, 0, 0, "远端尚未发布内容包（无法获取 content/manifest.json），已回退到本机数据。", "");

        var baseUrl = manifestUrl[..(manifestUrl.LastIndexOf('/') + 1)];
        Directory.CreateDirectory(Dir);

        var downloaded = 0;
        // 坏条目记下来继续跑：**不能因为一个文件失败就中止整份清单**
        // （清单下次不变，同一条目会永久堵死内容更新 —— 见报告 MEDIUM-13）
        var failed = new List<string>();
        try
        {
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();

                var relative = file.Path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                var target = Path.Combine(Dir, relative);

                if (File.Exists(target) && Sha256Of(target) == file.Sha256.ToLowerInvariant())
                    continue;

                progress?.Report($"正在同步 {file.Path}");
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var part = target + ".part";
                try
                {
                    await using (var source = await Http.GetStreamAsync(baseUrl + file.Path, ct).ConfigureAwait(false))
                    await using (var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await source.CopyToAsync(sink, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    DeletePart(part);
                    throw;
                }
                catch (Exception ex)
                {
                    // 网络断在半路：走这条兜底通道（站点网盘）时往往正是 GitHub 被墙、网络最不稳的时候。
                    // 原来这里**没有任何 catch** —— CopyToAsync 一抛，part 残片就永久留在
                    // %LOCALAPPDATA%\ClassSoftwareHub\content 里，没有任何回收路径。
                    DeletePart(part);
                    failed.Add($"{file.Path}（下载失败：{ex.Message}）");
                    continue;
                }

                // 读不出刚写完的 .part（被杀软锁住 → I/O 失败）与"哈希真的不符"必须能分开：
                // 原来的 Sha256Of 吞掉所有异常返回 ""，两种情况都走同一条"校验失败"，
                // 排查时完全看不出到底是哪一种。
                if (!TrySha256(part, out var actual))
                {
                    DeletePart(part);
                    failed.Add($"{file.Path}（读取失败，无法校验）");
                    continue;
                }
                if (!string.Equals(actual, file.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    DeletePart(part);
                    failed.Add($"{file.Path}（校验失败，已丢弃）");
                    continue;
                }

                File.Move(part, target, overwrite: true);
                downloaded++;
            }

            // 清单正文也留一份，方便排查本地是哪一版
            try { await File.WriteAllTextAsync(Path.Combine(Dir, "manifest.json"), manifestText, Encoding.UTF8, ct).ConfigureAwait(false); }
            catch { /* 写不下不影响内容 */ }
        }
        catch (Exception ex)
        {
            return new ContentSyncResult(downloaded > 0, downloaded, manifest.Files.Count, "同步出错：" + ex.Message, baseUrl);
        }

        if (failed.Count > 0)
        {
            // 失败条目可能很多，提示里只列前 3 条，别把对话框撑爆
            var shown = failed.Count > 3 ? failed.GetRange(0, 3) : failed;
            var more = failed.Count > shown.Count ? " …" : "";
            return new ContentSyncResult(downloaded > 0, downloaded, manifest.Files.Count,
                $"有 {failed.Count} 个文件没同步成功：{string.Join("；", shown)}{more}", baseUrl);
        }

        return new ContentSyncResult(downloaded > 0, downloaded, manifest.Files.Count, null, baseUrl);
    }

    // ── 内部 ────────────────────────────────────────────────────────

    private sealed class Manifest
    {
        public List<ManifestFile> Files { get; } = new();
    }

    private sealed class ManifestFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    private static Manifest? ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var manifest = new Manifest();

            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return manifest;

            foreach (var item in files.EnumerateArray())
            {
                var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var hash = item.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
                if (path.Length == 0 || hash.Length == 0) continue;

                // 只认识正经相对路径，别让清单里的 ../ 跳出目录
                if (path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path)) continue;

                manifest.Files.Add(new ManifestFile { Path = path, Sha256 = hash });
            }

            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 算 SHA256，读不出来（文件被占用 / 权限不足）时返回 ""。
    /// 用于**已存在的目标文件**的快速比对（对不上就重下，无所谓原因）。
    /// 需要区分"I/O 失败"与"哈希不符"的场景请用 <see cref="TrySha256"/>。
    /// </summary>
    private static string Sha256Of(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>算 SHA256；<paramref name="hash"/> 为 null 表示**读不出来**，与"哈希真的不符"区分开。</summary>
    private static bool TrySha256(string file, out string? hash)
    {
        hash = null;
        try
        {
            using var stream = File.OpenRead(file);
            using var sha = SHA256.Create();
            hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>删掉半截的 <c>.part</c>；删不掉就留着（下次同步会覆盖），别让它把异常顶出去。</summary>
    private static void DeletePart(string part)
    {
        try { File.Delete(part); } catch { }
    }
}
