using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// UI 线程卡死看门狗。
///
/// <para>
/// 解决什么问题：现场反馈「程序偶尔无响应，等一会儿自己好了」。这类症状 **不抛任何异常**，
/// 所以 <c>AppLog</c> 的三个全局异常钩子一个字都不会写 —— 用户报障时手里只有一句「卡了」，
/// 既不知道卡多久、也不知道卡在哪一页，最后只能归到「没法复现」。
/// </para>
///
/// <para>
/// 检测原理：后台线程周期性向 UI 线程派发一个空回调，并测量「派发到真正执行」之间的延迟。
/// 这个延迟就是 UI 消息队列的排队时间，等价于 UI 线程被占用的时长。UI 线程空闲时延迟接近 0；
/// 一旦某个同步调用把 UI 线程占住（死锁、同步 HTTP、大文件 I/O、布局循环、GC），
/// 延迟会随卡顿时长线性增长，超过阈值即判定为卡死。
/// </para>
///
/// <para>
/// ⚠️ 三条设计约束，改代码前先读：
/// </para>
/// <list type="number">
///   <item>
///     <b>绝不在后台线程碰 UI 对象。</b> 卡死状态下从别的线程读 <c>MainWindow.ClientSize</c>
///     这类属性，很可能去抢 UI 线程持有的锁，反而把看门狗自己一起卡死 —— 那样就永远抓不到现场。
///     所以「当时在哪一页」由 UI 线程在心跳回调里自行取样缓存，后台线程只读缓存。
///   </item>
///   <item>
///     <b>同一时刻只允许一个未执行的派发。</b> 用 <c>_posted</c> 标志闸住；否则 UI 线程卡 30 秒，
///     队列里会堆 150 个空回调，恢复瞬间集中执行，本身制造一次新的卡顿。
///   </item>
///   <item>
///     <b>稳态零分配、零锁。</b> 每 <see cref="PollMs"/> 毫秒一次派发，无分配、无 monitor 争用，
///     对老机器的影响可以忽略。
///   </item>
/// </list>
///
/// <para>
/// 自测：启动参数 <c>--test-hang 6</c> 会在主窗口出来后故意把 UI 线程占住 6 秒，
/// 用来确认这条链路在真机上确实能抓到（见 <see cref="RunSelfTestIfRequested"/>）。
/// </para>
/// </summary>
internal static class HangWatchdog
{
    /// <summary>派发间隔。200ms 是「够灵敏」与「够省」的折中：卡 2 秒内必被发现，稳态开销可忽略。</summary>
    private const int PollMs = 200;

    /// <summary>
    /// 判定阈值：UI 线程连续被占用这么久就记一条。
    ///
    /// 定 2000ms 而不是更小：软件渲染 + 教室机滚动大列表时，单帧耗到三四百毫秒是常态，
    /// 阈值压太低只会把正常卡顿刷成告警，真出事时反而淹没在噪音里。
    /// 可用环境变量 <c>CSH_HANG_MS</c> 覆盖（现场按机器性能调）。
    /// </summary>
    private static int _thresholdMs = 2000;

    /// <summary>超长卡顿（默认 10 秒）额外往 <c>crash.log</c> 也写一份 —— 用户排障时通常只发这一个文件。</summary>
    private const int SevereMs = 10_000;

    /// <summary>启动后的静默期：初始化阶段 UI 线程合法地忙上几秒，这段时间不报。自测时会被清零。</summary>
    private static volatile int StartupGraceMs = 15_000;

    /// <summary>两次告警的最小间隔，避免反复卡顿刷爆日志。</summary>
    private const int RepeatCooldownMs = 5_000;

    private static Thread? _worker;
    private static volatile bool _running;

    /// <summary>UI 线程最近一次执行心跳的 QPC 时刻。0 = 尚未开始。</summary>
    /// <remarks>
    /// ⚠️ 由 UI 线程写、后台线程读，两侧没有任何共同的锁 —— 必须显式走
    /// <see cref="Volatile"/> 读写。不能加 <c>volatile</c> 修饰：C# 不允许 volatile long（CS0677），
    /// long 也不保证在 32 位环境下原子。少了屏障，后台线程可能一直读到旧值，
    /// 表现就是「明明没卡却报卡死」。
    /// </remarks>
    private static long _uiBeat;

    /// <summary>闸门：1 = 已有一个派发在队列里等执行。</summary>
    private static int _posted;

    /// <summary>UI 线程侧缓存的现场描述（页面、窗口尺寸）。后台线程只读它，不碰 UI 对象。</summary>
    private static volatile string _scene = "";

    /// <summary>UI 线程心跳计数，用于「每 N 次取样一次现场」省掉无谓的字符串分配。</summary>
    private static int _beatCount;

    private static long _startTimestamp;
    private static long _lastWarnTimestamp;

    /// <summary>本次卡死是否已经报过开场（用于配对「卡死中 / 已恢复」两条日志）。</summary>
    private static long _hangStartTimestamp;

    /// <summary>
    /// 启动看门狗。**必须在 UI 线程已可用之后调用**（<c>Dispatcher.UIThread</c> 要能派发），
    /// 在 <c>Program.Main</c> 里紧接 Avalonia 初始化完成后即可。
    /// </summary>
    public static void Start()
    {
        if (_running) return;

        if (int.TryParse(Environment.GetEnvironmentVariable("CSH_HANG_MS"), out var custom) && custom >= 300)
            _thresholdMs = custom;

        _running = true;
        _startTimestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref _uiBeat, _startTimestamp);

        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "csh-hang-watchdog",
            // 卡死检测本身不能被别的因素拖慢；略低于普通优先级，避免和渲染抢 CPU。
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();

        AppLog.Info("hang", $"UI 线程看门狗启动｜阻塞阈值={_thresholdMs}ms｜采样间隔={PollMs}ms");
    }

    /// <summary>
    /// 停止看门狗。**退出流程一开始就要调**（<c>desktop.Exit</c> / <c>ShutdownRequested</c>）。
    ///
    /// <para>
    /// 不调会有两个后果：① Avalonia 的 Dispatcher 关闭后 <c>Post</c> 会持续抛异常，
    /// 后台线程每 200ms 空转一次捕获一次异常；② 退出阶段本来就有正常的等待
    /// （关窗、释放 WebView2、还原 gamma），会被误报成「UI 线程卡死」。
    /// </para>
    ///
    /// <para>幂等，可以多次调用。</para>
    /// </summary>
    public static void Stop()
    {
        if (!_running) return;
        _running = false;
    }

    private static void Loop()
    {
        while (_running)
        {
            try
            {
                Thread.Sleep(PollMs);

                var now = Stopwatch.GetTimestamp();
                var idleMs = ElapsedMs(Volatile.Read(ref _uiBeat), now);

                // ── 卡死判定 ────────────────────────────────────────────────
                if (idleMs >= _thresholdMs)
                {
                    ReportHang(idleMs, now);
                }
                else if (_hangStartTimestamp != 0)
                {
                    // 心跳恢复：补一条收尾，把「卡了多久」钉死。
                    var heldMs = ElapsedMs(_hangStartTimestamp, now);
                    _hangStartTimestamp = 0;

                    AppLog.Warn("hang", $"UI 线程恢复响应｜本次阻塞={heldMs:0}ms"
                                        + (string.IsNullOrEmpty(_scene) ? "" : "｜" + _scene));

                    // ⚠️ 超长判定必须放在**恢复时**做，不能放在进入卡死那一刻：
                    //    进入时只过了阈值（2 秒），永远够不到 10 秒，那样这一段永远不触发。
                    if (heldMs >= SevereMs)
                    {
                        AppLog.Error("crash", $"UI 线程长时间无响应｜阻塞={heldMs / 1000:0.#} 秒"
                                              + (string.IsNullOrEmpty(_scene) ? "" : "｜" + _scene));
                    }
                }

                // ── 派发下一次心跳（闸门保证队列里最多一个）──
                if (Interlocked.CompareExchange(ref _posted, 1, 0) == 0)
                {
                    // Normal 而不是 Send：Send 会插到队首，测出来的排队时间会系统性偏小，
                    // 而且每 200ms 抢一次最高优先级没有必要。Normal 与普通应用代码同级，
                    // 能如实反映「消息队列积压了多久」。
                    Dispatcher.UIThread.Post(OnUiBeat, DispatcherPriority.Normal);
                }
            }
            catch
            {
                // 看门狗自己绝不能把进程搞挂。
                try { Interlocked.Exchange(ref _posted, 0); } catch { }
            }
        }
    }

    /// <summary>
    /// UI 线程侧的心跳。**这里跑在 UI 线程上**，所以取现场信息是安全的 —— 这也正是
    /// 「现场由 UI 线程自己缓存、后台只读」这条约束的落点。
    /// </summary>
    private static void OnUiBeat()
    {
        try
        {
            Volatile.Write(ref _uiBeat, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _posted, 0);

            // 每 5 次（约 1 秒）刷新一次现场：卡死时报告用的是「卡住之前最后一次正常状态」，
            // 这已经足够定位（页面切换本身就会刷新它）。
            if (++_beatCount >= 5)
            {
                _beatCount = 0;
                try { _scene = AppLog.ContextProvider?.Invoke() ?? ""; }
                catch { _scene = ""; }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _posted, 0);
        }
    }

    private static void ReportHang(double idleMs, long now)
    {
        // 启动静默期：初始化时 UI 线程忙是正常的。
        if (ElapsedMs(_startTimestamp, now) < StartupGraceMs) return;

        // 同一次卡死只报开场一次。
        if (_hangStartTimestamp != 0) return;

        // 反复卡顿时限流。
        if (_lastWarnTimestamp != 0 && ElapsedMs(_lastWarnTimestamp, now) < RepeatCooldownMs) return;

        _hangStartTimestamp = Volatile.Read(ref _uiBeat);   // 起点 = 最后一次成功心跳，不是「现在」
        _lastWarnTimestamp = now;

        // 采集能区分「死锁 / 死算 / GC / I/O」的几个读数。全部是进程内廉价调用，不加锁。
        var gen0 = GC.CollectionCount(0);
        var gen2 = GC.CollectionCount(2);
        var heap = GC.GetTotalMemory(false);
        var poolThreads = SafeThreadPoolCount();
        var pending = SafePendingWork();

        var detail = $"UI 线程阻塞｜已阻塞≥{idleMs:0}ms"
                     + (string.IsNullOrEmpty(_scene) ? "" : "｜" + _scene)
                     + $"\n  · 托管堆={AppLog.Size(heap)}｜GC 次数 gen0={gen0} gen2={gen2}"
                     + $"\n  · 线程池线程={poolThreads}｜排队项={pending}"
                     + "\n  · 判读=后台线程仍在运行，进程未整体挂死，UI 线程被同步操作占用";

        // 进入卡死时只记在 hang 通道（正常日志）。做成「超长才升级到 crash.log」
        // 是在恢复那一刻判定的，见 Loop —— 这里拿到的只是刚越过阈值的时长，看不出总长。
        AppLog.Warn("hang", detail);
    }

    private static int SafeThreadPoolCount()
    {
        try { return ThreadPool.ThreadCount; } catch { return -1; }
    }

    private static int SafePendingWork()
    {
        try { return (int)ThreadPool.PendingWorkItemCount; } catch { return -1; }
    }

    private static double ElapsedMs(long from, long to)
        => (to - from) * 1000.0 / Stopwatch.Frequency;

    // ════════════════════════════════════════════════════════════════════════
    //  自测：确认这条链路在真机上真的能抓到
    //
    //  为什么需要：看门狗的价值全在「出事时抓得到」，而这件事平时无法验证 ——
    //  真等现场出问题再发现它没生效，就白做了。所以给一个主动制造卡顿的开关，
    //  在教室机上跑一遍，日志里应当出现 [hang] 条目。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 若命令行带 <c>--test-hang &lt;秒&gt;</c>，在主窗口稳定后故意占住 UI 线程若干秒。
    /// 仅用于验证看门狗生效，正式使用不会触发。
    /// </summary>
    public static void RunSelfTestIfRequested(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--test-hang", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;

        var seconds = 5;
        if (i + 1 < args.Length) int.TryParse(args[i + 1], out seconds);
        if (seconds <= 0) seconds = 5;

        AppLog.Warn("hang", $"看门狗自测｜{seconds} 秒后占住 UI 线程 {seconds} 秒｜目的=验证阻塞检测");

        // ⚠️ 必须清掉启动静默期：自测通常在主窗口起来十几秒内就跑，正好落在静默期里，
        //    不清的话卡死会被静默期吞掉，测出来「没生效」，实际是测法不对。
        StartupGraceMs = 0;

        // 延迟到主窗口画出第一帧之后再动手。
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                var t0 = Stopwatch.GetTimestamp();
                // 忙等：模拟真实的同步阻塞（死锁/大 IO 的观感）。Thread.Sleep 同样会占住 UI 线程。
                while (ElapsedMs(t0, Stopwatch.GetTimestamp()) < seconds * 1000)
                    Thread.Sleep(50);
            }
            catch { }
            AppLog.Warn("hang", "看门狗自测结束｜UI 线程已放行");
        }, TimeSpan.FromSeconds(seconds + 3));
    }
}
