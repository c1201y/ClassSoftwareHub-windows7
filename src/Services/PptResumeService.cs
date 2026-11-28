using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.SlideSync;
using ClassSoftwareHub.Desktop.SlideSync.Ui;

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

/// <summary>带具体日期与星期信息的课程，供本机 API 与 CI 联动使用。</summary>
public sealed class PptScheduledLesson
{
    public string Subject { get; init; } = "";
    public DateTime Date { get; init; }
    /// <summary>1 = 周一 … 7 = 周日。</summary>
    public int Weekday { get; init; }
    /// <summary>all | odd | even。</summary>
    public string Weeks { get; init; } = "all";
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public bool Ongoing { get; init; }

    public DateTime StartAt => Date + Start;
    public DateTime EndAt => Date + End;
    public string TimeText => Start.ToString(@"hh\:mm") + "–" + End.ToString(@"hh\:mm");
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
/// 课件续页后台服务。
///
/// <para>
/// 原来这里每 2 秒自己扫一次 PowerPoint / WPS 的 COM；现在换成课件续页插件的引擎
/// （<see cref="SessionEngine"/> + <see cref="Ledger"/> + <see cref="PptBridge"/> /
/// <see cref="SeewoBridge"/>）：250 ms 一次状态机、按课件记台账、WPS 死引用自动重连，
/// 并且支持希沃白板。CSH 只保留自己的课表导入、科目选择和浮窗界面。
/// </para>
///
/// <para>
/// 「科目 → 课件」的对照表仍然存在 ppt-resume.json：它负责老师手动指定的课件，
/// 以及引擎还没记过台账时的兜底进度；引擎的台账在 %LOCALAPPDATA%\ClassSoftwareHub\slidesync
/// 下，两份数据互不覆盖。
/// </para>
/// </summary>
public sealed class PptResumeService
{
    public static PptResumeService Current { get; } = new();

    private readonly object _gate = new();
    private PptResumeData _data = new();
    private CsesTimetable? _timetable;
    private PptLiveState _live = new();
    private string _lastMessage = "";
    private DateTime _messageAt;
    private DateTime _liveAt;
    private bool _loaded;

    private Ledger? _ledger;
    private SessionEngine? _engine;
    private SyncSettings _settings = new();
    private AppTheme? _theme;
    private CardAppearance? _appearance;
    private NativeOverlayCard? _card;
    private ControlPanel? _panel;
    private SlideSyncApi? _api;
    private volatile CardState? _lastState;
    private bool _armed;
    private string _armReason = "";
    private string _lastStatus = "";
    private DateTime _panelJumpAt;
    private string _lastOpenDeckPath = "";
    private DateTime _lastOpenDeckAt;

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub",
        "slidesync");

    /// <summary>
    /// 台账目录。在 ClassIsland 联动页打开「共用课件记录」后，直接用插件的配置目录，
    /// 两边的课件进度就是同一份；设置与外观仍留在 CSH 自己的目录里，互不覆盖。
    /// 目录在启动时定一次，运行中改开关要重启才生效。
    /// </summary>
    private static string LedgerDir => ClassIslandLink.SharedLedgerDir() ?? DataDir;

    private static string SettingsPath => Path.Combine(DataDir, "SyncSettings.json");

    /// <summary>状态 / 记录 / 课表有变化时触发（已切回 UI 线程）。</summary>
    public event Action? Changed;

    // ── 启动 ─────────────────────────────────────────────────────────────

    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;

            _data = PptResumeStore.Load();
            _timetable = null;
            if (!string.IsNullOrWhiteSpace(_data.TimetableJson))
                _timetable = CsesSchedule.Parse(_data.TimetableJson, out _);

            try
            {
                LoadSettingsLocked();

                var ledger = Ledger.Load(LedgerDir);
                _settings.DataFolder = ledger.Root;
                _settings.LogSink = m => { try { ledger.Log(m); } catch { } };

                var engine = new SessionEngine(ledger, _settings)
                {
                    LessonContext = ReadLessonTag,
                    Diag = _settings.DiagnosticLog,
                };
                engine.StateChanged += OnEngineState;

                _ledger = ledger;
                _engine = engine;
                engine.Start();

                _theme = AppTheme.Load(DataDir, m => { try { ledger.Log("theme: " + m); } catch { } });
                RecomputeAppearanceLocked();
                _appearance = CardAppearance.Resolve(_settings, _theme);
                BuildCardLocked();
                StartApiLocked();

                _settings.PropertyChanged += OnSettingsPropertyChanged;
                try
                {
                    if (Avalonia.Application.Current is not null)
                        Avalonia.Application.Current.ActualThemeVariantChanged += OnAppThemeVariantChanged;
                }
                catch { }

                _armed = true;
                _armReason = "随软件启动";
                AppLog.Info("ppt-resume", "续播引擎已启动｜台账目录=" + ledger.Root + "｜设置目录=" + DataDir);

                // 上次 CSH 退出后 ClassIsland 里可能还留着那块重复的「课件续页」面板 —— 启动顺手收掉。
                // 不阻塞启动：真正的枚举放到 UI 线程的下一次空闲里做。
                Dispatcher.UIThread.Post(() => { try { CiPanelCloser.CloseDuplicatePanels(); } catch { } });
            }
            catch (Exception ex)
            {
                _ledger = null;
                _engine = null;
                _loaded = false;
                AppLog.Exception("ppt-resume", ex, "启动课件续播引擎");
            }
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
                    FileName = record is { File.Length: > 0 } ? DisplayNameOf(record.File) : "",
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

    /// <summary>严格当前课：start ≤ now &lt; end，不带任何前后宽限。</summary>
    public PptScheduledLesson? CurrentOngoingLesson(DateTime? now = null)
    {
        EnsureStarted();
        lock (_gate)
        {
            return FindOngoingLessonLocked(now ?? DateTime.Now);
        }
    }

    /// <summary>下一节严格未来的课（start &gt; now），最多向后查两周以覆盖单双周。</summary>
    public PptScheduledLesson? NextLesson(DateTime? now = null)
    {
        EnsureStarted();
        lock (_gate)
        {
            return FindNextLessonLocked(now ?? DateTime.Now);
        }
    }

    private PptScheduledLesson? FindOngoingLessonLocked(DateTime now)
    {
        var table = _timetable;
        if (table is null) return null;

        var weekday = WeekdayOf(now);
        var parity = CsesSchedule.WeekParity(_data.SemesterStart, now);
        foreach (var schedule in table.Schedules)
        {
            if (schedule.EnableDay != weekday) continue;
            if (!ScheduleApplies(schedule, parity)) continue;

            foreach (var item in schedule.Classes)
            {
                var lesson = CreateScheduledLesson(schedule, item, now.Date, true);
                if (lesson is not null && now >= lesson.StartAt && now < lesson.EndAt)
                    return lesson;
            }
        }

        return null;
    }

    private PptScheduledLesson? FindNextLessonLocked(DateTime now)
    {
        var table = _timetable;
        if (table is null) return null;

        PptScheduledLesson? best = null;
        for (var offset = 0; offset < 14; offset++)
        {
            var date = now.Date.AddDays(offset);
            var weekday = WeekdayOf(date);
            var parity = CsesSchedule.WeekParity(_data.SemesterStart, date);

            foreach (var schedule in table.Schedules)
            {
                if (schedule.EnableDay != weekday) continue;
                if (!ScheduleApplies(schedule, parity)) continue;

                foreach (var item in schedule.Classes)
                {
                    var lesson = CreateScheduledLesson(schedule, item, date, false);
                    if (lesson is null || lesson.StartAt <= now) continue;
                    if (best is null || lesson.StartAt < best.StartAt) best = lesson;
                }
            }
        }

        return best;
    }

    private static PptScheduledLesson? CreateScheduledLesson(
        CsesDaySchedule schedule, CsesClass item, DateTime date, bool ongoing)
    {
        var subject = (item.Subject ?? "").Trim();
        if (subject.Length == 0) return null;
        if (!TryTime(item.StartTime, out var start) || !TryTime(item.EndTime, out var end)) return null;
        if (end <= start) return null;

        return new PptScheduledLesson
        {
            Subject = subject,
            Date = date,
            Weekday = schedule.EnableDay,
            Weeks = NormalizeWeeks(schedule.Weeks),
            Start = start,
            End = end,
            Ongoing = ongoing,
        };
    }

    private static bool ScheduleApplies(CsesDaySchedule schedule, string? parity)
    {
        var weeks = NormalizeWeeks(schedule.Weeks);
        return weeks == "all" || parity is null || weeks == parity;
    }

    private static string NormalizeWeeks(string? weeks)
    {
        var value = (weeks ?? "all").Trim().ToLowerInvariant();
        return value == "odd" || value == "even" ? value : "all";
    }

    private static int WeekdayOf(DateTime date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    private static bool TryTime(string? text, out TimeSpan time)
    {
        time = default;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return false;

        return TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out time)
               || TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out time)
               || TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time);
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
            return ImportCsesJson(json, path);
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppt-resume", ex, "导入 CSES 课表｜格式=CSES 课表文件");
            return (false, "读取课表失败：" + ex.Message);
        }
    }

    /// <summary>直接导入 CSES JSON / YAML 文本（本机 API 与 CI 联动用）。</summary>
    public (bool Ok, string Message) ImportCsesJson(string json, string source)
    {
        EnsureStarted();
        try
        {
            var table = CsesSchedule.Parse(json, out var error);
            if (table is null) return (false, error);

            lock (_gate)
            {
                _data.TimetableJson = json;
                _data.TimetableSource = source ?? "";
                _timetable = table;
                PptResumeStore.Save(_data);
            }

            var label = string.IsNullOrWhiteSpace(source)
                ? "本机接口"
                : Path.GetFileName(source);
            var message = "已导入课表：" + label + "（" + CsesSchedule.Describe(table) + "）";
            SetMessage(message);
            return (true, message);
        }
        catch (Exception ex)
        {
            AppLog.Exception("ppt-resume", ex, "导入 CSES 课表｜来源=JSON 文本");
            return (false, "导入课表失败：" + ex.Message);
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
        subject = (subject ?? "").Trim();

        PptRecord? record;
        lock (_gate)
        {
            _data.Subjects.TryGetValue(subject, out record);
        }

        if (record is null || record.File.Length == 0)
        {
            SetMessage("「" + subject + "」还没有课件记录：先在控制面板的「课件库」里选择这个科目的课件。");
            return;
        }

        bool seewo = SeewoBridge.IsSeewoKey(record.File);
        if (!seewo && !File.Exists(record.File))
        {
            SetMessage("课件文件找不到了：" + record.File + "（可能被移动或重命名）");
            return;
        }

        var (slide, note) = ResolveResumeSlide(record);
        var engine = _engine;
        if (engine is null)
        {
            SetMessage("续播引擎还没启动，请稍后再试。");
            return;
        }

        SetMessage("正在唤起演示程序，从第 " + slide + " 页继续…" + (note.Length > 0 ? " " + note : ""));
        engine.RequestJumpTo(record.File, slide);
    }

    public void ResumeCurrent()
    {
        EnsureStarted();
        var lesson = CurrentLesson();
        if (lesson is null)
        {
            SetMessage("现在没有找到对应课程：先导入 CSES 课表，或直接按科目续播。");
            return;
        }

        Resume(lesson.Subject);
    }

    /// <summary>
    /// 算出这次应该从第几页开始：优先用引擎台账里最近一次「讲完」的结果（能识别
    /// 课件被改过、以及上次已讲到最后一页），台账没有或比页面记录旧时退回页面记录。
    /// </summary>
    private (int Slide, string Note) ResolveResumeSlide(PptRecord record)
    {
        var slide = Math.Max(1, record.Slide);
        var note = "";
        var remapped = false;

        if (SeewoBridge.IsSeewoKey(record.File)) return (ClampToKnownTotal(slide, record), note);
        if (_ledger is null) return (ClampToKnownTotal(slide, record), note);

        try
        {
            var last = _ledger.Last(record.File);
            if (last is null) return (ClampToKnownTotal(slide, record), note);

            var lastAt = ParseStamp(last.EndedAt);
            var savedAt = ParseStamp(record.SavedAt);
            if (lastAt < savedAt) return (ClampToKnownTotal(slide, record), note);

            var end = last.EndSlide > 0 ? last.EndSlide : last.StartSlide;
            if (end <= 0) return (ClampToKnownTotal(slide, record), note);

            // 上次停在最后一页 = 已经讲完，下次从头开始（与插件行为一致）
            var finished = last.SlideCount > 0 && end >= last.SlideCount;
            slide = finished ? 1 : end;

            var fingerprint = Ledger.Fingerprint(record.File);
            if (!string.IsNullOrEmpty(last.Fingerprint)
                && !string.Equals(last.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                var remap = DeckRemap.RemapFor(_ledger, last, Ledger.Key(record.File), slide, _ledger.Root);
                if (remap is not null)
                {
                    if (remap.Usable && remap.NewSlide > 0)
                    {
                        slide = remap.NewSlide;
                        remapped = true;
                        if (!string.IsNullOrEmpty(remap.Note)) note = remap.Note;
                    }
                    else if (remap.Kind == RemapKind.Replaced)
                    {
                        slide = 1;
                        if (!string.IsNullOrEmpty(remap.Note)) note = remap.Note;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppt-resume", "计算续播页码", ex, LogLevel.Debug);
        }

        // 课件变短了但没能对齐页码时，页码可能超过现有页数 —— 旧版会先按总页数夹一下，
        // 这里用上次记下的总页数做同样的兜底（对齐成功的结果不夹，课件可能变长了）。
        slide = Math.Max(1, remapped ? slide : ClampToKnownTotal(slide, record));
        return (slide, note);
    }

    /// <summary>按页面记录里上次的总页数收一下页码；不知道总页数就原样返回。</summary>
    private static int ClampToKnownTotal(int slide, PptRecord record)
    {
        return record.Total > 0 ? Math.Min(slide, record.Total) : slide;
    }

    private static DateTime ParseStamp(string text)
    {
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : DateTime.MinValue;
    }

    // ── 插件界面（控制面板 / 浮层卡片）─────────────────────────────────────

    public SyncSettings Settings => _settings;
    public Ledger? Ledger => _ledger;
    public SessionEngine? Engine => _engine;
    public ICardSink? Card => _card;
    public CardAppearance? Appearance => _appearance;
    public AppTheme? Theme => _theme;
    public bool Armed => _armed;
    public string ArmReason => _armReason;

    /// <summary>课表里的科目名单（给控制面板每行的下拉用）。</summary>
    public IReadOnlyList<string> SubjectNames()
    {
        EnsureStarted();
        lock (_gate)
        {
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
            return names.ToList();
        }
    }

    /// <summary>打开课件续页：把主窗口叫出来，导航到内嵌面板那一页。</summary>
    public void OpenPanel()
    {
        EnsureStarted();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var main = App.MainWindow;
                if (main is null) return;

                // 2026-11-28（用户：俩课件续页重叠）：CSH 课件库要露面时，
                // 把 ClassIsland 里那个同款面板请它自己关掉 —— 屏幕上只留一套界面。
                try { CiPanelCloser.CloseDuplicatePanels(); } catch { }

                if (!main.IsVisible) main.ShowFromTray();
                main.Activate();
                main.Shell.NavigateToTool(typeof(Pages.Tools.PptResumePage));
            }
            catch (Exception ex)
            {
                AppLog.Catch("ppt-resume", "打开控制面板", ex, LogLevel.Debug);
            }
        });
    }

    /// <summary>由页面创建内嵌控件；服务保留引用，卡片和跳转状态才能刷进去。</summary>
    public ControlPanel? CreatePanel()
    {
        EnsureStarted();
        if (!Dispatcher.UIThread.CheckAccess()) return null;
        var panel = new ControlPanel(_ledger ?? new Ledger(), _settings,
            () => _lastState ?? new CardState(), OpenPanelFolder, this);
        panel.JumpRequested += OnPanelJumpRequested;
        _panel = panel;
        return panel;
    }

    /// <summary>页面离开时解绑，别让服务继续往已经卸载的控件上写字。</summary>
    public void ReleasePanel(ControlPanel panel)
    {
        if (panel is null) return;
        try { panel.JumpRequested -= OnPanelJumpRequested; } catch { }
        if (ReferenceEquals(_panel, panel)) _panel = null;
    }

    /// <summary>设置页「预览卡片」／卡片托盘入口。</summary>
    public void ShowCardNow()
    {
        EnsureStarted();
        try
        {
            var c = _card;
            if (c is not null) c.Expand();
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppt-resume", "预览卡片", ex, LogLevel.Debug);
        }
    }

    /// <summary>面板 / 卡片发起的跳页：统一走引擎的 RequestJumpTo。</summary>
    public void RequestJump(string filePath, int slide, bool fromPanel = false)
    {
        EnsureStarted();
        try
        {
            var engine = _engine;
            if (engine is null) return;

            if (!string.IsNullOrWhiteSpace(filePath) && slide > 0)
            {
                _panelJumpAt = DateTime.UtcNow;
                engine.RequestJumpTo(filePath, slide);
            }
            else
            {
                engine.RequestJump();
            }

            var c = _card;
            if (c is not null) c.ShowStatus("正在跳转…");
            if (fromPanel) _panel?.ShowStatus("正在跳转…");
        }
        catch (Exception ex)
        {
            var c = _card;
            if (c is not null) c.ShowStatus("跳转失败：" + ex.Message);
            if (fromPanel) _panel?.ShowStatus("跳转失败：" + ex.Message);
        }
    }

    /// <summary>退出软件时收掉卡片 / 引擎 / 面板，并落盘设置。</summary>
    public void Shutdown()
    {
        SyncSettings? settings;
        NativeOverlayCard? card;
        SessionEngine? engine;
        ControlPanel? panel;

        lock (_gate)
        {
            if (!_loaded) return;
            _loaded = false;
            _armed = false;

            settings = _settings;
            card = _card;
            _card = null;
            engine = _engine;
            _engine = null;
            panel = _panel;
            _panel = null;
        }

        try { settings.PropertyChanged -= OnSettingsPropertyChanged; } catch { }
        try
        {
            if (Avalonia.Application.Current is not null)
                Avalonia.Application.Current.ActualThemeVariantChanged -= OnAppThemeVariantChanged;
        }
        catch { }
        StopApiLocked();
        try { _saveTimer?.Dispose(); } catch { }
        _saveTimer = null;

        if (engine is not null)
        {
            try { engine.StateChanged -= OnEngineState; } catch { }
            try { engine.Dispose(); } catch { }
        }

        if (panel is not null)
        {
            try { panel.JumpRequested -= OnPanelJumpRequested; } catch { }
        }
        try { card?.Dispose(); } catch { }

        SaveSettings();
    }

    // ── 卡片接线 ─────────────────────────────────────────────────────────

    private void BuildCardLocked()
    {
        var c = new NativeOverlayCard();
        c.Configure(_appearance ?? new CardAppearance(),
            _settings.AutoShowOverlay, _settings.AutoHide, _settings.AutoHideSeconds,
            _settings.EnableControlPanel);
        c.JumpRequested += OnCardJumpRequested;
        c.HistoryJumpRequested += OnCardHistoryJumpRequested;
        c.PositionChanged += OnCardPositionChanged;
        c.PanelRequested += OpenPanel;
        c.OpenDeckRequested += OnCardOpenDeckRequested;
        c.SwitchToSuiteRequested += OnCardSwitchToSuiteRequested;
        c.Log = m => { try { _ledger?.Log("card: " + m); } catch { } };
        c.Start();
        _card = c;
    }

    private void OnCardJumpRequested()
    {
        var st = _lastState;
        var page = st?.Previous ?? 0;
        RequestJump("", page > 0 ? page : 0);
    }

    private void OnCardHistoryJumpRequested(string filePath, int slide) => RequestJump(filePath, slide);

    private void OnCardPositionChanged(bool onRight, int verticalPercent)
    {
        _settings.CardOnLeft = !onRight;
        _settings.CardVerticalPercent = verticalPercent;
    }

    private void OnCardOpenDeckRequested(string filePath, int slide)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            _card?.ShowStatus("课件不在了");
            return;
        }

        var now = DateTime.UtcNow;
        if (string.Equals(filePath, _lastOpenDeckPath, StringComparison.OrdinalIgnoreCase)
            && (now - _lastOpenDeckAt).TotalSeconds < 8) return;
        _lastOpenDeckPath = filePath;
        _lastOpenDeckAt = now;

        try
        {
            DeckLauncher.Open(filePath, m => { try { _ledger?.Log(m); } catch { } });
            _engine?.RequestJumpOnOpen(filePath, slide);
            _card?.ShowStatus("正在打开课件…");
        }
        catch (Exception ex)
        {
            _ledger?.Log("open deck failed: " + ex.Message);
            _card?.ShowStatus("打不开课件");
        }
    }

    private void OnCardSwitchToSuiteRequested()
    {
        try { PptBridge.ForegroundSuiteWindow(); } catch { }
    }

    private void OnPanelJumpRequested(string filePath, int slide) => RequestJump(filePath, slide, true);

    private void OpenPanelFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            _panel?.ShowStatus("没有可打开的文件夹");
            return;
        }
        if (!Directory.Exists(folder))
        {
            _panel?.ShowStatus("找不到这个文件夹，可能已被移动：" + folder);
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _panel?.ShowStatus("打不开文件夹：" + ex.Message);
        }
    }

    // ── 设置 / 外观 ──────────────────────────────────────────────────────

    private void LoadSettingsLocked()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<SyncSettings>(json);
                if (loaded is not null) _settings = loaded;
            }
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppt-resume", "读取课件续页设置", ex, LogLevel.Debug);
        }

        _settings.DataFolder = LedgerDir;
        DeckLauncher.Preferred = _settings.DeckOpenApp;
    }

    /// <summary>启动本机 HTTP 接口；令牌首次使用时自动生成并落盘。</summary>
    private void StartApiLocked()
    {
        if (!_settings.ApiEnabled || _api is not null) return;
        if (string.IsNullOrWhiteSpace(_settings.ApiToken))
        {
            _settings.ApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            SaveSettings();
        }

        var api = new SlideSyncApi(this, _settings.ApiPort, _settings.ApiToken);
        if (api.Start()) _api = api;
        else api.Dispose();
    }

    private void StopApiLocked()
    {
        var api = _api;
        _api = null;
        try { api?.Dispose(); } catch { }
    }

    private void OnAppThemeVariantChanged(object? sender, EventArgs e)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    lock (_gate)
                    {
                        if (!_loaded) return;
                        RecomputeAppearanceLocked();
                    }
                    RaiseChanged();
                }
                catch (Exception ex)
                {
                    AppLog.Catch("ppt-resume", "跟随软件主题刷新卡片颜色", ex, LogLevel.Debug);
                }
            });
        }
        catch { }
    }

    private System.Threading.Timer? _saveTimer;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 滑块一拖就是几十次变更，攒 600 ms 写一次盘。
        _saveTimer ??= new System.Threading.Timer(_ => SaveSettings(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        try { _saveTimer.Change(600, System.Threading.Timeout.Infinite); } catch { }

        if (e.PropertyName == nameof(SyncSettings.ApiEnabled)
            || e.PropertyName == nameof(SyncSettings.ApiPort)
            || e.PropertyName == nameof(SyncSettings.ApiToken))
        {
            lock (_gate)
            {
                if (_loaded)
                {
                    StopApiLocked();
                    StartApiLocked();
                }
            }
        }

        Dispatcher.UIThread.Post(ApplySettingsToCard);
    }

    private void ApplySettingsToCard()
    {
        try
        {
            DeckLauncher.Preferred = _settings.DeckOpenApp;
            RecomputeAppearanceLocked();
            _card?.Configure(_appearance ?? new CardAppearance(),
                _settings.AutoShowOverlay, _settings.AutoHide, _settings.AutoHideSeconds,
                _settings.EnableControlPanel);
            if (_engine is not null) _engine.Diag = _settings.DiagnosticLog;
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppt-resume", "应用课件续页设置", ex, LogLevel.Debug);
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppt-resume", "保存课件续页设置", ex, LogLevel.Debug);
        }
    }

    /// <summary>重新算一遍「软件主题 ⊕ 用户自定义」。</summary>
    public CardAppearance RecomputeAppearance()
    {
        EnsureStarted();
        RecomputeAppearanceLocked();
        return _appearance ?? new CardAppearance();
    }

    private void RecomputeAppearanceLocked()
    {
        _theme ??= AppTheme.Load(DataDir, null);
        ProbeUiThemeLocked();
        _appearance = CardAppearance.Resolve(_settings, _theme);
        _card?.SetAppearance(_appearance);
    }

    /// <summary>强调色优先取 Avalonia 运行时的系统强调色（软件主题色）。</summary>
    private void ProbeUiThemeLocked()
    {
        if (_theme is null) return;
        try
        {
            if (TryBrush("AccentFillColorDefaultBrush", out var c)
                || TryBrush("SystemAccentColor", out c)
                || TryBrush("SystemAccentColorDark1", out c))
            {
                _theme.AccentR = c.R;
                _theme.AccentG = c.G;
                _theme.AccentB = c.B;
                _theme.AccentFromUi = true;
                _theme.AccentNote = "avalonia:accent";
            }
            else
            {
                _theme.AccentFromUi = false;
                _theme.AccentNote = "default";
            }

            // 工具浮窗 / 文件搜索窗用的就是这把画刷；卡片底色跟着它，
            // 换主题时 ActualThemeVariantChanged 会再探一次。
            if (TryBrush("ApplicationPageBackgroundThemeBrush", out var bg))
            {
                _theme.BgR = bg.R;
                _theme.BgG = bg.G;
                _theme.BgB = bg.B;
                _theme.BackgroundHex = string.Format("#{0:X2}{1:X2}{2:X2}", bg.R, bg.G, bg.B);
            }
        }
        catch
        {
            _theme.AccentFromUi = false;
        }
    }

    private static bool TryBrush(string key, out Avalonia.Media.Color color)
    {
        color = default;
        try
        {
            var app = Avalonia.Application.Current;
            if (app is null) return false;
            if (!app.TryFindResource(key, out var value) || value is null) return false;
            if (value is Avalonia.Media.SolidColorBrush brush)
            {
                color = brush.Color;
                return true;
            }
            if (value is Avalonia.Media.Color direct)
            {
                color = direct;
                return true;
            }
        }
        catch { }
        return false;
    }

    // ── 引擎事件 ─────────────────────────────────────────────────────────

    private void OnEngineState(CardState state)
    {
        try
        {
            var now = DateTime.Now;
            var playing = state.Showing;
            var file = "";
            var slide = state.Current;
            var total = state.SlideCount;

            if (playing)
            {
                file = _engine?.LastFilePath ?? "";
                if (file.Length == 0) file = _live.File;
            }

            var subject = "";
            if (file.Length > 0) subject = ResolveSubject(file, now);

            var status = DescribeState(state, playing);
            if (state.BlockedNotice.Length > 0) status = state.BlockedNotice;

            lock (_gate)
            {
                _live = new PptLiveState
                {
                    Playing = playing,
                    File = playing ? file : "",
                    Slide = playing ? slide : 0,
                    Total = playing ? total : 0,
                    Subject = playing ? subject : "",
                    Status = status,
                };
                _liveAt = now;

                if (state.Status.Length > 0 && state.Status != _lastMessage)
                {
                    _lastMessage = state.Status;
                    _messageAt = now;
                }

                if (playing && file.Length > 0 && slide > 0)
                    SaveProgressLocked(file, subject, slide, total);
            }

            _lastState = state;

            if (_panelJumpAt != DateTime.MinValue)
            {
                if ((DateTime.UtcNow - _panelJumpAt).TotalSeconds > 20)
                {
                    _panelJumpAt = DateTime.MinValue;
                }
                else if (!string.IsNullOrEmpty(state.Status) && state.Status != "正在跳转…")
                {
                    _panelJumpAt = DateTime.MinValue;
                    _panel?.ShowStatus(state.Status);
                }
            }

            if (state.Status != _lastStatus)
            {
                _lastStatus = state.Status;
                _card?.ShowStatus(state.Status);
            }
            _card?.Apply(state);

            RaiseChanged();
        }
        catch (Exception ex)
        {
            AppLog.Catch("ppt-resume", "处理续播引擎状态", ex, LogLevel.Debug);
        }
    }

    private string DescribeState(CardState state, bool playing)
    {
        if (playing) return "正在放映。";
        if (state.DeckOpen) return "课件已打开，当前没有在放映。";

        try
        {
            if (PptBridge.SuiteIsRunning())
            {
                var name = PptBridge.SuiteName();
                return (name.Length > 0 ? name : "演示软件") + "已打开，当前没有在放映。";
            }
        }
        catch { }

        return "尚未检测到正在放映的课件。";
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

        var name = Path.GetFileNameWithoutExtension(SeewoBridge.DisplayName(file));
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

    private void SaveProgressLocked(string file, string subject, int slide, int total)
    {
        if (subject.Length == 0 || file.Length == 0) return;

        if (!_data.Subjects.TryGetValue(subject, out var record))
        {
            record = new PptRecord();
            _data.Subjects[subject] = record;
        }

        if (string.Equals(record.File, file, StringComparison.OrdinalIgnoreCase)
            && record.Slide == slide && record.Total == total)
            return;

        record.File = file;
        record.Slide = slide;
        record.Total = total;
        record.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _data.FileSubjects[file] = subject;
        PptResumeStore.Save(_data);
    }

    // ── 状态 / 通知 ──────────────────────────────────────────────────────

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

    private static string DisplayNameOf(string file)
    {
        return SeewoBridge.IsSeewoKey(file)
            ? SeewoBridge.DisplayName(file)
            : Path.GetFileName(file);
    }

    private LessonTag ReadLessonTag()
    {
        var tag = new LessonTag();
        var now = DateTime.Now;
        tag.Weekday = ((int)now.DayOfWeek + 6) % 7 + 1;

        lock (_gate)
        {
            var lesson = CsesSchedule.FindLesson(_timetable, _data.SemesterStart, now);
            if (lesson is not null) tag.Subject = lesson.Subject;
        }

        return tag;
    }
}