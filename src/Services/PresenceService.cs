using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 在线心跳：周期性向自建后端报告本机仍在使用，供管理后台统计在线设备。
///
/// <para>
/// 与 <see cref="AnnounceService"/> 的分工：后者是教室喊话的消息轮询，出厂默认关闭，
/// 关闭时服务端完全看不到这台机器。在线统计不能依赖它 —— 后台要回答的是
/// 「装了客户端的机器现在有几台在用」，与是否参加喊话无关。所以本服务只做一件事：
/// 每隔 <see cref="IntervalSec"/> 秒打一次 <c>GET /api/ping</c>，不带任何业务参数。
/// </para>
///
/// <para>
/// ⚠️ 心跳是后台行为，客户端刻意不挂 OfficialApi 的授权闸门：请求不带构建标识 / 设备码 /
/// 激活凭证，服务端偶发 401/403 也不会在用户毫无操作时弹出模态「激活正版」。
/// 服务端在请求入口统一登记在线，不区分是否激活 —— 未激活设备同样应当在后台可见。
/// </para>
///
/// <para>
/// 失败静默：这是后台运营数据，不该在界面上报错。连续失败按指数退避，
/// 上限 <see cref="MaxBackoffSec"/> 秒；一旦成功立刻回到正常节奏。
/// </para>
///
/// <para>
/// 线程模型：整个进程一份循环。<see cref="Start"/> 幂等，重复调用不会产生第二条循环；
/// <see cref="Stop"/> 通过取消令牌打断 <see cref="Task.Delay"/>，避免残留。
/// </para>
/// </summary>
internal static class PresenceService
{
    /// <summary>正常心跳间隔。服务端在线窗口为 300 秒，允许连续丢四次。</summary>
    private const int IntervalSec = 60;

    /// <summary>连续失败时的最长等待，避免把不可达的入口一直重打。</summary>
    private const int MaxBackoffSec = 300;

    /// <summary>单次请求超时。心跳是后台行为，慢一点没关系，但不能把线程拖住。</summary>
    private const int TimeoutSec = 10;

    // ⚠️ 心跳是后台行为，绝不能因服务端偶发 401/403 弹出模态「激活正版」：
    //    这里用不带 ClientAuthHandler 的客户端，请求也不带激活凭证。
    private static readonly HttpClient Http = new HttpClient(HttpTls.Handler())
    {
        Timeout = TimeSpan.FromSeconds(TimeoutSec),
    };

    private static readonly object Gate = new();
    private static CancellationTokenSource? _loopCts;
    private static bool _running;

    /// <summary>启动心跳。已启动时直接返回。</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_running) return;
            _running = true;

            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _ = Task.Run(() => LoopAsync(token));
        }

        AppLog.Info("presence", $"在线心跳启动｜间隔={IntervalSec}s");
    }

    /// <summary>停止心跳（退出前调；不调也会随后台任务一起结束）。</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            try { _loopCts?.Cancel(); } catch { }
            _loopCts?.Dispose();
            _loopCts = null;
        }

        AppLog.Info("presence", "在线心跳已停止");
    }

    private static async Task LoopAsync(CancellationToken token)
    {
        var failures = 0;

        while (!token.IsCancellationRequested)
        {
            if (token.IsCancellationRequested) return;

            if (await PingOnceAsync(token).ConfigureAwait(false))
            {
                failures = 0;
            }
            else
            {
                failures = Math.Min(failures + 1, 4);
            }

            // 成功走正常间隔；失败按 2 的幂退避，最长 MaxBackoffSec
            var waitSec = failures == 0
                ? IntervalSec
                : Math.Min(MaxBackoffSec, IntervalSec * (1 << failures));

            try
            {
                await Task.Delay(waitSec * 1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 依次尝试各入口，任意一个回 2xx 即算上报成功。
    /// 回 4xx 说明这个入口通、只是请求被拒（配额或来源判定），换入口也没有意义，直接结束本轮。
    /// </summary>
    private static async Task<bool> PingOnceAsync(CancellationToken token)
    {
        foreach (var baseUrl in ShellConfig.OrderedSubmitEndpoints())
        {
            if (token.IsCancellationRequested) return false;

            try
            {
                using var resp = await Http
                    .GetAsync(baseUrl.TrimEnd('/') + "/api/ping", token)
                    .ConfigureAwait(false);

                if (resp.IsSuccessStatusCode) return true;
                if ((int)resp.StatusCode is >= 400 and < 500)
                {
                    AppLog.Warn("presence", $"在线心跳被拒｜入口={new Uri(baseUrl).Host}｜HTTP={(int)resp.StatusCode}");
                    return false;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                // 单个入口不通很常见（三个入口的线路不同），换下一个即可，不必刷日志噪音
                AppLog.Warn("presence", $"在线心跳失败｜入口={SafeHost(baseUrl)}｜{ex.GetType().Name}");
            }
        }

        return false;
    }

    private static string SafeHost(string baseUrl)
    {
        try { return new Uri(baseUrl).Host; }
        catch { return baseUrl; }
    }
}
