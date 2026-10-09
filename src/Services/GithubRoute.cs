using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 「GitHub 应用更新加速源」的三个取值（设置页「软件内容」）。存进 settings.json 的就是这三个字面量。
/// ⚠️ 这是**存档格式**的一部分：只能加新值，不能改已有字面量。
/// </summary>
public static class GithubRoutes
{
    /// <summary>自动：默认档。优先自建节点，不可用时按测速结果挑最快的公益镜像。</summary>
    public const string Auto = "auto";

    /// <summary>自建加速服务：GitHub 链接交由社区自建节点中转（经本站 Worker 代签，取限时直链）。</summary>
    public const string SelfHosted = "selfhosted";

    /// <summary>GitHub 源：原样直连 github.com，不做任何改写。</summary>
    public const string Official = "github";

    /// <summary>将存档字符串收敛为三个合法值；无法识别时按 GitHub 源（官方直连）处理。</summary>
    public static string Normalize(string? value) => value switch
    {
        SelfHosted => SelfHosted,
        Auto => Auto,
        _ => Official,
    };
}

/// <summary>
/// 下载前把「原始链接」翻译成「实际要抓的链接」—— 用户级的**全局默认下载路径**。
///
/// 覆盖范围：**软件下载**（<see cref="DownloadService"/>）与**更新包下载**
/// （<c>UpdateService.DownloadAndVerifyAsync</c>）两条链路都走这里。
///
/// 和详情页那条「加速通道」的分工：
///   · 详情页 = 逐条**手动**挑公益镜像（<see cref="Core.GithubMirror.Channels"/>），一次一条链接；
///   · 这里   = 用户在设置里定**一次**，之后所有 GitHub 下载都按它走。
///
/// 默认链路（与网页端同策略，三段式，前一段不行就自动走下一段）：
///   ① **自建加速**：本站 Worker <c>/api/mirror-sign</c> 代签 → 拿到自建节点
///      <c>download.classsoftwarehub.cn</c> 的限时直链（约 15 分钟有效）。
///      ⚠️ 该节点**不是** gh-proxy 那种「前缀拼接」（<c>节点/原链接</c> 会被 403 拒绝），
///      只能走签名直链；且下载时**必须携带本站白名单里的 Referer**，缺了同样 403。
///      密钥不进客户端：签名在 Worker 侧完成，本程序只拿一次性直链。
///   ② **公益镜像**：自建代签不通 / 节点不可用 → 对公益镜像**并行测速**，最快的排前面，
///      其余按速度排序回退（结果缓存 10 分钟，避免重复测速）。
///   ③ **GitHub 官方直链**：以上均不可用时的最终回退，保证下载始终可用。
///
/// 选「GitHub 源」则以上全不做，原样直连；非 GitHub 文件链接（官网、网盘、商店页）一律不改写。
/// </summary>
public static class GithubRoute
{
    /// <summary>
    /// 自建加速服务**是否已经就绪**。就绪后设置页不再显示红色警告；
    /// 选「自建加速服务」（或默认的「自动」）会经 Worker 代签取限时直链，
    /// 失败自动回落公益镜像测速择优，全不可用再回 GitHub 官方直链。
    /// </summary>
    public static readonly bool AcceleratorReady = true;

    /// <summary>自建加速节点主机（gh-stream 签名节点）。仅用于判断「这条链接是不是加速链接」。</summary>
    public const string AcceleratorHost = "download.classsoftwarehub.cn";

    /// <summary>访问加速节点时**必须**携带的 Referer（节点侧的来源白名单；不携带一律 403）。</summary>
    public const string AcceleratorReferer = "https://classsoftwarehub.us.ci/";

    /// <summary>代签入口（本站 Worker），按顺序回退。原生命令行客户端也能调，无需浏览器 Origin。</summary>
    private static readonly string[] SignEndpoints =
    {
        "https://cshapi.132614.xyz/api/mirror-sign",
        "https://submit.132614.xyz/api/mirror-sign",
    };

    /// <summary>探测窗口：到点就断开（不等它下完），拿这段时间的平均速度当排名依据。</summary>
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(2.5);

    /// <summary>探测硬超时：覆盖已建立连接但无数据返回的情况。</summary>
    private static readonly TimeSpan ProbeHardTimeout = TimeSpan.FromSeconds(8);

    /// <summary>探测读到的字节数低于此值 → 这条基本不能用。</summary>
    private const int ProbeMinBytes = 64 * 1024;

    /// <summary>单次探测的最大读取量（Range 上界），避免为测速拉取完整文件。</summary>
    private const int ProbeMaxBytes = 512 * 1024;

    /// <summary>测速结果缓存时长，过期后重新测速。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly HttpClient ProbeHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,     // 靠下面的 CancellationToken 控制
    };

    /// <summary>代签请求专用 HttpClient（短请求，设置超时上限）。</summary>
    private static readonly HttpClient SignHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private static readonly object Gate = new();
    private static string[]? _mirrorOrder;
    private static DateTime _mirrorAt;

    /// <summary>当前设置（认不出的值当 GitHub 源）。</summary>
    public static string Current => GithubRoutes.Normalize(App.Settings.Current.GithubDownloadRoute);

    /// <summary>三条路径的中文名，界面上显示/写日志都用它。</summary>
    public static string DisplayName(string route) => GithubRoutes.Normalize(route) switch
    {
        GithubRoutes.SelfHosted => "自建加速服务",
        GithubRoutes.Official => "GitHub 源",
        _ => "自动",
    };

    /// <summary>这条 URL 是不是「自建加速节点」的链接 —— 下载/探速据此决定是否附 Referer。</summary>
    public static bool IsAcceleratedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            && u.Host.Equals(AcceleratorHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 原始链接 → **候选链接**（按优先级排：前一条失败了就试下一条）。
    /// 非 GitHub 文件链接、以及已经是加速链接的，原样返回一条 —— 绝不改写不该动的链接。
    ///
    /// 默认（自动）与「自建加速服务」同策略：**自建优先 → 公益镜像（测速择优）→ 官方直链**。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveAsync(string url, CancellationToken ct = default)
    {
        // 只有「GitHub 上的文件地址」才谈得上换路（官网直链、网盘、商店页一律不动）
        if (!Core.GithubMirror.IsMirrorableUrl(url))
            return new[] { url };

        // GitHub 源：原样直连，不做任何改写。
        if (Current == GithubRoutes.Official)
            return new[] { url };

        // 节点未就绪（当前已就绪，此分支留作回退）：直接走公益镜像测速择优，末尾回官方直链。
        if (!AcceleratorReady)
            return await MirrorChainAsync(url, probe: true, ct).ConfigureAwait(false);

        // ① 自建加速：先经本站 Worker 代签，拿到自建节点的限时直链。
        var signed = await SignAsync(url, ct).ConfigureAwait(false);
        if (signed is null)
        {
            // 自建不可用 → ② 公益镜像测速择优 → ③ 官方直链
            Core.AppLog.Info("download-route", "自建加速不可用：改用公益镜像（测速择优）");
            return await MirrorChainAsync(url, probe: true, ct).ConfigureAwait(false);
        }

        // 自建直链优先；下载失败时按已缓存的镜像顺序回退，最后回官方直链。
        var list = new List<string> { signed };
        list.AddRange(await MirrorChainAsync(url, probe: false, ct).ConfigureAwait(false));
        Core.AppLog.Info("download-route", $"自建加速服务：{signed}");
        return list;
    }

    /// <summary>
    /// 详情页「加速下载」专用：强制走加速链，不受设置页「GitHub 应用更新加速源」约束
    /// （该设置仅约束更新包等自动下载）。链路与「自动」档一致：
    /// ① 自建加速（Worker 代签 → 限时直链）；② 不可用时公益镜像并行测速择优；
    /// ③ 全部不可用时回退 GitHub 官方直链。
    /// 非 GitHub 文件链接（官网 / 网盘 / 商店页）原样返回，不做改写。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveAcceleratedAsync(string url, CancellationToken ct = default)
    {
        if (!Core.GithubMirror.IsMirrorableUrl(url))
            return new[] { url };

        var signed = await SignAsync(url, ct).ConfigureAwait(false);
        if (signed is null)
        {
            Core.AppLog.Info("download-route", "加速下载：自建加速不可用，改用公益镜像（测速择优）");
            return await MirrorChainAsync(url, probe: true, ct).ConfigureAwait(false);
        }

        var list = new List<string> { signed };
        list.AddRange(await MirrorChainAsync(url, probe: false, ct).ConfigureAwait(false));
        Core.AppLog.Info("download-route", "加速下载 · 自建节点：" + signed);
        return list;
    }

    /// <summary>调本站 Worker 代签：拿到自建节点的限时直链；不通返回 null。</summary>
    private static async Task<string?> SignAsync(string url, CancellationToken ct)
    {
        var query = "?url=" + Uri.EscapeDataString(url);
        foreach (var endpoint in SignEndpoints)
        {
            try
            {
                using var resp = await SignHttp.GetAsync(endpoint + query, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                {
                    var signed = u.GetString();
                    if (!string.IsNullOrWhiteSpace(signed)) return signed;
                }
            }
            catch { /* 换下一个入口 */ }
        }
        return null;
    }

    /// <summary>
    /// 公益镜像候选链：按测速结果排序（probe=true 现场并行测速；false 用缓存/默认序），
    /// **末尾固定补 GitHub 官方直链** —— 镜像全不可用时它接手。
    /// </summary>
    private static async Task<IReadOnlyList<string>> MirrorChainAsync(string official, bool probe, CancellationToken ct)
    {
        var order = probe
            ? await RankMirrorsAsync(official, ct).ConfigureAwait(false)
            : CachedMirrorOrder();

        var list = new List<string>(order.Count + 1);
        foreach (var id in order)
        {
            var channel = ChannelById(id);
            if (channel is not null)
                list.Add(Core.GithubMirror.MirrorUrl(official, channel));
        }
        list.Add(official);
        return list;
    }

    /// <summary>并行测速所有公益镜像 → 按「先可用、后速度」降序排序并缓存 10 分钟。</summary>
    private static async Task<IReadOnlyList<string>> RankMirrorsAsync(string official, CancellationToken ct)
    {
        var cached = CachedMirrorOrderOrNull();
        if (cached is not null) return cached;

        var channels = Core.GithubMirror.Channels;
        var probes = new Task<ProbeResult>[channels.Length];
        for (var i = 0; i < channels.Length; i++)
            probes[i] = ProbeAsync(Core.GithubMirror.MirrorUrl(official, channels[i]), ct);

        // 几条同时探，互不等待（总耗时 ≈ 单条探测窗口）
        var results = await Task.WhenAll(probes).ConfigureAwait(false);

        var order = channels
            .Select((ch, i) => (ch.Id, Result: results[i]))
            .OrderByDescending(x => x.Result.Usable)
            .ThenByDescending(x => x.Result.BytesPerSecond)
            .Select(x => x.Id)
            .ToArray();

        RememberMirrorOrder(order);
        Core.AppLog.Info("download-route", "公益镜像测速 → " + string.Join("；",
            channels.Select((ch, i) => $"{ch.Name} {Describe(results[i])}")));
        return order;
    }

    /// <summary>按 id 找一条镜像通道。</summary>
    private static Core.MirrorChannel? ChannelById(string id)
    {
        foreach (var ch in Core.GithubMirror.Channels)
            if (ch.Id == id) return ch;
        return null;
    }

    private sealed record ProbeResult(bool Ok, long Bytes, double BytesPerSecond, string? Error)
    {
        /// <summary>探到手了、而且读到的量够说明问题。</summary>
        public bool Usable => Ok && Bytes >= ProbeMinBytes;
    }

    /// <summary>只读一小段（Range）+ 只读一小会儿，据平均速度排名。失败不抛异常，包在结果里。</summary>
    private static async Task<ProbeResult> ProbeAsync(string url, CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(ProbeHardTimeout);
        var ct = linked.Token;

        var watch = Stopwatch.StartNew();
        long received = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, ProbeMaxBytes - 1);
            // 自建加速节点按 Referer 放行：探速也得带上，否则一律 403（会被误判成"不可用"）
            if (IsAcceleratedUrl(url))
                request.Headers.Referrer = new Uri(AcceleratorReferer);

            using var response = await ProbeHttp
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new ProbeResult(false, 0, 0, $"HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[32 * 1024];
            while (received < ProbeMaxBytes && watch.Elapsed < ProbeWindow)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                received += read;
            }
            watch.Stop();
            return new ProbeResult(true, received, Speed(received, watch.Elapsed), null);
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            // 只是探测超时（用户没取消）：已经读到的部分照样算数，够 64 KB 就还能用
            watch.Stop();
            return new ProbeResult(received > 0, received, Speed(received, watch.Elapsed), "探测超时");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult(false, 0, 0, ex.Message);
        }
    }

    /// <summary>平均速度（B/s）。耗时过短时按 0.05 秒下限计算，避免结果失真。</summary>
    private static double Speed(long bytes, TimeSpan elapsed) =>
        bytes <= 0 ? 0 : bytes / Math.Max(elapsed.TotalSeconds, 0.05);

    private static string Describe(ProbeResult r) => r switch
    {
        { Ok: false } => $"不可用（{r.Error}）",
        { Bytes: 0 } => "无数据",
        _ => $"{r.BytesPerSecond / 1024:0} KB/s（{r.Bytes / 1024} KB）",
    };

    /// <summary>返回缓存的镜像顺序；无缓存或已过期时按默认顺序返回，不触发测速。</summary>
    private static IReadOnlyList<string> CachedMirrorOrder()
    {
        var cached = CachedMirrorOrderOrNull();
        if (cached is not null) return cached;
        return Core.GithubMirror.Channels.Select(c => c.Id).ToArray();
    }

    private static string[]? CachedMirrorOrderOrNull()
    {
        lock (Gate)
        {
            if (_mirrorOrder is not null && DateTime.UtcNow - _mirrorAt <= CacheTtl)
                return _mirrorOrder;
            return null;
        }
    }

    private static void RememberMirrorOrder(string[] order)
    {
        lock (Gate)
        {
            _mirrorOrder = order;
            _mirrorAt = DateTime.UtcNow;
        }
    }

    /// <summary>路由设置变更后清除测速缓存，使新设置立即生效。</summary>
    public static void InvalidateCache()
    {
        lock (Gate)
        {
            _mirrorOrder = null;
        }
    }
}
