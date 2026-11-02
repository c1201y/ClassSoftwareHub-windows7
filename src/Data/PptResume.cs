using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassSoftwareHub.Desktop.Data;

// ═══════════════════════════════════════════════════════════════════════
//  PPT 续播的数据层
//
//  三件事：
//    ① 解析 CSES 课表（Course Schedule Exchange Schema v1，JSON）；
//    ② 按「今天 / 单双周 / 当前时间」找出正在（或临近）上的课；
//    ③ 把每个科目的课件路径与看到第几页落到本机文件。
//
//  CSES 是课表**交换**格式，不是 ClassIsland 的完整档案：只有 subjects +
//     schedules（enable_day 1-7 / weeks all|odd|even / classes[start,end]），
//     没有学期开始日期 —— 单双周靠用户填的「第一周周一」推算，没填时
//     odd/even 的课表都当作可用（宁可多显示，也不要漏掉一节课）。
// ═══════════════════════════════════════════════════════════════════════

/// <summary>CSES 的科目对象。</summary>
public sealed class CsesSubject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("simplified_name")] public string? SimplifiedName { get; set; }
    [JsonPropertyName("teacher")] public string? Teacher { get; set; }
    [JsonPropertyName("room")] public string? Room { get; set; }
}

/// <summary>CSES 的一节课（科目 + 起止时间）。</summary>
public sealed class CsesClass
{
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("start_time")] public string StartTime { get; set; } = "";
    [JsonPropertyName("end_time")] public string EndTime { get; set; } = "";
}

/// <summary>CSES 的一天课表。</summary>
public sealed class CsesDaySchedule
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>1 = 周一 … 7 = 周日。</summary>
    [JsonPropertyName("enable_day")] public int EnableDay { get; set; }
    /// <summary>all | odd | even。</summary>
    [JsonPropertyName("weeks")] public string Weeks { get; set; } = "all";
    [JsonPropertyName("classes")] public List<CsesClass> Classes { get; set; } = new();
}

/// <summary>CSES 课表（v1）。</summary>
public sealed class CsesTimetable
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("subjects")] public List<CsesSubject> Subjects { get; set; } = new();
    [JsonPropertyName("schedules")] public List<CsesDaySchedule> Schedules { get; set; } = new();
}

/// <summary>一条「现在/邻近」的课。</summary>
public sealed class PptLesson
{
    public string Subject { get; init; } = "";
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    /// <summary>true = 正在上；false = 前后 45 分钟内的相邻一节。</summary>
    public bool Ongoing { get; init; }

    public string TimeText => Start.ToString(@"hh\:mm") + "–" + End.ToString(@"hh\:mm");
}

/// <summary>CSES 解析 + 当前课程判断。</summary>
public static class CsesSchedule
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析 CSES JSON；失败时返回 null，<paramref name="error"/> 给出面向用户的说明。</summary>
    public static CsesTimetable? Parse(string json, out string error)
    {
        error = "";
        try
        {
            var table = JsonSerializer.Deserialize<CsesTimetable>(json, Options);
            if (table is null)
            {
                error = "课表文件是空的，或不是有效的 CSES JSON。";
                return null;
            }

            table.Subjects ??= new List<CsesSubject>();
            table.Schedules ??= new List<CsesDaySchedule>();
            foreach (var day in table.Schedules)
            {
                day.Classes ??= new List<CsesClass>();
                day.Weeks = (day.Weeks ?? "all").Trim().ToLowerInvariant();
                if (day.Weeks != "odd" && day.Weeks != "even") day.Weeks = "all";
            }

            if (table.Schedules.All(d => d.Classes.Count == 0))
            {
                error = "这个文件里没有任何课程安排（schedules 为空）。请确认导出的是 CSES 的 JSON，而不是 YAML。";
                return null;
            }

            return table;
        }
        catch (JsonException ex)
        {
            error = "课表不是有效的 CSES JSON：" + ex.Message
                    + (LooksLikeYaml(json) ? "（看起来是 YAML，请在课表工具里另存为 JSON 再导入）" : "");
            return null;
        }
        catch (Exception ex)
        {
            error = "读取课表失败：" + ex.Message;
            return null;
        }
    }

    public static string Describe(CsesTimetable table)
    {
        var subjects = table.Subjects
            .Select(s => (s.Name ?? "").Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var days = table.Schedules
            .Where(d => d.Classes.Count > 0)
            .Select(d => d.EnableDay)
            .Distinct()
            .Count();
        return subjects + " 个科目 · " + days + " 天课表";
    }

    /// <summary>
    /// 找「现在」的课：正在上的优先；没有正在上的，再看前后 <paramref name="graceMinutes"/> 分钟内
    /// 最近的一节（课间 PPT 通常还开着，这时续播按钮也不该突然失效）。
    /// </summary>
    public static PptLesson? FindLesson(
        CsesTimetable? table, string? semesterStart, DateTime now, int graceMinutes = 45)
    {
        if (table is null) return null;

        var day = ((int)now.DayOfWeek + 6) % 7 + 1;     // 周一 = 1 … 周日 = 7
        var parity = WeekParity(semesterStart, now);
        PptLesson? best = null;
        var bestDistance = double.MaxValue;

        foreach (var schedule in table.Schedules)
        {
            if (schedule.EnableDay != day) continue;
            if (parity is not null && schedule.Weeks != "all" && schedule.Weeks != parity) continue;

            foreach (var item in schedule.Classes)
            {
                var subject = (item.Subject ?? "").Trim();
                if (subject.Length == 0) continue;
                if (!TryTime(item.StartTime, out var start) || !TryTime(item.EndTime, out var end)) continue;
                if (end <= start) continue;

                var startAt = now.Date + start;
                var endAt = now.Date + end;

                if (now >= startAt && now < endAt)
                {
                    return new PptLesson
                    {
                        Subject = subject, Start = start, End = end, Ongoing = true,
                    };
                }

                var distance = now < startAt
                    ? (startAt - now).TotalMinutes
                    : (now - endAt).TotalMinutes;

                if (distance <= graceMinutes && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new PptLesson { Subject = subject, Start = start, End = end };
                }
            }
        }

        return best;
    }

    /// <summary>本周是单周还是双周；没填学期第一周周一（或格式不对）返回 null。</summary>
    public static string? WeekParity(string? semesterStart, DateTime now)
    {
        if (!TryDate(semesterStart, out var start)) return null;

        var weeks = (now.Date - start.Date).Days / 7;
        if (weeks < 0) return null;               // 还没到学期，无法判断
        return weeks % 2 == 0 ? "odd" : "even";   // 第一周算单周
    }

    public static bool TryDate(string? text, out DateTime date)
    {
        date = default;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return false;
        return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out date)
               || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date);
    }

    private static bool TryTime(string? text, out TimeSpan time)
    {
        time = default;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return false;

        return TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out time)
               || TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out time)
               || TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time);
    }

    private static bool LooksLikeYaml(string json)
    {
        var head = json.TrimStart();
        return head.StartsWith("version:", StringComparison.OrdinalIgnoreCase)
               || head.StartsWith("subjects:", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>一个科目的课件进度。</summary>
public sealed class PptRecord
{
    public string File { get; set; } = "";
    public int Slide { get; set; } = 1;
    public int Total { get; set; }
    public string SavedAt { get; set; } = "";
}

/// <summary>PPT 续播的落盘数据。</summary>
public sealed class PptResumeData
{
    public int Version { get; set; } = 1;
    /// <summary>导入时用的 CSES 文件路径（只作展示）。</summary>
    public string TimetableSource { get; set; } = "";
    /// <summary>CSES 原文：原样保存，读回来再解析，避免模型字段增减时丢信息。</summary>
    public string TimetableJson { get; set; } = "";
    /// <summary>本学期第一周周一（yyyy-MM-dd），用于单双周。</summary>
    public string SemesterStart { get; set; } = "";
    /// <summary>科目名 → 上次的课件与页码。</summary>
    public Dictionary<string, PptRecord> Subjects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>课件完整路径 → 科目（同一个课件下次打开时能直接归到这个科目）。</summary>
    public Dictionary<string, string> FileSubjects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>ppt-resume.json 的读写。</summary>
public static class PptResumeStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClassSoftwareHub");

    public static string FilePath => Path.Combine(Dir, "ppt-resume.json");

    public static PptResumeData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<PptResumeData>(File.ReadAllText(FilePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (data is not null)
                {
                    // JSON 反序列化不会保留字典的忽略大小写比较器，这里补回来
                    data.Subjects = new Dictionary<string, PptRecord>(
                        data.Subjects ?? new Dictionary<string, PptRecord>(),
                        StringComparer.OrdinalIgnoreCase);
                    data.FileSubjects = new Dictionary<string, string>(
                        data.FileSubjects ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase);
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("ppt-resume", ex, "读取课件续播记录");
        }

        return new PptResumeData();
    }

    public static void Save(PptResumeData data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("ppt-resume", ex, "保存课件续播记录");
        }
    }
}
