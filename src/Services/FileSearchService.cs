using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>一条文件快搜结果（只给界面用，按需取文件属性）。</summary>
public sealed class FileSearchItem
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>所在目录（结果行第二行显示）。</summary>
    public string Folder { get; init; } = "";
    /// <summary>右侧小字：文件夹显示修改日期，文件显示“大小 · 日期”。</summary>
    public string Meta { get; init; } = "";
    /// <summary>E8B7 = 文件夹，E8A5 = 文件（只走老码位，ED 段在 Win7 是空框）。</summary>
    public string Glyph { get; init; } = "\uE8A5";
    public bool IsDir { get; init; }
}

/// <summary>
/// 「文件快搜」的索引与查询（2026-10-29，insider1.4 新增）。
///
/// <para>
/// 设计取舍：不引入 Everything 的 SDK，也不调用需要管理员权限的 NTFS USN/MFT 接口 ——
/// 教室机多数是普通账户，带 UAC 提权的功能在第 5 分钟就会被关掉。这里用**后台目录遍历**
/// 建一份常驻内存的路径索引，并把结果缓存到本机数据目录：
///   · 第一次启动在后台建索引，界面可以先搜已收录的部分；
///   · 之后启动直接读缓存（毫秒级就绪），缓存超过 24 小时才在后台悄悄重建。
/// </para>
///
/// <para>
/// 索引只收**路径**（不含大小 / 时间）：目录遍历里取一次大小等于每个文件多一次系统调用，
/// 在老机械盘上会把建索引时间拉长好几倍。结果行的大小与日期是查出来之后对前 200 条现取的。
/// </para>
///
/// <para>
/// 默认范围 = 所有已就绪的固定磁盘与可移动磁盘。刻意跳过 Windows、回收站、WindowsApps
/// 这类“搜到了也不会去点”的目录 —— 它们往往占掉索引的一半以上，却对课堂使用没有价值。
/// 用户可在「文件快搜」页里只勾选部分磁盘。
/// </para>
/// </summary>
public sealed class FileSearchService
{
    public static FileSearchService Current { get; } = new();

    /// <summary>一次搜索最多返回多少条（再多界面也不会看）。</summary>
    public const int MaxResults = 200;

    /// <summary>索引条目硬上限，防止个别机器上目录爆炸把内存吃光。</summary>
    private const int MaxEntries = 600_000;

    /// <summary>缓存有效期：超过这个时长后在后台重建（旧索引在重建完成前仍然可用）。</summary>
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

    /// <summary>状态 / 结果数变化（可能来自后台线程）。界面请自行切回 UI 线程。</summary>
    public event Action? Changed;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _started;

    private volatile Entry[] _entries = Array.Empty<Entry>();
    private volatile string _phase = PhaseIdle;   // idle | loading | indexing | ready | failed
    private volatile int _count;
    private volatile bool _fromCache;
    private long _elapsedMs;
    private string _scope = "";
    private string _failure = "";
    private long _lastNotifyTicks;

    private const string PhaseIdle = "idle";
    private const string PhaseLoading = "loading";
    private const string PhaseIndexing = "indexing";
    private const string PhaseReady = "ready";
    private const string PhaseFailed = "failed";

    /// <summary>索引是否正在建立 / 读取缓存。</summary>
    public bool IsBusy => _phase is PhaseLoading or PhaseIndexing;

    /// <summary>已收录条目数。</summary>
    public int Count => _count;

    public string ScopeText => _scope;

    /// <summary>给界面那一行状态文字。</summary>
    public string StatusText => _phase switch
    {
        PhaseLoading => "正在读取文件索引…",
        PhaseIndexing => $"正在建立文件索引：已收录 {_count:N0} 项…",
        PhaseReady => $"索引就绪：{_count:N0} 项 · 用时 {_elapsedMs / 1000.0:0.0} 秒"
                      + (_fromCache ? "（来自缓存）" : "")
                      + (string.IsNullOrEmpty(_scope) ? "" : " · " + _scope),
        PhaseFailed => "文件索引建立失败：" + _failure,
        _ => "尚未建立文件索引"
    };

    // ── 对外入口 ────────────────────────────────────────────────────────

    /// <summary>
    /// 开始建立索引（幂等）。<paramref name="delayMs"/> 用于开机时错开启动高峰 ——
    /// 应用启动那几秒要抢着画首帧、读内容包，索引晚几秒再碰磁盘。
    /// </summary>
    public void EnsureStarted(int delayMs = 0)
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
                Build(useCache: true);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        });
    }

    /// <summary>重建索引（清缓存、重新遍历）。界面上那颗「重建索引」走这里。</summary>
    public void Rebuild()
    {
        lock (_gate)
        {
            _started = true;
            try { _cts?.Cancel(); } catch { }
        }

        _ = Task.Factory.StartNew(() =>
        {
            try { Build(useCache: false); }
            catch (Exception ex) { Fail(ex); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>按当前设置的范围重建（磁盘勾选变了之后调用）。</summary>
    public void RebuildForScopeChange()
    {
        try { File.Delete(CachePath); } catch { }
        Rebuild();
    }

    /// <summary>
    /// 查询。扫描在后台线程上跑，UI 线程只负责把结果贴上去；
    /// 250k 条目一次查询大约几十毫秒，输入防抖交给界面。
    /// </summary>
    public async Task<IReadOnlyList<FileSearchItem>> SearchAsync(
        string? query, int limit = MaxResults, CancellationToken ct = default)
    {
        var terms = SplitTerms(query);
        if (terms.Length == 0) return Array.Empty<FileSearchItem>();

        var snapshot = _entries;
        if (snapshot.Length == 0) return Array.Empty<FileSearchItem>();

        limit = Math.Clamp(limit, 1, MaxResults);
        return await Task.Run(() => SearchCore(snapshot, terms, limit, ct), ct).ConfigureAwait(false);
    }

    /// <summary>用系统默认程序打开结果；返回是否成功。</summary>
    public static bool TryOpen(FileSearchItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("file-search", "打开失败：" + item.Path + " → " + ex.Message);
            return false;
        }
    }

    /// <summary>在资源管理器里定位到该结果（选中，而不是只打开父目录）。</summary>
    public static bool TryReveal(FileSearchItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path)) return false;

        try
        {
            if (File.Exists(item.Path) || Directory.Exists(item.Path))
            {
                // /select, 对文件夹会打开父目录并选中它本身 —— 正是“所在位置”的意思
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"")
                {
                    UseShellExecute = true
                });
                return true;
            }

            var folder = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("file-search", "定位失败：" + item.Path + " → " + ex.Message);
        }

        return false;
    }

    private static string[] SplitTerms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToArray();

    private static IReadOnlyList<FileSearchItem> SearchCore(
        Entry[] entries, string[] terms, int limit, CancellationToken ct)
    {
        var hits = new List<Hit>(Math.Min(entries.Length, 4096));
        for (var i = 0; i < entries.Length; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();

            var entry = entries[i];
            var score = Score(entry, terms);
            if (score < 0) continue;

            hits.Add(new Hit(i, score));
            if (hits.Count > 200_000) break;    // 极宽泛的查询（如单个字母）不追求完整排序
        }

        if (hits.Count == 0) return Array.Empty<FileSearchItem>();

        hits.Sort((a, b) => b.Score.CompareTo(a.Score));

        var list = new List<FileSearchItem>(Math.Min(limit, hits.Count));
        for (var i = 0; i < hits.Count && list.Count < limit; i++)
        {
            if ((i & 0x3F) == 0) ct.ThrowIfCancellationRequested();
            list.Add(Materialize(entries[hits[i].Index]));
        }

        return list;
    }

    /// <summary>
    /// 打分：文件名命中（完全相同 &gt; 前缀 &gt; 包含）远高于只有目录名命中；
    /// 浅层路径、名字更短的结果略微靠前。所有关键词都得在完整路径里出现。
    /// </summary>
    private static int Score(in Entry entry, string[] terms)
    {
        var path = entry.Path;
        var name = path.AsSpan(entry.NameStart);
        var score = 0;

        foreach (var term in terms)
        {
            if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return -1;

            if (name.Equals(term, StringComparison.OrdinalIgnoreCase))
                score += 1200;
            else if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                score += 700;
            else if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                score += 420;
            else
                score += 90;
        }

        if (entry.IsDir) score -= 25;
        score -= Math.Min(60, entry.Depth * 3);
        return score;
    }

    private static FileSearchItem Materialize(in Entry entry)
    {
        var name = entry.Path.Substring(entry.NameStart);
        string folder;
        string meta;
        string glyph;

        if (entry.IsDir)
        {
            folder = Path.GetDirectoryName(entry.Path) ?? entry.Path;
            meta = SafeDirTime(entry.Path);
            glyph = "\uE8B7";
        }
        else
        {
            folder = Path.GetDirectoryName(entry.Path) ?? "";
            meta = SafeFileMeta(entry.Path);
            glyph = "\uE8A5";
        }

        return new FileSearchItem
        {
            Path = entry.Path,
            Name = name,
            Folder = folder,
            Meta = meta,
            Glyph = glyph,
            IsDir = entry.IsDir
        };
    }

    private static string SafeDirTime(string path)
    {
        try { return Directory.GetLastWriteTime(path).ToString("yyyy-MM-dd"); }
        catch { return ""; }
    }

    private static string SafeFileMeta(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return FormatSize(info.Length) + " · " + info.LastWriteTime.ToString("yyyy-MM-dd");
        }
        catch { return ""; }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (v >= 1024 && unit < units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00") + " " + units[unit];
    }

    // ── 建索引 ──────────────────────────────────────────────────────────

    private void Build(bool useCache)
    {
        var roots = ResolveRoots();
        var signature = BuildSignature(roots);
        _scope = DescribeScope(roots);

        CancellationTokenSource cts;
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { }
            cts = new CancellationTokenSource();
            _cts = cts;
        }

        var token = cts.Token;

        if (useCache && TryLoadCache(signature, out var cached, out var cacheAge))
        {
            _entries = cached;
            _count = cached.Length;
            _fromCache = true;
            _phase = PhaseReady;
            _elapsedMs = 0;
            Notify(force: true);

            // 缓存够新就不重建：一次全盘遍历在教室机上代价不小，没必要每次开机都来一遍
            if (cacheAge < CacheMaxAge) return;
        }

        _phase = PhaseIndexing;
        _failure = "";
        _count = 0;
        _fromCache = false;
        Notify(force: true);

        var sw = Stopwatch.StartNew();
        var list = new List<Entry>(Math.Min(200_000, MaxEntries));

        try
        {
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                Walk(root, list, token);
                if (list.Count >= MaxEntries) break;
            }
        }
        catch (OperationCanceledException)
        {
            return;     // 被更新的一次重建取代了
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        sw.Stop();

        var entries = list.ToArray();
        _entries = entries;
        _count = entries.Length;
        _elapsedMs = sw.ElapsedMilliseconds;

        if (entries.Length == 0)
        {
            _phase = PhaseFailed;
            _failure = roots.Count == 0 ? "没有可索引的磁盘" : "没有读取到任何文件（权限不足或被安全软件拦截）";
            Notify(force: true);
            return;
        }

        _phase = PhaseReady;
        Notify(force: true);

        AppLog.Info("file-search", $"索引完成：{entries.Length} 项，用时 {_elapsedMs} ms，范围 {_scope}");

        try { SaveCache(signature, entries, token); }
        catch (Exception ex) { AppLog.Warning("file-search", "保存索引缓存失败：" + ex.Message); }
    }

    private void Fail(Exception ex)
    {
        _phase = PhaseFailed;
        _failure = ex.Message;
        Notify(force: true);
        AppLog.Exception("file-search", ex, "建立文件索引");
    }

    /// <summary>遍历一个根目录。用显式栈而不是递归：路径极深的目录树不会把线程栈打爆。</summary>
    private void Walk(string root, List<Entry> list, CancellationToken token)
    {
        var stack = new Stack<(string Dir, int Depth, bool RootChild)>();
        stack.Push((root, 0, true));

        while (stack.Count > 0 && list.Count < MaxEntries)
        {
            token.ThrowIfCancellationRequested();
            var (dir, depth, rootChild) = stack.Pop();

            IEnumerator<string> it;
            try { it = Directory.EnumerateFileSystemEntries(dir).GetEnumerator(); }
            catch { continue; }     // 无权限 / 目录刚被删

            using (it)
            {
                while (true)
                {
                    string path;
                    try
                    {
                        if (!it.MoveNext()) break;
                        path = it.Current;
                    }
                    catch { break; }

                    FileAttributes attrs;
                    try { attrs = File.GetAttributes(path); }
                    catch { continue; }

                    // 符号链接 / 目录联接一律不跟：跟进去会绕圈，也会把同一批文件收好几遍
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;

                    var isDir = (attrs & FileAttributes.Directory) != 0;
                    if (isDir && ShouldSkipDir(path, rootChild)) continue;

                    list.Add(new Entry(path, path.LastIndexOf('\\') + 1, isDir, depth));

                    if (isDir) stack.Push((path, depth + 1, false));

                    if ((list.Count & 0xFFF) == 0)
                    {
                        _count = list.Count;
                        Notify(force: false);
                    }

                    if (list.Count >= MaxEntries) break;
                }
            }
        }
    }

    /// <summary>
    /// 跳过的目录：Windows 目录、回收站、WindowsApps、包缓存、临时目录。
    /// 这些目录“搜到了也不会去点”，却往往占掉全盘的半壁江山。
    /// </summary>
    private static bool ShouldSkipDir(string path, bool rootChild)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0) return false;

        if (rootChild && name.Equals("Windows", StringComparison.OrdinalIgnoreCase)) return true;

        if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Equals("Temp", StringComparison.OrdinalIgnoreCase)
            && path.IndexOf(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (name.Equals("Package Cache", StringComparison.OrdinalIgnoreCase)
            && path.IndexOf(@"\ProgramData\", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (name.Equals("INetCache", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static List<string> ResolveRoots()
    {
        var roots = new List<string>();
        try
        {
            var wanted = App.Settings.Current.FileSearchDrives ?? Array.Empty<string>();

            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;

                    // ⛔ 结尾的反斜杠必须留着："C:" 是**盘符相对路径**（相对于该盘当前目录），
                    //    .NET 6 下枚举它得不到任何东西 —— 2026-10-30 实测索引 0 项、界面报
                    //    「没有读取到任何文件」，就是这里 TrimEnd('\\') 造成的。
                    var root = drive.RootDirectory.FullName;
                    if (root.Length < 3) continue;

                    // 设置里存的是不带反斜杠的 "C:"（FileSearchPage 勾选时 TrimEnd 过），比对时两边都归一
                    if (wanted.Length > 0
                        && !wanted.Any(w => w.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    roots.Add(root);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("file-search", "枚举磁盘失败：" + ex.Message);
        }

        return roots;
    }

    private static string BuildSignature(List<string> roots) =>
        "CSHIDX1|" + string.Join("|", roots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));

    private static string DescribeScope(List<string> roots) =>
        roots.Count == 0 ? "" : string.Join(" · ", roots.Select(r => r.TrimEnd('\\') + "\\"));

    /// <summary>可用的磁盘清单（页面上的勾选项用）。</summary>
    public static IReadOnlyList<string> AvailableDrives()
    {
        var list = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                    list.Add(drive.RootDirectory.FullName.TrimEnd('\\'));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    // ── 缓存 ────────────────────────────────────────────────────────────

    private static string CachePath => Path.Combine(SettingsStore.Dir, "file-index.cache");

    private static bool TryLoadCache(string signature, out Entry[] entries, out TimeSpan age)
    {
        entries = Array.Empty<Entry>();
        age = TimeSpan.MaxValue;

        try
        {
            if (!File.Exists(CachePath)) return false;
            age = DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath);
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;

            using var reader = new StreamReader(CachePath, System.Text.Encoding.UTF8);
            var header = reader.ReadLine();
            if (!string.Equals(header, signature, StringComparison.Ordinal)) return false;

            var list = new List<Entry>(100_000);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length < 3) continue;
                var isDir = line[0] == 'D';
                var path = line.Substring(2);
                if (path.Length == 0) continue;
                list.Add(new Entry(path, path.LastIndexOf('\\') + 1, isDir, 0));
                if (list.Count >= MaxEntries) break;
            }

            if (list.Count == 0) return false;
            entries = list.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("file-search", "读取索引缓存失败：" + ex.Message);
            return false;
        }
    }

    private static void SaveCache(string signature, Entry[] entries, CancellationToken token)
    {
        var temp = CachePath + ".tmp";
        Directory.CreateDirectory(SettingsStore.Dir);

        using (var writer = new StreamWriter(temp, false, new System.Text.UTF8Encoding(false), 1 << 16))
        {
            writer.WriteLine(signature);
            for (var i = 0; i < entries.Length; i++)
            {
                if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                var e = entries[i];
                writer.Write(e.IsDir ? 'D' : 'F');
                writer.Write('\t');
                writer.WriteLine(e.Path);
            }
        }

        File.Move(temp, CachePath, overwrite: true);
    }

    /// <summary>状态变了通知界面；进度打点限流，别让后台线程把 UI 线程刷满。</summary>
    private void Notify(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastNotifyTicks) < 400) return;
        Interlocked.Exchange(ref _lastNotifyTicks, now);

        try { Dispatcher.UIThread.Post(() => Changed?.Invoke()); }
        catch { Changed?.Invoke(); }
    }

    private readonly record struct Hit(int Index, int Score);

    /// <summary>内存里的一条：只存路径与文件名起点，大小/时间按需现取。</summary>
    private readonly struct Entry
    {
        public Entry(string path, int nameStart, bool isDir, int depth)
        {
            Path = path;
            NameStart = nameStart < 0 ? 0 : nameStart;
            IsDir = isDir;
            Depth = depth;
        }

        public readonly string Path;
        public readonly int NameStart;
        public readonly bool IsDir;
        public readonly int Depth;
    }
}
