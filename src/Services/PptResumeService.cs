using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>正在放映的现场状态（给浮窗/工具页显示）。</summary>
public sealed class PptLiveState
{
    public bool Playing { get; init; }
    public string File { get; init; } = "";
    public int Slide { get; init; }
    public int Total { get; init; }
    public string Subject { get; init; } = "";
    public string Status { get; init; } = "尚未检测到正在放映的课件";
}

/// <summary>一个科目在界面上的进度行。</summary>
public sealed class PptSubjectView
{
    public string Name { get; init; } = "";
    public bool InTimetable { get; init; }
    public string File { get; init; } = "";
    public string FileName { get; init; } = "";
    public int Slide { get; init; }
    public int Total { get; init; }
    public string SavedAt { get; init; } = "";
    public bool HasRecord => File.Length > 0;
}

/// <summary>界面一次刷新用的快照。</summary>
public sealed class PptResumeSnapshot
{
    public CsesTimetable? Timetable { get; init; }
    public string TimetableSource { get; init; } = "";
    public string SemesterStart { get; init; } = "";
    public List<PptSubjectView> Subjects { get; init; } = new();
    public PptLesson? Lesson { get; init; }
    public PptLiveState Live { get; init; } = new();
    public string LastMessage { get; init; } = "";
    /// <summary>最后一条操作提示的时间；比 <see cref="LiveAt"/> 新时才显示，避免旧提示盖住实时状态。</summary>
    public DateTime MessageAt { get; init; }
    /// <summary>放映状态最后一次变化的时间。</summary>
    public DateTime LiveAt { get; init; }
}

/// <summary>
/// PPT 续播后台服务。
///
/// <para>
/// ── 干什么 ─────────────────────────────────────────────────────────
/// 每 2 秒问一次「PowerPoint / WPS 演示现在放映到第几页」，按当前课表把进度记到
/// 对应科目上；用户点「续播」时，打开该科目上次的课件并从记录的那一页继续放映。
/// 进度按「每换一页写一次」落盘，所以直接关掉 PPT / 关机也不会丢。
/// </para>
///
/// <para>
/// ── 为什么单开一条 STA 线程 ────────────────────────────────────────
/// Office 的 COM 自动化要求 STA；而且 PowerPoint 卡住（弹模态框、文件很大）时，
/// 调用会阻塞好几秒 —— 放在 UI 线程上的定时器里会直接把界面卡死。
/// 这里用一条后台 STA 线程同时处理「轮询」和「续播命令」，UI 只收状态通知。
/// </para>
///
/// <para>
/// ⚠️ 轮询只在机器上有演示进程时才会去扫 ROT；没开 PPT 时每 2 秒只做一次
///    轻量的进程名检查，不碰 COM。
/// </para>
/// </summary>
public sealed class PptResumeService
{
    public static PptResumeService Current { get; } = new();

    private readonly object _gate = new();
    private readonly BlockingCollection<Action> _commands = new();
    private PptResumeData _data = new();
    private CsesTimetable? _timetable;
    private PptLiveState _live = new();
    private string _lastMessage = "";
    private DateTime _messageAt;
    private DateTime _liveAt;
    private object? _ppt;
    private Thread? _worker;
    private bool _loaded;

    /// <summary>状态 / 记录 / 课表有变化时触发（已切回 UI 线程）。</summary>
    public event Action? Changed;

    // ── 启动 ─────────────────────────────────────────────────────────────

    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_worker is not null) return;

            LoadLocked();

            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "PptResume",
            };
            try { thread.SetApartmentState(ApartmentState.STA); } catch { }

            _worker = thread;
            thread.Start();
        }
    }

    private void LoadLocked()
    {
        if (_loaded) return;
        _loaded = true;

        _data = PptResumeStore.Load();
        _timetable = null;
        if (!string.IsNullOrWhiteSpace(_data.TimetableJson))
        {
            _timetable = CsesSchedule.Parse(_data.TimetableJson, out _);
        }
    }

    // ── 查询 ─────────────────────────────────────────────────────────────

    public PptResumeSnapshot Snapshot()
    {
        EnsureStarted();

        lock (_gate)
        {
            var now = DateTime.Now;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_timetable is not null)
            {
                foreach (var subject in _timetable.Subjects)
                {
                    var name = (subject.Name ?? "").Trim();
                    if (name.Length > 0) names.Add(name);
                }
            }
            foreach (var name in _data.Subjects.Keys) names.Add(name);

            var rows = new List<PptSubjectView>(names.Count);
            foreach (var name in names)
            {
                _data.Subjects.TryGetValue(name, out var record);
                rows.Add(new PptSubjectView
                {
                    Name = name,
                    InTimetable = IsInTimetableLocked(name),
                    File = record?.File ?? "",
                    FileName = record is { File.Length: > 0 } ? Path.GetFileName(record.File) : "",
                    Slide = record?.Slide ?? 0,
                    Total = record?.Total ?? 0,
                    SavedAt = record?.SavedAt ?? "",
                });
            }

            return new PptResumeSnapshot
            {
                Timetable = _timetable,
                TimetableSource = _data.TimetableSource,
                SemesterStart = _data.SemesterStart,
                Subjects = rows,
                Lesson = CsesSchedule.FindLesson(_timetable, _data.SemesterStart, now),
                Live = new PptLiveState
                {
                    Playing = _live.Playing,
                    File = _live.File,
                    Slide = _live.Slide,
                    Total = _live.Total,
                    Subject = _live.Subject,
                    Status = _live.Status,
                },
                LastMessage = _lastMessage,
                MessageAt = _messageAt,
                LiveAt = _liveAt,
            };
        }
    }

    public PptLesson? CurrentLesson(DateTime? now = null)
    {
        EnsureStarted();
        lock (_gate)
        {
            return CsesSchedule.FindLesson(_timetable, _data.SemesterStart, now ?? DateTime.Now);
        }
    }

    public string? RecordFile(string subject)
    {
        lock (_gate)
        {
            return _data.Subjects.TryGetValue(subject ?? "", out var record) && record.File.Length > 0
                ? record.File
                : null;
        }
    }

    // ── 课表 ─────────────────────────────────────────────────────────────

    public (bool Ok, string Message) ImportCses(string path)
    {
        EnsureStarted();
        try
        {
            var json = File.ReadAllText(path);
            var table = CsesSchedule.Parse(json, out var error);
            if (table is null) return (false, error);

            lock (_gate)
            {
                _data.TimetableJson = json;
                _data.TimetableSource = path;
                _timetable = table;
                PptResumeStore.Save(_data);
            }

            var message = "已导入课表：" + Path.GetFileName(path) + "（" + CsesSchedule.Describe(table) + "）";
            SetMessage(message);
            return (true, message);
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppt-resume", ex, "导入 CSES 课表｜格式=CSES 课表文件");
            return (false, "读取课表失败：" + ex.Message);
        }
    }

    public void ClearTimetable()
    {
        lock (_gate)
        {
            _data.TimetableJson = "";
            _data.TimetableSource = "";
            _timetable = null;
            PptResumeStore.Save(_data);
            SetMessageLocked("课表已清除（各科课件记录保留）。");
        }
        RaiseChanged();
    }

    public (bool Ok, string Message) ApplySemesterStart(string text)
    {
        EnsureStarted();
        var value = (text ?? "").Trim();

        if (value.Length > 0)
        {
            if (!CsesSchedule.TryDate(value, out var date))
                return (false, "日期格式不对，请填“2026-09-01”这样的形式。");
            value = date.ToString("yyyy-MM-dd");
        }

        lock (_gate)
        {
            _data.SemesterStart = value;
            PptResumeStore.Save(_data);
        }

        var message = value.Length == 0
            ? "已清除学期开始日期；单双周课表会都视为可用。"
            : "本学期第一周周一：" + value + "（用于推算单双周）。";
        SetMessage(message);
        return (true, message);
    }

    // ── 课件记录 ─────────────────────────────────────────────────────────

    public (bool Ok, string Message) AssignFile(string subject, string file)
    {
        EnsureStarted();
        subject = (subject ?? "").Trim();
        if (subject.Length == 0) return (false, "先确定科目：导入课表或等程序从放映的 PPT 里识别。");
        if (!File.Exists(file)) return (false, "课件文件不存在：" + file);

        lock (_gate)
        {
            if (!_data.Subjects.TryGetValue(subject, out var record))
            {
                record = new PptRecord { Slide = 1 };
                _data.Subjects[subject] = record;
            }

            // 换了一份课件就从头记：沿用旧页码会续播到新课件的错误位置
            if (!string.Equals(record.File, file, StringComparison.OrdinalIgnoreCase))
            {
                record.Slide = 1;
                record.Total = 0;
            }

            record.File = file;
            record.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _data.FileSubjects[file] = subject;     // 下次打开同一份课件直接归到这个科目
            PptResumeStore.Save(_data);
        }

        var message = "「" + subject + "」的课件已设为：" + Path.GetFileName(file);
        SetMessage(message);
        return (true, message);
    }

    public void RemoveRecord(string subject)
    {
        lock (_gate)
        {
            if (_data.Subjects.Remove(subject))
            {
                foreach (var pair in _data.FileSubjects
                             .Where(p => string.Equals(p.Value, subject, StringComparison.OrdinalIgnoreCase))
                             .Select(p => p.Key).ToList())
                {
                    _data.FileSubjects.Remove(pair);
                }

                PptResumeStore.Save(_data);
            }

            SetMessageLocked("已清除「" + subject + "」的课件记录。");
        }
        RaiseChanged();
    }

    // ── 续播 ─────────────────────────────────────────────────────────────

    public void Resume(string subject)
    {
        EnsureStarted();
        _commands.Add(() => DoResume(subject));
    }

    public void ResumeCurrent()
    {
        EnsureStarted();
        _commands.Add(() =>
        {
            PptLesson? lesson;
            lock (_gate)
            {
                lesson = CsesSchedule.FindLesson(_timetable, _data.SemesterStart, DateTime.Now);
            }

            if (lesson is null)
            {
                SetMessage("现在没有找到对应课程：先导入 CSES 课表，或直接按科目续播。");
                return;
            }

            DoResume(lesson.Subject);
        });
    }

    // ── 后台线程 ─────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        var lastPoll = Environment.TickCount - 2200;

        while (true)
        {
            try
            {
                while (_commands.TryTake(out var command))
                {
                    try { command(); }
                    catch (Exception ex) { AppLog.Catch("ppt-resume", "执行放映线程排队的操作", ex, LogLevel.Debug); }
                }

                var now = Environment.TickCount;
                if (unchecked(now - lastPoll) >= 2000)
                {
                    lastPoll = now;
                    Poll();
                }
            }
            catch (Exception ex)
            {
                AppLog.Catch("ppt-resume", "轮询放映状态", ex, LogLevel.Debug);
            }

            Thread.Sleep(200);
        }
    }

    private void Poll()
    {
        if (!OfficeCom.AnyRunning())
        {
            ReleaseApp();
            SetLive(false, "尚未检测到正在放映的课件。", "", 0, 0, "");
            return;
        }

        var app = _ppt ??= OfficeCom.TryGetRunning();
        if (app is null)
        {
            SetLive(false, "检测到演示软件进程，但暂时连不上（可能还在启动）。", "", 0, 0, "");
            return;
        }

        try
        {
            dynamic office = app;
            var windows = 0;
            try { windows = Convert.ToInt32(office.SlideShowWindows.Count); } catch { }

            if (windows <= 0)
            {
                SetLive(false, "演示软件已打开，当前没有在放映。", "", 0, 0, "");
                return;
            }

            dynamic show = office.SlideShowWindows[1];
            var slide = Convert.ToInt32(show.View.CurrentShowPosition);

            dynamic presentation = show.Presentation;
            var file = Convert.ToString(presentation.FullName) ?? "";
            var total = Convert.ToInt32(presentation.Slides.Count);

            var subject = ResolveSubject(file, DateTime.Now);
            SetLive(true, "正在放映。", file, slide, total, subject);
            SaveProgress(file, subject, slide, total);
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppt-resume", "读取放映状态", ex, LogLevel.Debug);
            ReleaseApp();
            SetLive(false, "读取放映状态失败（演示软件可能正忙或被弹窗挡住）。", "", 0, 0, "");
        }
    }

    /// <summary>
    /// 这份课件属于哪个科目：① 之前记过就沿用；② 课表里正在上（或前后 45 分钟）的课；
    /// ③ 文件名里带了课表里的科目名（“第三单元_语文.pptx”也能认出来）。
    /// </summary>
    private string ResolveSubject(string file, DateTime now)
    {
        lock (_gate)
        {
            if (file.Length > 0 && _data.FileSubjects.TryGetValue(file, out var known) && known.Length > 0)
                return known;

            var lesson = CsesSchedule.FindLesson(_timetable, _data.SemesterStart, now);
            if (lesson is not null)
            {
                RememberFileSubjectLocked(file, lesson.Subject);
                return lesson.Subject;
            }

            var byName = MatchSubjectByNameLocked(file);
            if (byName.Length > 0)
            {
                RememberFileSubjectLocked(file, byName);
                return byName;
            }

            return "";
        }
    }

    private string MatchSubjectByNameLocked(string file)
    {
        if (_timetable is null || file.Length == 0) return "";

        var name = Path.GetFileNameWithoutExtension(file);
        if (name.Length == 0) return "";

        var best = "";
        foreach (var subject in _timetable.Subjects)
        {
            var candidate = (subject.Name ?? "").Trim();
            if (candidate.Length < 2) continue;      // 单字科目名太容易误命中
            if (candidate.Length <= best.Length) continue;
            if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) best = candidate;
        }
        return best;
    }

    private void RememberFileSubjectLocked(string file, string subject)
    {
        if (file.Length == 0 || subject.Length == 0) return;
        if (_data.FileSubjects.TryGetValue(file, out var existing)
            && string.Equals(existing, subject, StringComparison.OrdinalIgnoreCase))
            return;

        _data.FileSubjects[file] = subject;
        PptResumeStore.Save(_data);
    }

    private void SaveProgress(string file, string subject, int slide, int total)
    {
        if (subject.Length == 0 || file.Length == 0) return;

        var changed = false;
        lock (_gate)
        {
            if (!_data.Subjects.TryGetValue(subject, out var record))
            {
                record = new PptRecord();
                _data.Subjects[subject] = record;
            }

            if (record.File == file && record.Slide == slide && record.Total == total)
                return;

            record.File = file;
            record.Slide = slide;
            record.Total = total;
            record.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _data.FileSubjects[file] = subject;
            PptResumeStore.Save(_data);
            changed = true;
        }

        if (changed) RaiseChanged();
    }

    private void DoResume(string subject)
    {
        subject = (subject ?? "").Trim();

        PptRecord? record;
        lock (_gate)
        {
            _data.Subjects.TryGetValue(subject, out record);
        }

        if (record is null || record.File.Length == 0)
        {
            SetMessage("「" + subject + "」还没有课件记录：先在「PPT 续播」页里选择这个科目的课件。");
            return;
        }

        if (!File.Exists(record.File))
        {
            SetMessage("课件文件找不到了：" + record.File + "（可能被移动或重命名）");
            return;
        }

        var app = _ppt ??= OfficeCom.TryGetRunning() ?? OfficeCom.Start();
        if (app is null)
        {
            SetMessage("没有找到 PowerPoint 或 WPS 演示，无法续播。请先安装其中之一。");
            return;
        }

        try
        {
            dynamic office = app;
            try { office.Visible = -1; } catch { }   // msoTrue；WPS 也认

            dynamic? presentation = FindPresentation(office, record.File);
            if (presentation is null)
            {
                try
                {
                    presentation = office.Presentations.Open(record.File, -1, 0, -1);
                }
                catch
                {
                    presentation = office.Presentations.Open(record.File);
                }
            }

            var total = 0;
            try { total = Convert.ToInt32(presentation.Slides.Count); } catch { }

            var slide = Math.Max(1, record.Slide);
            if (total > 0) slide = Math.Min(slide, total);

            // 已经在放映同一份课件：直接跳回那一页，不再 Run 一次
            dynamic? showing = null;
            try
            {
                if (Convert.ToInt32(office.SlideShowWindows.Count) > 0)
                    showing = office.SlideShowWindows[1];
            }
            catch { }

            if (showing is not null)
            {
                var showingFile = "";
                try { showingFile = Convert.ToString(showing.Presentation.FullName) ?? ""; } catch { }

                if (string.Equals(showingFile, record.File, StringComparison.OrdinalIgnoreCase))
                {
                    showing.View.GotoSlide(slide);
                    try { showing.Activate(); } catch { }
                    SetMessage("「" + subject + "」已跳回第 " + slide + " 页。");
                    Poll();
                    return;
                }
            }

            dynamic settings = presentation.SlideShowSettings;
            settings.StartingSlide = slide;
            settings.Run();

            SetMessage("「" + subject + "」从第 " + slide + " 页开始放映。");
            _commands.Add(() => { Thread.Sleep(600); Poll(); });
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppt-resume", ex, $"续播｜科目={subject}");
            SetMessage("续播失败：" + ex.Message);
            ReleaseApp();
        }
    }

    private static dynamic? FindPresentation(dynamic office, string file)
    {
        dynamic presentations = office.Presentations;
        var count = Convert.ToInt32(presentations.Count);

        for (var i = 1; i <= count; i++)
        {
            dynamic presentation = presentations[i];
            var fullName = "";
            try { fullName = Convert.ToString(presentation.FullName) ?? ""; } catch { }

            if (string.Equals(fullName, file, StringComparison.OrdinalIgnoreCase))
                return presentation;
        }

        return null;
    }

    // ── 状态 / 通知 ──────────────────────────────────────────────────────

    private void SetLive(bool playing, string status, string file, int slide, int total, string subject)
    {
        lock (_gate)
        {
            if (_live.Playing == playing && _live.Status == status && _live.File == file
                && _live.Slide == slide && _live.Total == total && _live.Subject == subject)
                return;

            _live = new PptLiveState
            {
                Playing = playing,
                Status = status,
                File = file,
                Slide = slide,
                Total = total,
                Subject = subject,
            };
            _liveAt = DateTime.Now;
        }

        RaiseChanged();
    }

    private void SetMessageLocked(string message)
    {
        _lastMessage = message ?? "";
        _messageAt = DateTime.Now;
    }

    private void SetMessage(string message)
    {
        lock (_gate) SetMessageLocked(message);
        RaiseChanged();
    }

    private void ReleaseApp()
    {
        var app = _ppt;
        _ppt = null;
        OfficeCom.Release(app);
    }

    private void RaiseChanged()
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { Changed?.Invoke(); } catch { }
            });
        }
        catch { }
    }

    private bool IsInTimetableLocked(string subject)
    {
        if (_timetable is null) return false;
        return _timetable.Subjects.Any(s =>
            string.Equals((s.Name ?? "").Trim(), subject, StringComparison.OrdinalIgnoreCase));
    }
}
