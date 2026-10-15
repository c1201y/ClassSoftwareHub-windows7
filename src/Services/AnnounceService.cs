using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Views;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 教室喊话：后台轻轮询自建 Worker 的 <c>/api/announce/poll</c>，收到就弹全屏喊话窗。
/// 设计稿见 docs/教室喊话功能设计.md。
///
/// <para>
/// 轮询纪律（对齐设计稿 3.3 节）：
///   · 间隔取设置值（默认 30s），每次 ±20% 抖动 —— 避免一批教室机同拍打服务器；
///   · 连续失败按 ×2 退避（上限 120s），成功一次即复位；
///   · 喊话窗开着（正在播报）时本轮跳过 —— 播报期间再拉到新消息只会互相覆盖；
///   · 多入口回退复用 <see cref="SubmitEndpoint"/>（与投稿同一套域名与"记住上次成功"机制）。
/// </para>
///
/// <para>
/// ⚠️ 日志只记 id / 长度 / 来源档位，**不记正文** —— 教室机日志学生可能翻得到（设计稿 5.2 节）。
/// 403 = 配对码被拒（被其他机器认领 / 绑定过期且被顶号）：只 Warn，不停止轮询
/// —— 停了之后设置页没人重新拉起，宁可空轮询也不哑掉。
/// </para>
/// </summary>
public static class AnnounceService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(SubmitEndpoint.TimeoutMs / 1000.0) };
    private static readonly object Gate = new();

    /// <summary>
    /// 本机配对码：由设备指纹确定性导出的 6 位数字（FNV-1a → 取模），
    /// 重装 / 清空设置后不变，无需落盘、无需服务端分配。
    /// 语义对齐 UU 远程 —— 一台电脑一个码，配对即互填对方配对码；一台设备的码可被多方保存。
    /// ⚠️ 换码只改这一处导出逻辑，界面与服务端都不用动。
    /// </summary>
    private static string? _pairCode;
    public static string MyPairCode => _pairCode ??= DerivePairCode(DeviceFingerprint.Value);

    private static string DerivePairCode(string deviceId)
    {
        uint h = 2166136261;
        foreach (var c in deviceId)
        {
            h ^= c;
            h *= 16777619;
        }
        return ((int)(h % 1000000)).ToString("D6");
    }

    private static bool _running;
    private static int _failStreak;          // 连续网络失败次数（退避用）
    private static int _backoffSec;          // 当前退避秒数；0 = 正常节奏

    /// <summary>当前轮询状态（喊话页显示用）。false = 开关没开 / 刚被关掉。</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>最近一次轮询拿到服务器回应的时刻（本地时间；null = 一次都没通过）。喊话页显示"上次检查"用。</summary>
    public static DateTime? LastPollAt { get; private set; }

    /// <summary>当前退避秒数（&gt;0 = 连续失败退避中）。喊话页状态栏用。</summary>
    public static int BackoffSec => _backoffSec;

    /// <summary>
    /// 启动轮询（App 启动与喊话页打开开关时调）。幂等：已在跑就什么都不做。
    /// </summary>
    public static void Start()
    {
        lock (Gate)
        {
            var s = App.Settings.Current;
            if (!s.AnnounceEnabled)
            {
                AppLog.Info("announce", "喊话轮询未启用（设置开关关闭），不上电");
                return;
            }
            if (_running) return;

            _running = true;
            IsRunning = true;
            _failStreak = 0;
            _backoffSec = 0;
        }

        AppLog.Begin("announce", $"启动喊话轮询（间隔档 {App.Settings.Current.AnnounceIntervalSec}s，本机配对码 {MyPairCode}）");
        _ = Task.Run(() => LoopAsync());
    }

    /// <summary>停止轮询（设置页关掉开关时调）。正在播报的窗口不关，让它自然播完。</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            IsRunning = false;
        }
        AppLog.Info("announce", "喊话轮询已停止");
    }

    /// <summary>设置页改了间隔档 / 重新生成配对码后调一下：未运行则拉起，已运行则下一轮自然生效。</summary>
    public static void RefreshSettings() => Start();

    // ════════════════════════ 轮询主循环 ════════════════════════

    private static async Task LoopAsync()
    {
        while (true)
        {
            lock (Gate)
            {
                if (!_running) { IsRunning = false; return; }
            }

            try
            {
                await Task.Delay(NextDelaySec() * 1000);
            }
            catch (OperationCanceledException) { return; }

            lock (Gate)
            {
                if (!_running) { IsRunning = false; return; }
            }

            // 正在播报就跳过这一轮：全屏窗开着，拉到新消息也只是把它顶掉
            if (AnnounceWindow.IsShowing) continue;

            try
            {
                await PollOnceAsync();
            }
            catch (Exception ex)
            {
                // 理论上 PollOnceAsync 自己兜底，这里防意外漏网
                AppLog.Fail("announce", ex, "轮询循环意外异常");
            }
        }
    }

    /// <summary>下一轮等待秒数：正常节奏 = 设置档 ±20% 抖动；有退避则用退避值。</summary>
    private static int NextDelaySec()
    {
        if (_backoffSec > 0) return _backoffSec;

        var baseSec = Math.Clamp(App.Settings.Current.AnnounceIntervalSec, 10, 120);
        var jitter = baseSec * 0.2;
        return Math.Max(5, (int)Math.Round(baseSec + (Random.Shared.NextDouble() * 2 - 1) * jitter));
    }

    private static async Task PollOnceAsync()
    {
        var name = Uri.EscapeDataString((Environment.MachineName ?? "").Trim());
        var query = $"deviceId={DeviceFingerprint.Value}&pairCode={MyPairCode}&name={name}";
        var gotAnswer = false;      // 只要有一个入口返回了 HTTP 状态就算"网络通"

        foreach (var baseUrl in SubmitEndpoint.Ordered())
        {
            HttpResponseMessage resp;
            try
            {
                resp = await Http.GetAsync(baseUrl.TrimEnd('/') + "/api/announce/poll?" + query);
            }
            catch (Exception)
            {
                continue;       // 这个入口不通，换下一个
            }

            gotAnswer = true;
            using (resp)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
                {
                    MarkOk();
                    return;     // 没有新喊话，最常见的一态
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    var msg = AnnounceMessage.Parse(body);
                    MarkOk();
                    if (msg is null)
                    {
                        AppLog.Warn("announce", "poll 200 但内容解不出来（长度 " + (body?.Length ?? 0) + "）");
                        return;
                    }
                    OnMessage(msg);
                    return;
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    // 固定码广播方案下 poll 不应再出现 403（多机注册，没有顶号概念）。
                    // 真见到就是服务端语义变了 —— Warn 一笔退避，别把轮询停死。
                    AppLog.Warn("announce", "poll 403：服务端拒绝本机（语义可能已变化）");
                    _backoffSec = 60;
                    return;
                }

                if ((int)resp.StatusCode >= 500)
                {
                    continue;   // 服务端抖了，换下一个入口
                }

                // 4xx 其他（400 参数错等）：参数是我们自己拼的，重试也不会好，记一笔就回
                AppLog.Warn("announce", $"poll 返回 {(int)resp.StatusCode}，本轮放弃");
                return;
            }
        }

        if (!gotAnswer)
        {
            _failStreak++;
            _backoffSec = Math.Min(120, 15 * (1 << Math.Min(_failStreak, 3)));   // 15/30/60/120 封顶
            if (_failStreak == 1 || _failStreak % 5 == 0)
                AppLog.Warn("announce", $"所有入口均不可达（连续第 {_failStreak} 轮），退避 {_backoffSec}s");
        }
    }

    private static void MarkOk()
    {
        _failStreak = 0;
        _backoffSec = 0;
        LastPollAt = DateTime.Now;
    }

    /// <summary>收到喊话：回 UI 线程弹窗。日志只记 id 与长度，不记正文。</summary>
    private static void OnMessage(AnnounceMessage msg)
    {
        AppLog.Begin("announce", $"收到喊话 id={msg.Id} 长度={msg.Text.Length} 时长={msg.Duration}s"
            + (string.IsNullOrEmpty(msg.From) ? "" : " 有署名"));
        if (App.Settings.Current.AnnounceQuietMode)
            AppLog.Step("announce", "安静模式：只展示不出声");

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var s = App.Settings.Current;
                AnnounceWindow.Show(msg, quiet: s.AnnounceQuietMode, speak: s.AnnounceSpeakText);
            }
            catch (Exception ex)
            {
                AppLog.Fail("announce", ex, "弹出喊话窗失败");
            }
        });
    }

    // ════════════════════════ 配对码 ════════════════════════

    public static bool IsValidPairCode(string? code)
    {
        if (code is null || code.Length != 6) return false;
        foreach (var c in code)
            if (c < '0' || c > '9') return false;
        return true;
    }

    // ════════════════════════ 发送（桌面端发送区用） ════════════════════════

    /// <summary>已配对设备的查询结果。LastSeenMs = 0 表示该码下还没有设备登记过（从未开启接收）。</summary>
    public sealed record PairedDeviceInfo(string Name, long LastSeenMs);

    /// <summary>
    /// 查询一个配对码对应的设备（<c>/api/announce/list</c>，一码一机语义下注册表至多一条）。
    /// 用于添加配对 / 刷新机器名与在线状态。码格式非法或网络不可达返回 null。
    /// </summary>
    public static async Task<PairedDeviceInfo?> QueryDeviceAsync(string code)
    {
        if (!IsValidPairCode(code)) return null;
        var query = "pairCode=" + Uri.EscapeDataString(code);
        foreach (var baseUrl in SubmitEndpoint.Ordered())
        {
            HttpResponseMessage resp;
            try
            {
                resp = await Http.GetAsync(baseUrl.TrimEnd('/') + "/api/announce/list?" + query);
            }
            catch (Exception)
            {
                continue;       // 这个入口不通，换下一个
            }

            using (resp)
            {
                if (resp.StatusCode != System.Net.HttpStatusCode.OK) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (!doc.RootElement.TryGetProperty("devices", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                        continue;
                    foreach (var e in arr.EnumerateArray())
                    {
                        var nm = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var ts = e.TryGetProperty("ts", out var t) && t.TryGetInt64(out var v) ? v : 0;
                        return new PairedDeviceInfo(nm, ts);    // 一码一机：取第一条即目标设备
                    }
                    return new PairedDeviceInfo("", 0);          // 码合法但从未有设备在该码下轮询过
                }
                catch (Exception)
                {
                    continue;   // 内容解不出，换下一个入口
                }
            }
        }
        return null;
    }

    /// <summary>SendAsync 的结果。Devices = 成功送达的设备数之和；Failed = 发送失败的设备数；Error = 首个失败码。</summary>
    public sealed record SendResult(bool Ok, string Error, int Devices, int Failed);

    /// <summary>
    /// 发一条喊话（走与网页端同一组接口，多入口回退与轮询一致）。
    /// <paramref name="targetCodes"/> = 目标设备的配对码名单（发送区勾选结果，一码一机），
    /// 逐码顺序发送 —— 服务端限流按码计，不同码之间互不影响，可批量。
    /// 日志只记长度与结果，不记正文。
    /// </summary>
    public static async Task<SendResult> SendAsync(string text, string from, int duration, System.Collections.Generic.IReadOnlyList<string>? targetCodes = null)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > 100)
            return new SendResult(false, "bad_request", 0, 0);

        var codes = targetCodes?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList()
                    ?? new System.Collections.Generic.List<string>();
        if (codes.Count == 0)
            return new SendResult(false, "no_target", 0, 0);

        AppLog.Begin("announce", $"发送喊话 长度={text.Length} 时长={duration}s 目标 {codes.Count} 台"
            + (string.IsNullOrWhiteSpace(from) ? "" : " 有署名"));

        var devices = 0;
        var failed = new System.Collections.Generic.List<string>();
        foreach (var code in codes)
        {
            var (ok, err, dev) = await SendToOneAsync(code, text, from, duration);
            if (ok) devices += dev;
            else failed.Add(code + (string.IsNullOrEmpty(err) ? "" : $"({err})"));
        }

        if (failed.Count == 0)
        {
            AppLog.Done("announce", $"发送成功，将送达 {devices} 台设备");
            return new SendResult(true, "", devices, 0);
        }

        AppLog.Warn("announce", $"发送完成：成功 {codes.Count - failed.Count} 台，失败 {failed.Count} 台（{string.Join(", ", failed)}）");
        return new SendResult(failed.Count < codes.Count, failed[0], devices, failed.Count);
    }

    /// <summary>向单个配对码发送（多入口回退）。返回 (是否成功, 服务端错误码, 送达设备数)。</summary>
    private static async Task<(bool Ok, string Error, int Devices)> SendToOneAsync(string code, string text, string from, int duration)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            pairCode = code,
            text,
            from = (from ?? "").Trim(),
            duration,
        });
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        foreach (var baseUrl in SubmitEndpoint.Ordered())
        {
            HttpResponseMessage resp;
            try
            {
                resp = await Http.PostAsync(baseUrl.TrimEnd('/') + "/api/announce/send", content);
            }
            catch (Exception)
            {
                continue;       // 这个入口不通，换下一个
            }

            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync();
                if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300)
                {
                    var devices = 0;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        devices = doc.RootElement.TryGetProperty("devices", out var d) && d.TryGetInt32(out var n) ? n : 0;
                    }
                    catch { /* 解不出就当 0，不影响成功判定 */ }
                    return (true, "", devices);
                }

                var err = "";
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                }
                catch { }

                // 限流 / 无设备这类确定性失败不用换入口重试，直接回
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (false, string.IsNullOrEmpty(err) ? "rejected" : err, 0);

                if ((int)resp.StatusCode >= 500)
                    continue;   // 服务端抖了，换下一个入口

                return (false, string.IsNullOrEmpty(err) ? "rejected" : err, 0);
            }
        }

        return (false, "unreachable", 0);
    }
}
