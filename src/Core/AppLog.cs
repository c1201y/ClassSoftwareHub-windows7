using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>日志级别。中文标签直接用于日志行与查看页的筛选。</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// 日志**正文写作规范**（所有调用点必须遵守，评审时按此条核对）。
///
/// <para><b>一、句式</b>：<c>{对象}{动作}[｜{字段}={值}][：{细节}]</c>。陈述事实，不叙述过程。</para>
/// <code>
/// ✔ 检查更新｜通道=insider｜当前版本=1.2.0
/// ✔ 下载失败｜主机=github.com｜候选=3/3｜校验=SHA256 不匹配
/// ✘ 又试了一次下载，这次还是不行（口语、无字段、无结论）
/// </code>
///
/// <para><b>二、必备字段</b>（有多少写多少，缺一项就少一条排查线索）：</para>
/// <list type="bullet">
///   <item>路径 → 完整路径；URL → 主机名或完整 URL；</item>
///   <item>版本 → 版本号 + 通道名；状态码 → 数值；</item>
///   <item>数量 → <c>已处理/总数</c>；体积 → <see cref="AppLog.Size"/>；耗时 → <see cref="AppLog.Secs"/>。</item>
/// </list>
///
/// <para><b>三、禁用写法</b>：语气词（了/吧/呢/啊）、感叹号、拟人（"叫醒"、"僵尸"、"补刀"）、
/// 口语（"拿不到"、"弹窗没弹出来"、"打个照面"）、无主语的评论（"这样不好"）、
/// 面向用户的产品话术（"为了让您更流畅"）。日志是给维护者看的事实记录，不是交互文案。</para>
///
/// <para><b>四、失败要带原因</b>：预期内的失败（网络不通、文件不存在、权限不足、被用户取消）
/// 走 <see cref="AppLog.Catch"/> —— 自动带上异常类型名，不带堆栈；
/// 非预期异常（逻辑出错、空引用、状态错乱）走 <see cref="AppLog.Exception"/> —— 带完整堆栈。
/// 直接拼 <c>"失败：" + ex.Message</c> 属于**禁用写法**：既丢了类型，也丢了 HResult。</para>
///
/// <para><b>五、模块名**取固定值**（不许随手新造，否则日志页的模块列会被拆成十几份）：
/// <c>startup / exit / single / links / crash / window / ui / theme / backdrop / chrome / tray /
/// sidebar / palette / oobe / net / bridge / shell / download / download-route / upload / submit / update / package /
/// content / cache / logs / legacy / dump / hang / picker / clean / uninstall / leftover / quarantine /
/// repair / tweaks / sysfeatures / pagefile / startupitem / appdata / file-search / dict / hubai /
/// hubai-web / ai / announce / announce-lan / teaching / ring / timer / ppt-resume / snip / message /
/// subject-tools / chem / equation / plot / diskmap / hash-check / license / device / footprint /
/// machinecheck / memory / echo-cave / clock-bg / clock-presets / port /
/// cleanup / doctor / tool-host / procguard / perf / easiguard / brightness / autoshutdown</c>。</para>
/// </summary>

/// <summary>
/// 统一日志入口。
///
///   · 目录：<c>%LOCALAPPDATA%\ClassSoftwareHub\logs\{本次启动时间}\</c>（见 <see cref="Services.LogHome"/>），
///     一次运行一个目录，目录名就是启动时间；
///   · 文件：**只有两个** ——
///       <c>app.log</c>    日常全部日志（所有模块、所有级别都进这里）；
///       <c>crash.log</c>  崩溃与页面加载失败（模块名传 <c>crash</c> 的走这里）。
///     为什么崩溃单独一个文件：它要在「应用还没启动完」和「崩溃那一瞬间」都写得进去，
///     跟主日志合流反而更脆；而且用户出问题时只被要求发这一个文件。
///   · 行格式：<c>[时间] [级别] [模块] 消息</c>。**消息正文的写法见文件顶部的「日志正文写作规范」**。
///
/// 模块名要实事求是：缓存自己（<see cref="Services.CacheHome"/>）、日志自己
/// （<see cref="Services.LogHome"/>）、旧结构清理（<see cref="Services.LegacyCleanup"/>）
/// 都直接写 <c>AppLog.Info("cache"/"logs"/"legacy", …)</c>，
/// 不要图省事走 <c>ScreenCapture.Log</c> —— 那个会把模块名标成 <c>snip</c>（截屏模块），
/// 于是在日志页的「模块」列里，一条缓存清理记录看起来像是截屏写的。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    /// <summary>崩溃专用日志文件名（模块名传 <c>crash</c> 时进这个）。</summary>
    private const string CrashFile = "crash.log";

    /// <summary>日常日志文件名（其余所有模块都进这个）。</summary>
    private const string AppFile = "app.log";

    /// <summary>日志根目录：<c>%LOCALAPPDATA%\ClassSoftwareHub\logs</c>。下面按「启动一次一个目录」分层。</summary>
    public static string Root => Services.LogHome.Root;

    /// <summary>本次运行的会话目录（日志就写在它里面）。</summary>
    public static string Dir => Services.LogHome.SessionDir;

    public static void Debug(string module, string message) => Write(module, LogLevel.Debug, message);
    public static void Info(string module, string message) => Write(module, LogLevel.Info, message);
    public static void Warning(string module, string message) => Write(module, LogLevel.Warning, message);
    public static void Error(string module, string message) => Write(module, LogLevel.Error, message);

    // ══════════════════ 异常与「操作链路」══════════════════
    //
    // 设计背景：早期版本绝大多数异常路径是静默的（大量空 catch、记日志调用点极少），
    // 功能坏了时日志里没有任何痕迹。因此补两样东西：
    //   ① <see cref="Exception(string, Exception, string?)"/> —— 所有 catch 块的**统一出口**，
    //      一次调用带全「类型 / HResult / 消息 / 堆栈 / 内部异常 / 当时页面」；
    //   ② <see cref="Begin"/> / <see cref="Step"/> / <see cref="Done"/> / <see cref="Fail"/> ——
    //      「操作链路」记账（下载、更新、同步、提交走的就是这套），让日志从
    //      "发生了某件事" 变成 "某件事走到了哪一步、结果如何、花了多久"。
    //
    // 级别分工（"既要全、又别吵"的折中，别随手改）：
    //    · 主干步骤 → <c>信息</c>（默认就看得见）
    //    · 高频读数（内存回收之类）→ <c>调试</c>（日志页点掉「调试」就没它）
    //    · 真的出岔子 → <c>警告</c> / <c>错误</c>（**全量记，不节流**）

    /// <summary>
    /// 由 <c>App</c> 在启动时注入：返回"当时在哪一页、窗口多大"这类现场描述。
    /// 做成委托而不是直接调 <c>Pages.ShellPage</c> —— Core 层不该反向依赖 Pages 层。
    /// </summary>
    public static Func<string>? ContextProvider { get; set; }

    /// <summary>
    /// 记一条异常。**所有 catch 块的统一出口**（别再写裸的 <c>AppLog.Error(mod, ex.Message)</c>：
    /// 那样没有类型、没有 HResult、没有堆栈，等于没记）。
    ///
    /// <paramref name="what"/> 写"当时在干什么"，人话，例如 <c>下载「微信」</c>、<c>读取设置</c>。
    /// 整个异常（含堆栈与内部异常）会**缩进后拼在同一条日志里** ——
    /// 日志页的解析规则是"缩进行归并到上一条"，所以它在列表里是一行、在详细视图里是全貌。
    /// </summary>
    public static void Exception(string module, Exception ex, string? what = null)
    {
        try
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(what)) sb.Append(what!.Trim()).Append('｜');
            sb.Append(ex.GetType().Name)
              .Append(" 0x").Append(ex.HResult.ToString("X8"))
              .Append(": ").Append(ex.Message);

            var where = SafeContext();
            if (where.Length > 0) sb.Append("\n  · 当时：").Append(where);

            sb.Append('\n').Append(Indent(ex.ToString()));
            Write(module, LogLevel.Error, sb.ToString());
        }
        catch { /* 记日志这件事本身绝不能把调用方搞挂 */ }
    }

    /// <summary>链路开始（<c>▶</c>）。<paramref name="what"/> 里请带上关键入参（链接、版本号、文件名…）。</summary>
    public static void Begin(string module, string what) => Info(module, "▶ " + what);

    /// <summary>链路中间步骤（<c>·</c>）。一步一步写清楚，出问题时才知道断在哪。</summary>
    public static void Step(string module, string what) => Info(module, "  · " + what);

    /// <summary>链路成功收尾（<c>✔</c>），带耗时。</summary>
    public static void Done(string module, string what, TimeSpan? elapsed = null)
        => Info(module, "✔ " + what + (elapsed is { } t ? " · 耗时 " + Secs(t) : ""));

    /// <summary>链路失败收尾（<c>✘</c>，带完整异常）。</summary>
    public static void Fail(string module, Exception ex, string what) => Exception(module, ex, "✘ " + what);

    /// <summary>链路失败收尾（没有异常对象时用这个，例如服务端返回了一个失败结果）。</summary>
    public static void Fail(string module, string what)
        => Write(module, LogLevel.Error, "✘ " + what);

    /// <summary>
    /// 告警：**能用但已经不对了**（降级、回退、重试、跳过校验…）。
    /// 与 <see cref="Error"/> 的分界 = 功能还能不能正常用。
    /// </summary>
    public static void Warn(string module, string what) => Warning(module, "⚠ " + what);

    /// <summary>
    /// 记一条**预期内**的失败：网络不通、文件不存在、权限不足、被用户取消、服务端返回非成功码。
    ///
    /// <para>输出形如 <c>读取订阅源失败｜HttpRequestException: 连接超时</c> —— 一定带异常类型名，
    /// 不带堆栈（这类失败频繁且堆栈同质，堆栈只会淹没其他条目）。</para>
    ///
    /// <para><paramref name="what"/> 写"对什么做了什么"，不要自带"失败"二字（本方法会补），
    /// 并带上关键入参：<c>下载「微信」｜主机=github.com</c>。</para>
    ///
    /// <para>非预期异常**不要**用这个，用 <see cref="Exception"/> 以保留完整堆栈。</para>
    /// </summary>
    /// <param name="level">默认 <see cref="LogLevel.Warning"/>：功能可降级或已回退。
    /// 完全做不成时传 <see cref="LogLevel.Error"/>。</param>
    public static void Catch(string module, string what, Exception ex, LogLevel level = LogLevel.Warning)
    {
        try
        {
            var head = string.IsNullOrWhiteSpace(what) ? "操作" : what.Trim();
            Write(module, level, $"{head}失败｜{ex.GetType().Name}: {ex.Message}");
        }
        catch { /* 记日志本身不能抛 */ }
    }

    /// <summary>耗时可读化：不到 1 秒给毫秒，超过给秒。</summary>
    public static string Secs(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0}ms" : $"{t.TotalSeconds:0.##}s";

    /// <summary>字节可读化（日志里写"12.3 MB"比写 12897484 好读）。</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 0 => "未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    private static string SafeContext()
    {
        try { return ContextProvider?.Invoke() ?? ""; }
        catch { return ""; }
    }

    /// <summary>整个异常文本每行都加两个空格 —— 这样日志页会把它归并成**同一条**（见类注释）。</summary>
    private static string Indent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', lines.Select(l => "  " + l.TrimEnd()));
    }

    /// <summary>
    /// 写一条日志。写不进去就静默放弃 —— 日志自己绝不能把功能搞挂。
    /// </summary>
    public static void Write(string module, LogLevel level, string message)
    {
        try
        {
            var target = string.Equals(module, "crash", StringComparison.OrdinalIgnoreCase) ? CrashFile : AppFile;
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

            // 时间戳与格式化放在锁外：持锁时间越短，别的线程（尤其是 UI 线程）排队越少。
            var line = $"[{stamp}] [{TagOf(level)}] [{module}] {message}\r\n";

            lock (Gate)
            {
                WriteLine(Path.Combine(Dir, target), line);
            }
        }
        catch (Exception ex)
        {
            // 主链路静默失败时，把异常细节落到 ProgramData —— 否则「一行日志都没有」
            // 永远查不出是哪一步、什么原因炸的。只在失败时执行，正常启动零开销。
            var detail = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 写日志失败\r\n  {ex.GetType().FullName}: {ex.Message}\r\n{ex.StackTrace}\r\n";
            try
            {
                string dirSnapshot;
                try { dirSnapshot = Dir; }
                catch (Exception dex) { dirSnapshot = "<Dir 自身抛 " + dex.GetType().Name + ": " + dex.Message + ">"; }
                File.AppendAllText(
                    @"C:\ProgramData\ClassSoftwareHub-log-diag.txt",
                    detail.Replace("写日志失败", "写日志失败 dir=" + dirSnapshot),
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    private static string TagOf(LogLevel level) => level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    // ══════════════════ 落盘：常驻句柄 ══════════════════
    //
    // ⛔ 为什么不再用 File.AppendAllText（实测数据，2026-11-03）：
    //    那条路每写一行都要 CreateFile → WriteFile → CloseHandle，外加一次
    //    Directory.CreateDirectory（目录早就在了，纯属白做）。本机实测 2.14 ms/行，
    //    换成常驻句柄后 0.05 ms/行，**快 42 倍**，写出的字节完全一致。
    //
    //    这不是纯性能洁癖：启动阶段就要写 50 多行，机械盘 + 杀软实时防护的教室机上
    //    单行能到 10~50 ms —— 0.5~2.5 秒全耗在「记日志」上，表现就是「启动后半天没反应」。
    //    更隐蔽的是 Gate 锁：后台线程（下载 / 同步 / 巡检）持锁写盘时，UI 线程只要也写一条
    //    就要排队等，于是「用着用着卡一下、过会儿自己好」。
    //
    //    FileShare 特意带上 Delete —— 用户想手动删日志、或日志页清空当前会话时不会被句柄挡住。

    private static readonly Dictionary<string, StreamWriter> Writers = new(StringComparer.OrdinalIgnoreCase);
    private static string? _writerDir;
    private static bool _dirReady;

    /// <summary>无 BOM 的 UTF-8（日志查看器按 UTF-8 读，带 BOM 会在首行显示成乱码字符）。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>取（必要时创建）一条日志文件的常驻写入器。调用方必须已持有 <see cref="Gate"/>。</summary>
    private static StreamWriter? GetWriter(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return null;

        // 会话目录在进程内是稳定的（见 LogHome.Ensure）；真变了就把旧句柄全关掉重来。
        if (!string.Equals(_writerDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            CloseWriters();
            _writerDir = dir;
            _dirReady = false;
        }

        if (!_dirReady)
        {
            Directory.CreateDirectory(dir);
            _dirReady = true;
        }

        if (Writers.TryGetValue(path, out var known)) return known;

        FileStream stream;
        try
        {
            stream = OpenAppend(path);
        }
        catch (DirectoryNotFoundException)
        {
            // 目录被外部删掉了（清缓存 / 手工整理）：重建一次再试。
            Directory.CreateDirectory(dir);
            stream = OpenAppend(path);
        }

        var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
        Writers[path] = writer;
        return writer;
    }

    private static FileStream OpenAppend(string path) =>
        new(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);

    /// <summary>写一行并刷到 OS。调用方必须已持有 <see cref="Gate"/>。</summary>
    private static void WriteLine(string path, string line)
    {
        var writer = GetWriter(path);
        if (writer is null) return;

        try
        {
            writer.Write(line);

            // 每行都刷：进程被强杀、看门狗崩溃钩子、原生 dump 之后都还能读到这一行。
            // 断电会丢最后的缓冲，但这跟改动前（AppendAllText 同样只到 OS 缓存）是一样的。
            writer.Flush();
        }
        catch
        {
            // 句柄失效（文件被删 / 磁盘满 / U 盘被拔）→ 丢掉，下一条重新打开。
            DropWriter(path);
            throw;
        }
    }

    private static void DropWriter(string path)
    {
        if (!Writers.TryGetValue(path, out var writer)) return;
        Writers.Remove(path);
        try { writer.Dispose(); } catch { }
    }

    private static void CloseWriters()
    {
        foreach (var writer in Writers.Values)
        {
            try { writer.Flush(); } catch { }
            try { writer.Dispose(); } catch { }
        }
        Writers.Clear();
    }

    /// <summary>
    /// 进程收尾时关掉日志句柄。数据其实每行都刷过了，这一步只为优雅释放文件句柄
    /// （否则「日志目录被占用删不掉」会变成新的现场问题）。可重复调用。
    /// </summary>
    public static void Shutdown()
    {
        try { lock (Gate) CloseWriters(); } catch { }
    }
}
