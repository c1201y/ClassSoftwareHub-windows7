#if CSH_CONSOLE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClassSoftwareHub.Desktop.Platform;

/// <summary>
/// 诊断控制台 —— **只在控制台版构建（<c>-p:CshConsoleBuild=true</c>）下编译进来**，
/// 普通 GUI 版里这段代码根本不存在（预处理器整段剔除），所以对正式产物零影响。
///
/// 存在的理由：目标机是老师的 Win7 教室机，「界面不出来 / 点了没反应 / 托盘没图标」
/// 这类症状光看 GUI 什么都看不出来；而日志文件又留在那台机器上，要来回折腾才拿得到。
/// 这个版本把同一个 exe 挂一个 cmd 窗口，实时回显三类信息：
///   1. 启动环境自检（系统 / DPI / 显示器 / 主窗口矩形 / 托盘挂载 / 侧边栏位置）
///   2. 日志目录下**所有** .log 的新增内容（startup / tray / sidebar / brightness / crash …）
///   3. 未捕获异常（含 Avalonia 内部 Trace 日志）
/// 于是「截一张图」就能替代「来回问十句」。
///
/// 设计取向：**零侵入**。不修改任何现有 <c>Log()</c> 实现，改为从文件层面 tail ——
/// 这样既不会漏掉任何一条日志，也不会因为改日志代码把正式版带出新 bug。
///
/// ⚠️ 别把这个版本发给老师 —— 黑窗口会吓到人。它只用来排障。
/// </summary>
internal static class DiagConsole
{
    // ============================================================
    // 状态
    // ============================================================

    /// <summary>所有输出都从这里走，避免 tail 线程和 UI 线程交叉写花屏。</summary>
    private static readonly object Gate = new();

    /// <summary>各日志文件已消费到的字节位置。</summary>
    private static readonly Dictionary<string, long> TailPos = new(StringComparer.OrdinalIgnoreCase);

    private static volatile bool _running;
    private static Thread? _tailThread;

    /// <summary>控制台副本落盘路径 —— 黑窗滚没了/关掉了，用户还能把这个文件发出来。</summary>
    private static string? _mirrorPath;

    // 日志目录：与 <see cref="Services.SettingsStore.Dir"/> 同源，保持单一真源。
    private static string LogDir => Services.SettingsStore.Dir;

    // ============================================================
    // 初始化
    // ============================================================

    /// <summary>架好输出通道 + 打横幅 + 起 tail 线程 + 挂异常兜底。必须在 Avalonia 起来**之前**调用。</summary>
    public static void Init(string[] args)
    {
        try
        {
            PrepareConsole();
            HookGlobalExceptions();
            Banner(args);
            ProbeSiblings();   // 必须排在 tail 之后、Avalonia 之前 —— 单实例判定就在 App 构造函数里
            StartTailing();
        }
        catch
        {
            // 诊断模块自己绝不能成为新的崩溃源 —— 它挂了就静默退化成普通版。
        }
    }

    private static void PrepareConsole()
    {
        try { Console.Title = "ClassSoftwareHub 诊断控制台 · 关闭本窗口＝结束程序"; } catch { }

        // ⚠️⚠️ 中文能不能正常显示，Win7 和 Win10 必须分开处理 —— 这是实机踩出来的（2026-10-01）：
        //
        //   · Win10/11 的 conhost 支持 UTF-8：.NET 侧设 UTF-8、控制台代码页切 65001，
        //     两边对上即可，最省事。
        //   · Win7 的 conhost 是**点阵字体的老实现**，代码页切到 65001 之后中文会整片变乱码
        //     （用户实机反馈原话：「控制台乱码」）。它必须沿用系统本地代码页（简体中文 = 936）。
        //   · 但 .NET Core 默认**不带** 936 编码器：不先注册 CodePages 提供程序的话，
        //     Encoding.GetEncoding(936) 会抛，静默回退成 UTF-8 → 又乱码。所以顺序不能反。
        //
        // 这一步一旦错了，会把真正的报错（比如主窗口构造失败的异常）糊成看不懂的花屏 ——
        // 排障时它是第一个要保证正确的东西。
        try
        {
            if (OsInfo.IsWindows7)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                var cp = GetConsoleOutputCP();
                if (cp == 0) cp = 936;                       // 兜底：中文系统默认代码页
                try { Console.OutputEncoding = Encoding.GetEncoding((int)cp); } catch { }
                SetConsoleOutputCP(cp);                      // 本来就是它，写明以防被上面改歪
            }
            else
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                SetConsoleOutputCP(65001);
            }
        }
        catch { }

        // 缓冲区给宽一点，长路径/长异常不折行折得没法看。
        try
        {
            if (Console.BufferWidth < 140) Console.BufferWidth = 140;
            if (Console.WindowWidth < 120) Console.WindowWidth = 120;
        }
        catch { }

        // 控制台副本：写在日志目录里，但 tail 时会跳过它（否则自己 tail 自己 → 死循环）。
        try
        {
            Directory.CreateDirectory(LogDir);
            _mirrorPath = Path.Combine(LogDir, "console.log");
            WriteMirror($"===== 诊断控制台会话开始 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} =====");
        }
        catch { _mirrorPath = null; }
    }

    // ============================================================
    // 横幅：一次把「这台机器长什么样」说清
    // ============================================================

    private static void Banner(string[] args)
    {
        var asm = Assembly.GetExecutingAssembly();
        var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? asm.GetName().Version?.ToString() ?? "?";
        var build = TryGetBuildTime();

        Rule('=');
        Line("  ClassSoftwareHub · Windows 7 移植版 · 诊断控制台", Kind.Ok);
        Line($"  版本 {ver}    构建于 {build}", Kind.Info);
        Rule('=');

        var proc = Process.GetCurrentProcess();
        Line($" 进程     : {proc.ProcessName}  PID={proc.Id}  {(Environment.Is64BitProcess ? "64" : "32")} 位进程", Kind.Info);
        Line($" CLR      : .NET {Environment.Version}   {(Environment.Is64BitOperatingSystem ? "64" : "32")} 位系统", Kind.Info);
        Line($" 系统     : {OsInfo.Describe()}"
             + $" | DWM合成={(OsInfo.IsDwmCompositionEnabled ? "开" : "关")}"
             + $" | 亚克力支持={(OsInfo.SupportsAcrylicBlur ? "是" : "否")}", Kind.Info);

        // D3D11 是 Avalonia ANGLE 渲染后端的前提。老机器上它经常不可用，而 Avalonia 自己
        // 只会吐一句「No adapters found / Unknown requested PlatformApi 'DirectX11'」——
        // 对用户等于没说。这里直接把结论摆在横幅上。
        Line($" Direct3D11 : {ProbeD3D11()}", Kind.Info);

        // 目标系统是不是 Win7 —— 排障时第一眼要确认的事，单独拎出来。
        if (OsInfo.IsWindows7)
            Line("           ★ 已确认运行在 Windows 7 上（本移植版的主目标系统）", Kind.Ok);
        else
            Line("           ⚠ 当前**不是** Windows 7 —— 请确认是不是拿错机器测了", Kind.Warn);

        Line($" 日志目录 : {LogDir}", Kind.Info);
        Line($" 工作目录 : {Environment.CurrentDirectory}", Kind.Info);
        Line($" 命令行   : {string.Join(" ", args)}", Kind.Info);
        Line($" 启动时刻 : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}", Kind.Info);

        Rule('-');
        Line(" 下面会实时回显日志目录里所有 .log 的新增内容。", Kind.Info);
        Line(" 界面出不来 / 点了没反应的话 —— 把这里的文字（或日志目录下的 console.log）发出来即可定位。", Kind.Warn);
        Line(" ⚠ 关闭本窗口 = 结束整个程序。", Kind.Warn);
        Rule('-');
    }

    // ============================================================
    // 显卡能力探测
    // ============================================================

    /// <summary>
    /// 探一下这台机器的 D3D11 能不能用 —— Avalonia 的 ANGLE 渲染后端就架在它上面。
    ///
    /// 为什么值得单独探：实机上 ANGLE 起不来时只报
    /// 「Unable to initialize ANGLE-based rendering with DirectX11: No adapters found」，
    /// 这句话对用户毫无信息量，也没说清到底是"显卡太老"还是"驱动没装"。
    /// 直接给结论，比让人对着英文短句猜有用得多。
    /// </summary>
    private static string ProbeD3D11()
    {
        try
        {
            // DriverType: 1 = D3D_DRIVER_TYPE_HARDWARE，SDKVersion: 7 = D3D11_SDK_VERSION
            var hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0,
                IntPtr.Zero, 0, 7, out var device, out var level, out var context);

            if (hr == 0)
            {
                // 只是探测，用完立刻还回去，别把设备对象挂在进程里
                if (context != IntPtr.Zero) Marshal.Release(context);
                if (device != IntPtr.Zero) Marshal.Release(device);
                return $"可用（特性级别 0x{(uint)level:X}）";
            }

            return $"⚠ 不可用（HRESULT=0x{hr:X8}）—— 这台机器的显卡/驱动不支持 D3D11。"
                   + "ANGLE 后端是架在 D3D11 上的，所以本次已自动跳过它，直接走 OpenGL → 软件渲染";
        }
        catch (DllNotFoundException)
        {
            return "⚠ 探测失败：系统里没有 d3d11.dll（Avalonia 会走别的渲染后端）";
        }
        catch (Exception ex)
        {
            return "探测失败: " + ex.Message;
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    // ============================================================
    // 单实例探测
    // ============================================================

    /// <summary>
    /// 列出本机所有同名的 ClassSoftwareHub 进程。
    ///
    /// 为什么必须排在最前面：本程序是**单实例**的 —— 只要机器上还挂着一个（最常见的来路是
    /// 「上次点了 × 以为关了，其实 CloseToTray 把它收进了托盘」，而托盘图标一旦看不见就再也找不到入口），
    /// 新双击的这个就会在 3~4 秒后**安安静静地退出**。
    /// 用户看到的现象恰好是「窗口一闪就没了 / 双击没反应 / 主界面不显示」。
    ///
    /// 把这件事在启动第 1 秒就摆到台面上，能省掉一整轮「到底哪儿坏了」的猜测。
    /// </summary>
    private static void ProbeSiblings()
    {
        try
        {
            var self = Environment.ProcessId;
            var name = Process.GetCurrentProcess().ProcessName;

            var siblings = Process.GetProcessesByName(name)
                .Where(p => p.Id != self)
                .ToList();

            if (siblings.Count == 0)
            {
                Line(" 存活实例 : 无其它 ClassSoftwareHub 进程（本次是干净启动）", Kind.Ok);
                return;
            }

            Line($" 存活实例 : ⚠ 检测到本机已有 {siblings.Count} 个 ClassSoftwareHub 正在运行！", Kind.Error);
            Line("           本程序是单实例的。下面逐个看它们**有没有一个看得见的主窗口**：", Kind.Error);
            Line("             · 有   → 会被正常唤醒，本次启动退出；", Kind.Warn);
            Line("             · 没有 → 判定为僵尸实例，会被自动结束，本次启动接管。", Kind.Warn);

            foreach (var p in siblings)
            {
                var extra = "(拿不到详细信息)";
                try
                {
                    var started = p.StartTime.ToString("MM-dd HH:mm:ss");
                    extra = $"启动于 {started}  常驻内存 {p.WorkingSet64 / 1024 / 1024} MB";
                }
                catch { /* 权限不足时取不到，不影响判断 */ }

                Line($"             · PID={p.Id}  {extra}", Kind.Error);

                // ⭐ 这一行才是「界面到底出没出来」的唯一硬证据。
                //    以前只报 PID 和内存，看日志的人分不清「进程活着」和「界面看得见」，
                //    于是被一个"活着但没界面"的僵尸实例坑了整整一天（2026-10-01 PID=4040）。
                var wins = WindowProbe.ForPids(new[] { p.Id });
                if (wins.Count == 0)
                {
                    Line("               └ 顶层窗口: 一个都没有（界面从未创建出来）", Kind.Error);
                    continue;
                }

                foreach (var w in wins)
                {
                    Line($"               └ 窗口: {w.Describe()}"
                         + (w.LooksLikeMainWindow ? "  ←★ 这就是主窗口" : ""),
                         w.LooksLikeMainWindow ? Kind.Ok : Kind.Warn);
                }

                if (!wins.Any(w => w.LooksLikeMainWindow))
                {
                    Line("               └ ⚠ 没有任何一个像主窗口 —— 界面其实是看不见的（僵尸态）", Kind.Error);
                }
            }

            Line("           新版会自己处理：拿不出窗口的实例会被自动结束，本次启动继续，不必手工开任务管理器。", Kind.Warn);
            Line("", Kind.Info);
        }
        catch
        {
            // 权限不足等情况取不到进程列表 —— 不影响其它诊断
        }
    }

    private static string TryGetBuildTime()
    {
        try
        {
            var path = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch { }
        return "未知";
    }

    // ============================================================
    // 日志尾部跟踪（tail -f）
    // ============================================================

    private static void StartTailing()
    {
        _running = true;
        _tailThread = new Thread(TailLoop)
        {
            IsBackground = true,          // 后台线程：主程序退出时不会拖住进程
            Name = "csh-diag-tail",
            Priority = ThreadPriority.BelowNormal
        };
        _tailThread.Start();
    }

    private static void TailLoop()
    {
        while (_running)
        {
            try
            {
                if (Directory.Exists(LogDir))
                {
                    foreach (var file in Directory.EnumerateFiles(LogDir, "*.log"))
                    {
                        // 跳过自己的副本文件，否则就变成「自己读自己写」的死循环。
                        if (_mirrorPath is not null &&
                            string.Equals(file, _mirrorPath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        Consume(file);
                    }
                }
            }
            catch { /* 单轮扫描失败无所谓，下一轮继续 */ }

            try { Thread.Sleep(400); } catch { }
        }
    }

    private static void Consume(string path)
    {
        long length;
        try { length = new FileInfo(path).Length; }
        catch { return; }

        var name = Path.GetFileName(path);

        // 第一次见到某个文件：只记下当前长度，不倒历史内容 ——
        // 不然上次运行留下的几百行会把本次启动的输出顶出屏幕。
        if (!TailPos.TryGetValue(path, out var pos))
        {
            TailPos[path] = length;
            Line($"   · 已开始跟踪 {name}（跳过已有 {length} 字节的历史内容）", Kind.Echo);
            return;
        }

        // 文件被清空/重建（长度倒退）→ 从头重读。
        if (length < pos)
        {
            pos = 0;
        }

        if (length == pos) return;

        try
        {
            // ⚠️ 必须带 FileShare.ReadWrite|Delete：日志写入方（我们的 Log()）持有的句柄
            //    允许并发读，但如果我们自己用独占方式打开就会撞车，把对方的写入顶失败。
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (pos > 0) fs.Seek(pos, SeekOrigin.Begin);

            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                Line($" [{name}] {line}", ClassifyLog(name, line));
            }

            // 用 fs.Position 而不是 length：万一读的过程中对方又追加了，
            // 我们只认真正读到的位置，剩下的下一轮再补，不会丢。
            TailPos[path] = fs.Position;
        }
        catch
        {
            // 读失败就保持原位置，下一轮重试（大概率只是刚好被占用）。
        }
    }

    /// <summary>按文件名 + 行内容给日志着色，扫一眼就能找到红的那几行。</summary>
    private static Kind ClassifyLog(string fileName, string line)
    {
        if (line.Contains("异常") || line.Contains("失败") || line.Contains("错误") ||
            line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("FAIL", StringComparison.Ordinal))
            return Kind.Error;

        if (fileName.Equals("crash.log", StringComparison.OrdinalIgnoreCase))
            return Kind.Error;

        if (line.Contains("警告") || line.Contains("没挂上") || line.Contains("看不见") ||
            line.Contains("Warn", StringComparison.OrdinalIgnoreCase))
            return Kind.Warn;

        if (fileName.Equals("startup.log", StringComparison.OrdinalIgnoreCase))
            return Kind.Highlight;

        return Kind.Echo;
    }

    // ============================================================
    // 异常兜底
    // ============================================================

    private static void HookGlobalExceptions()
    {
        // 1) Avalonia 的 LogToTrace 走 Trace —— 接过来就能看到「平台初始化为什么失败」这类关键信息。
        try { Trace.Listeners.Add(new AvaloniaTraceListener()); } catch { }

        // 2) 任意线程上漏网的异常（Avalonia 的 Dispatcher 异常在 App.axaml.cs 里另有处理）。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Fatal("进程级未处理异常（即将终止）", e.ExceptionObject as Exception);
        };

        // 3) 被丢弃的 Task 异常 —— 默认静默，这里让它出声。
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Line(" 后台任务未观察异常: " + e.Exception.Message, Kind.Error);
            e.SetObserved();
        };

        // 4) 进程退出兜底。
        //    ⚠️ 单实例那条路径走的是 Environment.Exit()，它**不会**执行 try/finally 里的收尾，
        //       所以退出原因必须靠这条事件补记（同时保证 console.log 里留下痕迹）。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { NoteExit("进程结束"); } catch { }
        };
    }

    private static bool _exitNoted;
    private static volatile bool _mainShown;

    /// <summary>
    /// 主窗口**第一次真正显示出来**时打点（由 MainWindow 的首次 Activated 调用）。
    ///
    /// 为什么不直接看 <c>MainWindow.IsVisible</c>：退出那一刻窗口往往已经被关掉了，
    /// IsVisible 必然是 false，会把"用户正常用完主动关闭"误判成"界面没出来"。
    /// 只有「曾经显示过」这个事实才是判断依据。
    /// </summary>
    public static void MarkMainWindowShown() => _mainShown = true;

    /// <summary>
    /// 记一次“进程即将退出”及其原因。幂等 —— 重复调用只输出第一条。
    ///
    /// 关键信息是「主窗口到底露过脸没有」：露过 = 正常收工；没露过 = 用户从头到尾没看见界面，
    /// 那正是要抓的 bug，控制台这时不该跟着一起消失。
    /// </summary>
    public static void NoteExit(string reason)
    {
        if (_exitNoted) return;
        _exitNoted = true;

        Line("", Kind.Info);
        Rule('=');
        Line(" 进程即将退出", Kind.Warn);
        Line(" 退出原因: " + reason, Kind.Warn);
        Line(" 主窗口本次是否显示过: " + (_mainShown ? "是" : "否 —— 界面从头到尾没出来，这就是要抓的问题"),
             _mainShown ? Kind.Ok : Kind.Error);
        Rule('=');
    }

    /// <summary>致命错误：整块打出来（含完整堆栈），并落盘。</summary>
    public static void Fatal(string title, Exception? ex)
    {
        Rule('!');
        Line(" 致命错误: " + title, Kind.Error);
        if (ex is not null)
        {
            Line(" 类型: " + ex.GetType().FullName, Kind.Error);
            Line(" 消息: " + ex.Message, Kind.Error);
            Line(" 堆栈:", Kind.Error);
            foreach (var l in (ex.ToString() ?? "").Split('\n'))
                Line("   " + l.TrimEnd(), Kind.Error);
        }
        Rule('!');
    }

    /// <summary>启动失败后停住，别让黑窗一闪而过 —— 用户来得及截图。</summary>
    public static void Pause()
    {
        try
        {
            Line("", Kind.Info);
            Line(" 主程序已退出。按任意键关闭本窗口…", Kind.Warn);
            if (!Console.IsInputRedirected) Console.ReadKey(intercept: true);
        }
        catch { }
    }

    public static void Shutdown()
    {
        _running = false;
        try
        {
            Line($" 程序正常退出 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}", Kind.Ok);
            WriteMirror($"===== 诊断控制台会话结束 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} =====\r\n");

            // 界面从头到尾没露过脸，进程却自己收工了 —— 这正是「双击后一闪而过」的样子。
            // 别让控制台跟着消失，把最后的现场留在屏幕上等人看。
            if (!_mainShown)
            {
                Line("", Kind.Error);
                Line(" ★ 主窗口从未显示过，进程却要退出了 —— 十有八九就是问题本身。", Kind.Error);
                Line("   上面最后几行（尤其是红色/黄色）就是线索。", Kind.Error);
                Pause();
            }
        }
        catch { }
    }

    // ============================================================
    // 输出原语
    // ============================================================

    public enum Kind { Info, Ok, Warn, Error, Echo, Highlight, Trace }

    public static void Line(string text, Kind kind = Kind.Info)
    {
        lock (Gate)
        {
            try
            {
                var keep = Console.ForegroundColor;
                try { Console.ForegroundColor = ColorOf(kind); } catch { }
                Console.WriteLine(text);
                try { Console.ForegroundColor = keep; } catch { }
            }
            catch { }

            WriteMirror(text);
        }
    }

    public static void Rule(char c = '-') => Line(new string(c, 78), Kind.Info);

    private static ConsoleColor ColorOf(Kind k) => k switch
    {
        Kind.Ok => ConsoleColor.Green,
        Kind.Warn => ConsoleColor.Yellow,
        Kind.Error => ConsoleColor.Red,
        Kind.Echo => ConsoleColor.Gray,
        Kind.Highlight => ConsoleColor.Cyan,
        Kind.Trace => ConsoleColor.DarkGray,
        _ => ConsoleColor.White
    };

    /// <summary>
    /// 把 Avalonia 内部 Trace 日志接到控制台。
    ///
    /// ⚠️ 必须过滤：<c>LogToTrace</c> 一开详细级别，<c>[Layout]</c> 就每帧刷好几条，
    ///    十几秒就能把真正有用的诊断行顶出屏幕。排障要的是「平台/字体/绑定哪儿不对」，
    ///    不是布局管理器的心跳。
    /// </summary>
    private sealed class AvaloniaTraceListener : TraceListener
    {
        /// <summary>噪音区域黑名单（用黑名单而不是白名单，免得漏掉没见过的关键告警）。</summary>
        private static readonly string[] Muted = { "[Layout]", "[Render]" };

        /// <summary>只收整行 —— 半行拼接出来的日志读起来毫无意义。</summary>
        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            // Avalonia 的日志一律以 "[区域]" 开头，不是这个形状的就是别的库的 Trace，放行也无妨。
            if (!message.StartsWith('[')) return;

            foreach (var m in Muted)
                if (message.StartsWith(m, StringComparison.Ordinal)) return;

            Line("  · Avalonia " + message.Trim(), Kind.Trace);
        }
    }

    /// <summary>把控制台内容同步写一份到 console.log（tails 时会跳过该文件）。</summary>
    private static void WriteMirror(string text)
    {
        if (_mirrorPath is null) return;
        try { File.AppendAllText(_mirrorPath, text + "\r\n", new UTF8Encoding(false)); }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    /// <summary>读控制台当前输出代码页（简体中文系统一般是 936）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleOutputCP();
}
#endif
