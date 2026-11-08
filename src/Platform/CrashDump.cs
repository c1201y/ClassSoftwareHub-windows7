using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClassSoftwareHub.Desktop.Core;
using Microsoft.Win32;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 原生崩溃转储。
///
/// <para>
/// 解决什么问题：现场反馈「程序有时候直接没了」。这类消失**不经过 CLR 的异常通道** ——
/// 访问冲突（0xC0000005）、栈溢出、驱动层崩溃、<c>Environment.FailFast</c> 都会让进程当场结束，
/// 于是 <c>AppDomain.UnhandledException</c> 来不及跑、<c>ProcessExit</c> 不触发，
/// 日志目录里只剩一个「没有收尾」的会话 —— 事后完全查不出死因。
/// 唯一能留下证据的办法是让 **Windows 自己** 在进程死亡时写一份内存转储。
/// </para>
///
/// <para>
/// 做法：注册 WER（Windows 错误报告）的 LocalDumps 策略，指向本程序私有的
/// <c>crashes</c> 目录。这一步只是写注册表，**不注入任何进程内钩子** ——
/// 这是刻意的选择：dump 由系统在进程已损坏的状态下生成，不受本进程内存/锁状态影响，
/// 比自己在 <c>SetUnhandledExceptionFilter</c> 里调 <c>MiniDumpWriteDump</c> 可靠得多，
/// 也不会覆盖 CLR 自己安装的顶层异常过滤器。
/// </para>
///
/// <para>
/// 转储类型默认 <b>mini</b>（<see cref="MiniDumpType"/>）而不是 full：
/// mini dump 含线程栈、已加载模块、异常记录，足以定位「崩在哪个函数」；
/// 而 full dump 动辄两三百兆，教室机磁盘本来就紧张。
/// 需要看托管堆时设环境变量 <c>CSH_DUMP_FULL=1</c>，或启动参数 <c>--dump-full</c>。
/// </para>
///
/// <para>
/// 自测：启动参数 <c>--test-crash</c> 会故意制造一次访问冲突，用来确认 dump 真的会落地
/// （见 <see cref="RunSelfTestIfRequested"/>）。
/// </para>
/// </summary>
internal static class CrashDump
{
    /// <summary>dump 存放目录：<c>%LOCALAPPDATA%\ClassSoftwareHub\crashes</c>。</summary>
    public static string DumpDir => Path.Combine(Services.SettingsStore.Dir, "crashes");

    /// <summary>保留的 dump 个数上限。MiniDump 实测约 4MB，留 5 份对教室机磁盘没有压力。</summary>
    private const int KeepCount = 5;

    /// <summary>
    /// WER LocalDumps 的 <c>DumpType</c> 取值。
    /// ⚠️ 与 CLR 的 <c>DOTNET_DbgMiniDumpType</c> 是**两套不同的编码**，别混用：
    ///    WER：0=自定义 1=Mini 2=Full
    ///    CLR：1=Mini 2=Heap 3=Triage 4=Full
    /// 本文件里 WER 走这两个常量，CLR 走字符串 "1" / "4"。
    /// </summary>
    private const int DumpTypeMini = 1;
    private const int DumpTypeFull = 2;

    /// <summary>已上报过的 dump 名单（避免每次启动都重复提示同一份）。</summary>
    private static string ReportedListPath => Path.Combine(DumpDir, ".reported");

    private static bool _fullRequested;

    // Init 采集到的状态。刻意不在这里写日志，原因见 Init 的注释。
    private static bool _werRegistered;
    private static string _werWhere = "";
    private static string _werError = "";
    private static string _werStatus = "未知";

    /// <summary>
    /// 注册 WER LocalDumps 并检查环境。**必须在 <c>Program.Main</c> 尽早调用**，
    /// 晚于第一次可能崩溃的代码就没意义了。
    ///
    /// <para>
    /// ⛔ 这里**只做注册与状态采集，一个字都不写日志**，落盘交给
    /// <see cref="LogStartupStatus"/>。原因是调用时机太早：
    /// 本方法跑在 Avalonia 启动之前，而 <c>App</c> 构造函数里紧接着还有一趟
    /// 「第二个实例敲门 → 自退」的判定（<c>Environment.Exit(0)</c>）。
    /// 若在此处写日志，会先把日志会话目录建出来 —— 于是 <c>LogHome.Established</c>
    /// 那道「只写了一行的空会话不该留下」的闸当场失效，
    /// 用户每点一次桌面图标就多一个垃圾目录。
    /// </para>
    /// </summary>
    public static void Init(string[] args)
    {
        _fullRequested = Array.Exists(args, a => a.Equals("--dump-full", StringComparison.OrdinalIgnoreCase))
                         || Environment.GetEnvironmentVariable("CSH_DUMP_FULL") == "1";

        try { Directory.CreateDirectory(DumpDir); } catch { /* 建不出来也要继续，后面的检查会说明原因 */ }

        _werRegistered = RegisterLocalDumps(out _werWhere, out _werError);
        _werStatus = ProbeWerService();
    }

    /// <summary>
    /// 把 <see cref="Init"/> 采到的状态写进日志，并处理上一次运行留下的转储。
    ///
    /// 由 <c>App.OnFrameworkInitializationCompleted</c> 在会话日志目录落定之后调用 ——
    /// 具体原因见 <see cref="Init"/> 的注释（早写会污染单实例的"空会话"判定）。
    /// </summary>
    public static void LogStartupStatus()
    {
        try
        {
            CheckRuntimeDump();

            AppLog.Info("dump", $"WER 转储｜状态={(_werRegistered ? "已启用" : "未启用")}"
                                + (_werWhere.Length > 0 ? $"｜注册位置={_werWhere}" : "")
                                + $"｜输出目录={DumpDir}"
                                + $"｜WER 服务={_werStatus}");

            if (!_werRegistered)
            {
                // 注册失败不挡启动，但必须让人知道「以后真崩了可能没有 dump」，否则又是一次无从查起。
                AppLog.Warn("dump", "注册 WER LocalDumps 失败" + (_werError.Length > 0 ? "｜原因=" + _werError : "")
                                    + "｜影响=程序可正常运行，但原生崩溃时不产生转储文件");
            }

            if (_werStatus == "已禁用")
            {
                AppLog.Warn("dump", "Windows 错误报告服务（WerSvc）已禁用｜影响=系统不会写 dump，原生崩溃无现场"
                                    + "｜建议=将 WerSvc 启动类型改回「手动」");
            }

            ReportPendingDumps();
            TrimOldDumps();
        }
        catch
        {
            // 诊断信息写不进去不影响运行。
        }
    }

    // ══════════════════ .NET 自身的转储（与 WER 是两条腿） ══════════════════

    /// <summary>
    /// CLR 转储开关是否**已经由父进程注入**。
    ///
    /// ⚠️ 只能查，不能在这里设：实测（2026-11-03）在 <c>Main</c> 里调
    /// <c>Environment.SetEnvironmentVariable</c> 完全无效 —— CLR 早在托管入口之前就读完了这些开关。
    /// 所以唯一的设置时机是「本进程被创建之前」，即启动器注入，或本进程带开关重启自身。
    /// </summary>
    public static bool RuntimeDumpArmed =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump"));

    /// <summary>
    /// <b>必须在 <c>Program.Main</c> 的第一行调用。</b>
    ///
    /// <para>
    /// 若 CLR 转储开关没被注入（例如用户直接双击 <c>app\</c> 里的 exe，绕过了启动器），
    /// 就带着开关把自身重启一次，然后让当前进程退出。这样「托管代码崩溃不留现场」这个缺口
    /// 在任何启动方式下都被堵上。
    /// </para>
    ///
    /// <para>
    /// 返回 <c>true</c> = 已交接给子进程，调用方**立即 return**，不要再往下走。
    /// </para>
    ///
    /// <para>
    /// 为什么要这么绕：托管层的访问冲突 / 栈溢出 / <c>FailFast</c> 由 CLR 自己接管
    /// （FailFast 明确绕过 WER），WER LocalDumps 对它无效 —— 实测确认过，进程按访问冲突终止、
    /// 事件日志有 .NET Runtime 1023，但 crashes 目录一份 dump 都没有。
    /// </para>
    /// </summary>
    public static bool TryHandoff(string[] args)
    {
        try
        {
            // 已注入：正常启动。
            if (RuntimeDumpArmed) return false;

            // 调试会话里重启会甩掉断点，开发期本来就有 IDE，不折腾。
            if (Debugger.IsAttached) return false;

            // 显式关闭（自动化 / 便携脚本 / 排查启动慢时用）。
            if (Environment.GetEnvironmentVariable("CSH_NO_REEXEC") == "1") return false;

            // 防重入：正常情况下子进程必然带着开关，走不到这里；
            // 但万一环境变量注入失败，这道闸能避免无限自我重启。
            if (Environment.GetEnvironmentVariable("CSH_REEXEC") == "1") return false;

            // 离屏自检类工具（--mathshot / --resource-check）跑完就退，重启一次毫无意义，
            // 而且会丢掉父进程捕获的 stdout，导致自检结果拿不到。
            if (Array.Exists(args, a => a.Equals("--mathshot", StringComparison.OrdinalIgnoreCase))) return false;
            if (Array.Exists(args, a => a.Equals("--resource-check", StringComparison.OrdinalIgnoreCase))) return false;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            try { Directory.CreateDirectory(DumpDir); } catch { }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                // 与启动器保持一致的定位口径（应用按 AppContext.BaseDirectory 找资源）。
                WorkingDirectory = AppContext.BaseDirectory,
            };

            // ArgumentList 而不是拼字符串：引号/空格的转义交给运行时。
            foreach (var a in args) psi.ArgumentList.Add(a);

            psi.EnvironmentVariables["DOTNET_DbgEnableMiniDump"] = "1";
            // 这里不能用 _fullRequested：它在 Init 里才赋值，而本方法跑在 Init 之前。
            var full = Array.Exists(args, a => a.Equals("--dump-full", StringComparison.OrdinalIgnoreCase))
                       || Environment.GetEnvironmentVariable("CSH_DUMP_FULL") == "1";
            psi.EnvironmentVariables["DOTNET_DbgMiniDumpType"] = full ? "4" : "1";
            psi.EnvironmentVariables["DOTNET_DbgMiniDumpName"] = Path.Combine(DumpDir, "csh.%p.dmp");
            psi.EnvironmentVariables["CSH_REEXEC"] = "1";

            // Process.Start 失败会抛异常（外层已兜）；万一返回 null 也不能当作交接成功，
            // 否则父进程退出、子进程没起来，用户看到的就是「双击没反应」。
            var child = Process.Start(psi);
            return child is not null;
        }
        catch
        {
            // 交接失败就让当前进程照常跑 —— 丢的是崩溃现场，不是程序本身。
            return false;
        }
    }

    /// <summary>记录 CLR 转储开关的实际状态。设置时机不在本进程，这里只负责如实汇报。</summary>
    private static void CheckRuntimeDump()
    {
        if (RuntimeDumpArmed)
        {
            var type = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpType") ?? "1";
            var name = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName") ?? "(默认位置)";
            AppLog.Info("dump", $"CLR 转储｜状态=已启用｜类型={type}｜输出={name}");
        }
        else
        {
            // 不是错误：可能是 CSH_NO_REEXEC / 调试会话 / 交接失败。但必须让人知道这一层是空的。
            AppLog.Warn("dump", "CLR 转储｜状态=未启用｜影响=托管代码崩溃（访问冲突／栈溢出／FailFast）不产生转储"
                                + "｜说明=WER 转储对这类崩溃无效");
        }
    }

    // ══════════════════ 注册表 ══════════════════

    /// <summary>
    /// 写入 <c>LocalDumps</c> 策略。
    ///
    /// <para>
    /// 用户级（HKCU）和机器级（HKLM）**都写**，不提前返回：两者不是同一套生效规则 ——
    /// 教室机不一定每次都提权（HKCU 无需提权，更稳），但部分环境下 WER 只认 HKLM。
    /// 任意一处写成功即视为已启用；HKLM 失败属于正常情况（无管理员权限）。
    /// </para>
    /// </summary>
    private static bool RegisterLocalDumps(out string where, out string error)
    {
        where = "";
        error = "";

        var exe = "";
        try { exe = Path.GetFileName(Environment.ProcessPath ?? ""); }
        catch { /* 取不到就用下面的兜底名 */ }
        if (string.IsNullOrEmpty(exe)) exe = "ClassSoftwareHub.exe";

        var type = _fullRequested ? DumpTypeFull : DumpTypeMini;
        var written = new List<string>();
        var lastError = "";

        foreach (var (root, label) in new[]
                 {
                     (Registry.CurrentUser, "HKCU"),
                     (Registry.LocalMachine, "HKLM"),
                 })
        {
            try
            {
                using var baseKey = root.CreateSubKey(@"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps");
                using var appKey = baseKey?.CreateSubKey(exe);
                if (appKey is null)
                {
                    if (lastError.Length == 0) lastError = label + ": 无法创建子键";
                    continue;
                }

                // 先读后写：三个值都与目标一致就跳过，避免每次启动都写一遍注册表（审计 P2）。
                // HKLM 未提权时写入必失败，先读也能省掉这次无谓尝试；值不同才真正写。
                if (appKey.GetValue("DumpFolder") is string folder
                    && string.Equals(folder, DumpDir, StringComparison.OrdinalIgnoreCase)
                    && appKey.GetValue("DumpType") is int dt && dt == type
                    && appKey.GetValue("DumpCount") is int dc && dc == KeepCount)
                {
                    written.Add(label);
                    continue;
                }

                // DumpFolder 必须是 REG_EXPAND_SZ，WER 才会展开里面的 %LOCALAPPDATA%。
                appKey.SetValue("DumpFolder", DumpDir, RegistryValueKind.ExpandString);
                appKey.SetValue("DumpType", type, RegistryValueKind.DWord);
                appKey.SetValue("DumpCount", KeepCount, RegistryValueKind.DWord);

                written.Add(label);
            }
            catch (Exception e)
            {
                // HKLM 需要管理员，失败是常态，不该盖掉 HKCU 的成功。
                if (lastError.Length == 0) lastError = $"{label}: {e.GetType().Name} {e.Message}";
            }
        }

        if (written.Count > 0)
        {
            where = string.Join("+", written);
            return true;
        }

        error = lastError;
        return false;
    }

    /// <summary>
    /// WER 服务（<c>WerSvc</c>）是否被禁用。被禁用时 LocalDumps 不会生效，崩溃将完全没有现场。
    ///
    /// ⚠️ 走注册表读启动类型而不是 <c>ServiceController</c>：后者在 net6 上要额外引
    ///    <c>System.ServiceProcess.ServiceController</c> 包，为一个诊断读数不值得动依赖清单。
    ///    教室机常做「服务优化」，WerSvc 被改成禁用是最常见的一种，所以这一项必须查。
    /// </summary>
    private static string ProbeWerService()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\WerSvc");
            if (key is null) return "未安装";

            // Start: 2=自动 3=手动 4=已禁用
            var start = key.GetValue("Start") as int? ?? -1;
            return start switch
            {
                2 => "自动（正常）",
                3 => "手动（正常）",
                4 => "已禁用",
                -1 => "未知",
                _ => "启动类型 " + start,
            };
        }
        catch
        {
            return "未知";
        }
    }

    // ══════════════════ 事后处理 ══════════════════

    /// <summary>
    /// 把上次运行期间产生的 dump 报到日志里 —— 这样用户下次打开程序，日志开头就有
    /// 「上次是横死的，dump 在哪」。否则 dump 静静躺在某个目录里，永远不会有人去翻。
    /// </summary>
    private static void ReportPendingDumps()
    {
        try
        {
            if (!Directory.Exists(DumpDir)) return;

            var seen = ReadReported();
            var files = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(DumpDir, "*.dmp", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (seen.Contains(name)) continue;
                try { files.Add(new FileInfo(path)); } catch { }
            }

            if (files.Count == 0) return;

            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            AppLog.Error("crash", $"发现未上报的崩溃转储｜数量={files.Count}｜结论=上次运行为异常终止（正常退出不产生转储）");
            foreach (var f in files)
            {
                AppLog.Error("crash", $"  转储文件={f.Name}｜写入时间={f.LastWriteTime:yyyy-MM-dd HH:mm:ss}｜体积={AppLog.Size(f.Length)}");
                seen.Add(f.Name);
            }

            AppLog.Info("dump", "转储打开方式｜原生=Visual Studio 或 WinDbg｜托管=dotnet-dump analyze");

            // 顺手剔除已被 TrimOldDumps 删掉的条目：否则名单只增不减，
            // 而且将来出现同名转储（进程号被复用）时会被误判成「已上报」而漏报。
            var stale = new List<string>();
            foreach (var n in seen)
            {
                try { if (!File.Exists(Path.Combine(DumpDir, n))) stale.Add(n); } catch { }
            }
            foreach (var n in stale) seen.Remove(n);

            WriteReported(seen);
        }
        catch
        {
            // 上报失败不影响运行。
        }
    }

    private static HashSet<string> ReadReported()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ReportedListPath)) return set;
            foreach (var line in File.ReadAllLines(ReportedListPath))
            {
                var t = line.Trim();
                if (t.Length > 0) set.Add(t);
            }
        }
        catch { }
        return set;
    }

    private static void WriteReported(HashSet<string> set)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var name in set) sb.AppendLine(name);
            File.WriteAllText(ReportedListPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>按时间保留最近 <see cref="KeepCount"/> 份，其余删除。老机器磁盘经不起累积。</summary>
    private static void TrimOldDumps()
    {
        try
        {
            if (!Directory.Exists(DumpDir)) return;

            var all = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(DumpDir, "*.dmp", SearchOption.TopDirectoryOnly))
            {
                try { all.Add(new FileInfo(path)); } catch { }
            }

            if (all.Count <= KeepCount) return;

            all.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));   // 新的在前
            for (var i = KeepCount; i < all.Count; i++)
            {
                try
                {
                    all[i].Delete();
                    AppLog.Info("dump", $"清理旧转储｜文件={all[i].Name}");
                }
                catch { }
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  自测
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 若命令行带 <c>--test-crash</c>，故意制造一次**真正的**访问冲突。
    ///
    /// <para>
    /// ⚠️ 这里踩过一个坑，别改回去：最初用的是 <c>Marshal.ReadInt32(IntPtr.Zero)</c>，
    /// 实测在 .NET 6 上它抛的是**可捕获的** <c>AccessViolationException</c>（Marshal 层做了 null 预检），
    /// 进程活得好好的，完全测不出原生 dump 那条链路。
    /// </para>
    ///
    /// <para>
    /// 现在改成「分配一页 → 改成不可访问 → 往里写」：这是真正的内存保护违规，
    /// CLR 无法预先检查，只能走 native 异常 → failfast → WER 写转储，正是要验证的场景。
    /// 不用 <c>unsafe</c>（工程未开 <c>AllowUnsafeBlocks</c>），全部走 P/Invoke。
    /// </para>
    ///
    /// ⚠️ 只在显式传参时执行，正式使用绝不会触发。
    /// </summary>
    public static void RunSelfTestIfRequested(string[] args)
    {
        if (!Array.Exists(args, a => a.Equals("--test-crash", StringComparison.OrdinalIgnoreCase))) return;

        AppLog.Warn("dump", "崩溃转储自测开始｜延时=3 秒｜动作=制造真实访问冲突");
        AppLog.Warn("dump", $"崩溃转储自测预期｜结果=进程终止｜判据={DumpDir} 下新增 .dmp 文件");

        var t = new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(3000);
            try
            {
                const uint MemCommitReserve = 0x1000 | 0x2000;   // MEM_COMMIT | MEM_RESERVE
                const uint PageReadWrite = 0x04;
                const uint PageNoAccess = 0x01;

                var page = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, MemCommitReserve, PageReadWrite);
                if (page == IntPtr.Zero)
                {
                    AppLog.Error("dump", "崩溃转储自测失败｜阶段=VirtualAlloc｜原因=返回空指针");
                    return;
                }

                if (!VirtualProtect(page, (UIntPtr)4096, PageNoAccess, out _))
                {
                    AppLog.Error("dump", "崩溃转储自测失败｜阶段=VirtualProtect｜原因=未能改为不可访问");
                    return;
                }

                // 真正的内存保护违规 —— CLR 拦不住，进程会当场终止。
                Marshal.WriteInt32(page, 0x41414141);
            }
            catch (Exception e)
            {
                // 正常情况下走不到这里（真正的 AV 不会被托管层捕获）。
                AppLog.Error("dump", "崩溃转储自测失败｜原因=访问冲突被托管层捕获，进程未终止｜异常=" + e.GetType().Name);
            }
        })
        {
            IsBackground = false,
            Name = "csh-crash-selftest",
        };
        t.Start();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
}
