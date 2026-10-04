using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 内存回收（教学机 8G 内存，不能一路涨上去）。
///
/// 两步走：
///   ① 先让东西能被放掉 —— 页面容器关掉导航缓存（ContentFrame.CacheSize=0）、
///      每个页面自己 Unloaded 时停掉自己的定时器（定时器会通过委托把页面对象钉在内存里）
///   ② 再把内存真还给系统 —— GC + EmptyWorkingSet（把闲置页丢进系统 standby list，
///      进程的工作集会立刻降下来，别的程序要用内存时系统优先回收）
///
/// 只在「用户看不见」的时候收：收进托盘 / 最小化 / 切页停稳之后 / 浮窗收起。
/// 节流 4 秒，避免连续切页时反复 GC 卡顿。
/// </summary>
public static class MemoryTrimmer
{
    private static DateTimeOffset _last = DateTimeOffset.MinValue;
    private static readonly object Gate = new();

    /// <summary>上一次回收把托管堆压下去多少（字节），界面上可以显示。</summary>
    public static long LastFreedBytes { get; private set; }

    /// <summary>累计回收次数（排查用）。</summary>
    public static int TrimCount { get; private set; }

    private static string LogPath => Path.Combine(SettingsStore.Dir, "memory.log");

    /// <summary>收一次。force = 用户主动触发，忽略节流。</summary>
    public static void Trim(bool force = false)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.Now;
            if (!force && (now - _last).TotalMilliseconds < 4000) return;
            _last = now;
            TrimCount++;
        }

        try
        {
            var before = GC.GetTotalMemory(false);

            // ⚠️ 2026-10-02 实机日志改：原来是 GCCollectionMode.Optimized + blocking:false ——
            //    运行时把这种调用只当"建议"，目标机上 21 次回收里有 19 次是
            //    「46.4MB → 46.4MB」，一个字节都没放掉（memory.log 里白纸黑字）。
            //    回收只在"用户看不见"的时刻发生（收托盘 / 最小化 / 切页停稳），
            //    所以这里改成 **Forced + blocking**：宁可卡这几十毫秒，也要真的把内存还回去。
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

            var after = GC.GetTotalMemory(false);
            LastFreedBytes = Math.Max(0, before - after);

            NativeMethodsMemory.EmptyWorkingSet(NativeMethodsMemory.GetCurrentProcess());

            Log($"第 {TrimCount} 次：托管堆 {before / 1048576.0:0.#}MB → {after / 1048576.0:0.#}MB (force={force})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[mem] 回收失败: " + ex.Message);
            Log("回收失败：" + ex.Message);
        }
    }

    /// <summary>等界面渲染停稳再收（切页动画没结束就 GC，纯浪费）。</summary>
    /// <remarks>
    /// ⚠️ 移植说明：原版用 <c>Microsoft.UI.Dispatching.DispatcherQueue.CreateTimer()</c> 起一个单次定时器；
    ///    Avalonia 的等价物是 <c>Avalonia.Threading.DispatcherTimer</c>（默认重复，Tick 里自己 Stop，
    ///    语义与原版 <c>IsRepeating=false</c> 一致）。
    /// </remarks>
    public static void TrimLater(int delayMs = 3000, bool force = false)
    {
        try
        {
            // 不在 UI 线程时没法定时，直接收（等价于原版 GetForCurrentThread() 拿到 null 的分支）
            if (!Dispatcher.UIThread.CheckAccess()) { Trim(force); return; }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Trim(force);
            };
            timer.Start();
        }
        catch
        {
            Trim(force);
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Dir);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }
}
