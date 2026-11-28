using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.SlideSync;

namespace ClassSoftwareHub.Desktop.Services
{
    /// <summary>
    /// 课件续页的本机 HTTP 接口。只绑定 127.0.0.1，业务请求必须带 X-Api-Token
    /// （或 ?token=）。和 AnnounceLanService 一样手写最小 HTTP，不引 Kestrel。
    /// </summary>
    public sealed class SlideSyncApi : IDisposable
    {
        private const int MaxBodyBytes = 64 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            // 中文科目名直接输出，老师用浏览器看也能读懂。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly PptResumeService _service;
        private readonly TcpListener _listener;
        private readonly string _token;
        private readonly CancellationTokenSource _cts = new();
        private int _started;

        public int Port { get; }

        public SlideSyncApi(PptResumeService service, int port, string token)
        {
            _service = service;
            Port = port >= 1024 && port <= 65535 ? port : 47822;
            _token = token ?? "";
            _listener = new TcpListener(IPAddress.Loopback, Port);
        }

        public bool Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return true;
            try
            {
                _listener.Start(16);
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
                AppLog.Step("slidesync-api", "本机接口已启动｜地址=http://127.0.0.1:" + Port);
                return true;
            }
            catch (Exception ex)
            {
                _started = 0;
                AppLog.Catch("slidesync-api", "启动本机接口", ex);
                return false;
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _cts.Dispose(); } catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Catch("slidesync-api", "接受本机接口连接", ex, LogLevel.Debug);
                    try { await Task.Delay(300, token).ConfigureAwait(false); } catch { return; }
                    continue;
                }

                _ = Task.Run(() => HandleAsync(client, token));
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    using var stream = client.GetStream();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));

                    var (head, leftover) = await HttpHeadReader.ReadAsync(stream, cts.Token).ConfigureAwait(false);
                    if (head is null) return;

                    var lines = Encoding.ASCII.GetString(head).Split("\r\n");
                    var parts = (lines.Length > 0 ? lines[0] : "").Split(' ');
                    if (parts.Length < 2)
                    {
                        await WriteJsonAsync(stream, 400, "{\"error\":\"bad_request\"}").ConfigureAwait(false);
                        return;
                    }

                    var method = parts[0].ToUpperInvariant();
                    var target = parts[1];
                    var q = target.IndexOf('?');
                    var path = q >= 0 ? target.Substring(0, q) : target;
                    var query = q >= 0 ? target.Substring(q + 1) : "";

                    if (PathIs(path, "/api/slidesync/health", "/api/slidesync/ci/health"))
                    {
                        await WriteJsonAsync(stream, 200, HealthJson()).ConfigureAwait(false);
                        return;
                    }

                    var headers = ParseHeaders(lines);
                    var givenToken = headers.TryGetValue("X-Api-Token", out var headerToken)
                        ? headerToken
                        : QueryValue(query, "token");
                    if (!TokenOk(givenToken))
                    {
                        await WriteJsonAsync(stream, 401, "{\"error\":\"unauthorized\"}").ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && PathIs(path, "/api/slidesync/status", "/api/slidesync/ci/status"))
                    {
                        await WriteJsonAsync(stream, 200, StatusJson()).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && PathIs(path, "/api/slidesync/subjects", "/api/slidesync/ci/subjects"))
                    {
                        await WriteJsonAsync(stream, 200, SubjectsJson()).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && PathIs(path, "/api/slidesync/lesson", "/api/slidesync/ci/lesson"))
                    {
                        await WriteJsonAsync(stream, 200, LessonJson()).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && PathIs(path, "/api/slidesync/timetable", "/api/slidesync/ci/timetable"))
                    {
                        var json = TimetableJson();
                        if (json is null)
                            await WriteJsonAsync(stream, 404, "{\"error\":\"no_timetable\"}").ConfigureAwait(false);
                        else
                            await WriteJsonAsync(stream, 200, json).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && PathIs(path, "/api/slidesync/context", "/api/slidesync/ci/context"))
                    {
                        await WriteJsonAsync(stream, 200, ContextJson()).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && PathIs(path, "/api/slidesync/next", "/api/slidesync/ci/next"))
                    {
                        await WriteJsonAsync(stream, 200, NextJson()).ConfigureAwait(false);
                        return;
                    }

                    if (method == "POST" && PathIs(path, "/api/slidesync/timetable", "/api/slidesync/ci/timetable", "/api/slidesync/ci/sync"))
                    {
                        var (body, bodyError) = await ReadBodyAsync(stream, leftover, headers, cts.Token).ConfigureAwait(false);
                        if (bodyError is not null)
                        {
                            await WriteBodyErrorAsync(stream, bodyError).ConfigureAwait(false);
                            return;
                        }
                        await ImportTimetableAsync(stream, body ?? "", query).ConfigureAwait(false);
                        return;
                    }

                    if (method == "POST" && path.Equals("/api/slidesync/resume", StringComparison.OrdinalIgnoreCase))
                    {
                        var (body, bodyError) = await ReadBodyAsync(stream, leftover, headers, cts.Token).ConfigureAwait(false);
                        if (bodyError is not null)
                        {
                            await WriteBodyErrorAsync(stream, bodyError).ConfigureAwait(false);
                            return;
                        }
                        await ResumeAsync(stream, body ?? "").ConfigureAwait(false);
                        return;
                    }

                    if (method == "POST" && path.Equals("/api/slidesync/jump", StringComparison.OrdinalIgnoreCase))
                    {
                        var (body, bodyError) = await ReadBodyAsync(stream, leftover, headers, cts.Token).ConfigureAwait(false);
                        if (bodyError is not null)
                        {
                            await WriteBodyErrorAsync(stream, bodyError).ConfigureAwait(false);
                            return;
                        }
                        await JumpAsync(stream, body ?? "").ConfigureAwait(false);
                        return;
                    }

                    await WriteJsonAsync(stream, 404, "{\"error\":\"not_found\"}").ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppLog.Catch("slidesync-api", "处理本机接口请求", ex, LogLevel.Debug);
                }
            }
        }

        private string HealthJson()
        {
            var version = "";
            try
            {
                var info = typeof(SlideSyncApi).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (info.Length > 0 && info[0] is System.Reflection.AssemblyInformationalVersionAttribute iv)
                {
                    version = iv.InformationalVersion ?? "";
                    var plus = version.IndexOf('+');
                    if (plus > 0) version = version.Substring(0, plus);
                }
            }
            catch { }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                module = "slidesync",
                version,
                port = Port,
            }, JsonOptions);
        }

        private string StatusJson()
        {
            var snap = _service.Snapshot();
            var lesson = snap.Lesson;
            var appearance = _service.Appearance;
            return JsonSerializer.Serialize(new
            {
                ok = true,
                armed = _service.Armed,
                armReason = _service.ArmReason,
                playing = snap.Live.Playing,
                file = snap.Live.File,
                slide = snap.Live.Slide,
                total = snap.Live.Total,
                subject = snap.Live.Subject,
                status = snap.Live.Status,
                lesson = lesson == null ? null : new
                {
                    subject = lesson.Subject,
                    time = lesson.TimeText,
                    ongoing = lesson.Ongoing,
                },
                message = snap.LastMessage,
                appearance = appearance == null ? null : new
                {
                    followsTheme = appearance.FollowsTheme,
                    background = string.Format("#{0:X2}{1:X2}{2:X2}",
                        appearance.BgR, appearance.BgG, appearance.BgB),
                    accent = string.Format("#{0:X2}{1:X2}{2:X2}",
                        appearance.AccentR, appearance.AccentG, appearance.AccentB),
                    opacity = appearance.BgAlpha,
                },
            }, JsonOptions);
        }

        private string SubjectsJson()
        {
            var snap = _service.Snapshot();
            var subjects = snap.Subjects.Select(s => new
            {
                name = s.Name,
                inTimetable = s.InTimetable,
                file = s.File,
                fileName = s.FileName,
                slide = s.Slide,
                total = s.Total,
                savedAt = s.SavedAt,
                hasRecord = s.HasRecord,
            }).ToList();

            return JsonSerializer.Serialize(new { ok = true, subjects }, JsonOptions);
        }

        private string LessonJson()
        {
            var lesson = _service.CurrentLesson();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                lesson = lesson == null ? null : new
                {
                    subject = lesson.Subject,
                    time = lesson.TimeText,
                    ongoing = lesson.Ongoing,
                },
            }, JsonOptions);
        }

        private static bool PathIs(string path, params string[] candidates)
        {
            foreach (var candidate in candidates)
                if (path.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private string? TimetableJson()
        {
            var snap = _service.Snapshot();
            var table = snap.Timetable;
            if (table is null) return null;

            var now = DateTime.Now;
            var parity = CsesSchedule.WeekParity(snap.SemesterStart, now);
            var days = new List<object>();
            for (var weekday = 1; weekday <= 7; weekday++)
            {
                var classes = new List<(string Subject, TimeSpan Start, TimeSpan End, string Weeks, bool Ongoing)>();
                foreach (var schedule in table.Schedules)
                {
                    if (schedule.EnableDay != weekday) continue;
                    var weeks = NormalizeWeeks(schedule.Weeks);
                    if (weeks != "all" && parity is not null && weeks != parity) continue;

                    foreach (var item in schedule.Classes)
                    {
                        var subject = (item.Subject ?? "").Trim();
                        if (subject.Length == 0) continue;
                        if (!TryTime(item.StartTime, out var start) || !TryTime(item.EndTime, out var end)) continue;
                        if (end <= start) continue;

                        var ongoing = weekday == WeekdayOf(now)
                                      && now.TimeOfDay >= start
                                      && now.TimeOfDay < end;
                        classes.Add((subject, start, end, weeks, ongoing));
                    }
                }

                classes.Sort((a, b) =>
                {
                    var byStart = a.Start.CompareTo(b.Start);
                    if (byStart != 0) return byStart;
                    var byEnd = a.End.CompareTo(b.End);
                    return byEnd != 0 ? byEnd : StringComparer.OrdinalIgnoreCase.Compare(a.Subject, b.Subject);
                });

                days.Add(new
                {
                    weekday,
                    name = WeekdayName(weekday),
                    classes = classes.Select(c => new
                    {
                        subject = c.Subject,
                        start = FormatTime(c.Start),
                        end = FormatTime(c.End),
                        time = FormatTime(c.Start) + "–" + FormatTime(c.End),
                        weeks = c.Weeks,
                        ongoing = c.Ongoing,
                    }).ToList(),
                });
            }

            var subjects = table.Subjects
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .GroupBy(s => (s.Name ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new
                {
                    name = (s.Name ?? "").Trim(),
                    simplifiedName = s.SimplifiedName,
                    teacher = s.Teacher,
                    room = s.Room,
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                ok = true,
                now = IsoDateTime(now),
                date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                time = now.ToString("HH:mm", CultureInfo.InvariantCulture),
                weekday = WeekdayOf(now),
                semesterStart = (snap.SemesterStart ?? "").Trim(),
                weekType = parity,
                source = SourceFileName(snap.TimetableSource),
                updatedAt = SourceUpdatedAt(snap.TimetableSource),
                subjects,
                days,
            }, JsonOptions);
        }

        private string ContextJson()
        {
            var now = DateTime.Now;
            var snap = _service.Snapshot();
            var current = _service.CurrentOngoingLesson(now);
            var upcoming = _service.NextLesson(now);
            var live = snap.Live;
            var liveSubject = (live.Subject ?? "").Trim();
            var subject = liveSubject.Length > 0 ? liveSubject : current?.Subject ?? "";
            var record = snap.Subjects.FirstOrDefault(s =>
                string.Equals(s.Name, subject, StringComparison.OrdinalIgnoreCase));

            return JsonSerializer.Serialize(new
            {
                ok = true,
                now = IsoDateTime(now),
                date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                time = now.ToString("HH:mm", CultureInfo.InvariantCulture),
                weekday = WeekdayOf(now),
                timetableAvailable = snap.Timetable is not null,
                weekType = snap.Timetable is null
                    ? null
                    : CsesSchedule.WeekParity(snap.SemesterStart, now),
                ongoing = current is not null,
                current = ScheduledLessonPayload(current),
                upcoming = ScheduledLessonPayload(upcoming),
                nearby = NearbyLessonPayload(_service.CurrentLesson(now), now),
                playing = live.Playing,
                status = live.Status,
                armed = _service.Armed,
                armReason = _service.ArmReason,
                subject,
                fileName = LiveFileName(live.File),
                slide = live.Slide,
                total = live.Total,
                hasCourseware = record?.HasRecord == true,
                recordSlide = record?.Slide ?? 0,
                recordTotal = record?.Total ?? 0,
            }, JsonOptions);
        }

        private string NextJson()
        {
            var now = DateTime.Now;
            var lesson = _service.NextLesson(now);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                now = IsoDateTime(now),
                lesson = ScheduledLessonPayload(lesson),
                next = ScheduledLessonPayload(lesson),
            }, JsonOptions);
        }

        private static object? ScheduledLessonPayload(PptScheduledLesson? lesson)
        {
            return lesson is null ? null : LessonPayload(
                lesson.Subject, lesson.Date, lesson.Weekday, lesson.Weeks,
                lesson.Start, lesson.End, lesson.Ongoing);
        }

        private static object? NearbyLessonPayload(PptLesson? lesson, DateTime now)
        {
            return lesson is null ? null : LessonPayload(
                lesson.Subject, now.Date, WeekdayOf(now), null,
                lesson.Start, lesson.End, lesson.Ongoing);
        }

        private static object LessonPayload(
            string subject, DateTime date, int weekday, string? weeks,
            TimeSpan start, TimeSpan end, bool ongoing)
        {
            var startAt = date + start;
            var endAt = date + end;
            return new
            {
                subject,
                date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                weekday,
                weeks,
                start = FormatTime(start),
                end = FormatTime(end),
                startAt = IsoDateTime(startAt),
                endAt = IsoDateTime(endAt),
                time = FormatTime(start) + "–" + FormatTime(end),
                ongoing,
            };
        }

        private async Task ImportTimetableAsync(NetworkStream stream, string body, string query)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                await WriteJsonAsync(stream, 400, "{\"error\":\"empty_body\"}").ConfigureAwait(false);
                return;
            }

            string json;
            string semesterStart = QueryValue(query, "semesterStart");
            var source = QueryValue(query, "source");
            if (source.Length == 0) source = "ci";
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    await WriteJsonAsync(stream, 400, "{\"error\":\"timetable_object_required\"}").ConfigureAwait(false);
                    return;
                }

                if (root.TryGetProperty("semesterStart", out var semester) && semester.ValueKind == JsonValueKind.String)
                    semesterStart = semester.GetString() ?? semesterStart;
                if (root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
                    source = src.GetString() ?? source;

                if (root.TryGetProperty("timetable", out var timetable))
                    json = TimetableText(timetable) ?? "";
                else if (root.TryGetProperty("json", out var jsonNode) && jsonNode.ValueKind == JsonValueKind.String)
                    json = jsonNode.GetString() ?? "";
                else if (root.TryGetProperty("cses", out var cses))
                    json = TimetableText(cses) ?? "";
                else
                    json = body;    // 直接 POST 一份 CSES JSON
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                await WriteJsonAsync(stream, 400, "{\"error\":\"bad_json\"}").ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                await WriteJsonAsync(stream, 400, "{\"error\":\"timetable_required\"}").ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(semesterStart) && !CsesSchedule.TryDate(semesterStart, out _))
            {
                await WriteJsonAsync(stream, 400, "{\"error\":\"invalid_semester_start\"}").ConfigureAwait(false);
                return;
            }

            var (ok, message) = _service.ImportCsesJson(json, source);
            if (!ok)
            {
                await WriteJsonAsync(stream, 400,
                    JsonSerializer.Serialize(new { error = "invalid_timetable", message }, JsonOptions))
                    .ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(semesterStart))
            {
                var applied = _service.ApplySemesterStart(semesterStart);
                if (!applied.Ok)
                {
                    await WriteJsonAsync(stream, 400,
                        JsonSerializer.Serialize(new { error = "invalid_semester_start", message = applied.Message }, JsonOptions))
                        .ConfigureAwait(false);
                    return;
                }
            }

            await WriteJsonAsync(stream, 200,
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    message,
                    semesterStart = (_service.Snapshot().SemesterStart ?? "").Trim(),
                }, JsonOptions)).ConfigureAwait(false);
        }

        private static string? TimetableText(JsonElement node)
        {
            return node.ValueKind switch
            {
                JsonValueKind.String => node.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => node.GetRawText(),
                _ => null,
            };
        }

        private static string NormalizeWeeks(string? weeks)
        {
            var value = (weeks ?? "all").Trim().ToLowerInvariant();
            return value == "odd" || value == "even" ? value : "all";
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

        private static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        private static int WeekdayOf(DateTime date) => ((int)date.DayOfWeek + 6) % 7 + 1;

        private static string WeekdayName(int weekday) => weekday switch
        {
            1 => "周一",
            2 => "周二",
            3 => "周三",
            4 => "周四",
            5 => "周五",
            6 => "周六",
            _ => "周日",
        };

        private static string? IsoDateTime(DateTime? value) => value?.ToString(
            "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

        private static string SourceFileName(string? source)
        {
            var value = (source ?? "").Trim();
            if (value.Length == 0) return "";
            try { return Path.GetFileName(value); }
            catch { return ""; }
        }

        private static string? SourceUpdatedAt(string? source)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
                    return IsoDateTime(File.GetLastWriteTime(source));
            }
            catch { }
            return null;
        }

        private static string LiveFileName(string? file)
        {
            var value = file ?? "";
            if (value.Length == 0) return "";
            try
            {
                return SeewoBridge.IsSeewoKey(value)
                    ? SeewoBridge.DisplayName(value)
                    : Path.GetFileName(value);
            }
            catch { return ""; }
        }

        private async Task ResumeAsync(NetworkStream stream, string body)
        {
            var subject = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("subject", out var p))
                    {
                        subject = (p.GetString() ?? "").Trim();
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                await WriteJsonAsync(stream, 400, "{\"error\":\"bad_json\"}").ConfigureAwait(false);
                return;
            }

            if (subject.Length == 0)
            {
                var lesson = _service.CurrentLesson();
                if (lesson is null)
                {
                    await WriteJsonAsync(stream, 409, "{\"error\":\"no_lesson\"}").ConfigureAwait(false);
                    return;
                }
                subject = lesson.Subject;
            }

            if (_service.RecordFile(subject) is null)
            {
                await WriteJsonAsync(stream, 404,
                    JsonSerializer.Serialize(new { error = "no_record", subject }, JsonOptions))
                    .ConfigureAwait(false);
                return;
            }

            _service.Resume(subject);
            await WriteJsonAsync(stream, 200,
                JsonSerializer.Serialize(new { ok = true, subject }, JsonOptions)).ConfigureAwait(false);
        }

        private async Task JumpAsync(NetworkStream stream, string body)
        {
            string file = "";
            var slide = 0;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    await WriteJsonAsync(stream, 400, "{\"error\":\"bad_json\"}").ConfigureAwait(false);
                    return;
                }
                if (doc.RootElement.TryGetProperty("file", out var f)) file = (f.GetString() ?? "").Trim();
                if (doc.RootElement.TryGetProperty("slide", out var s) && s.TryGetInt32(out var n)) slide = n;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                await WriteJsonAsync(stream, 400, "{\"error\":\"bad_json\"}").ConfigureAwait(false);
                return;
            }

            if (file.Length == 0 || slide <= 0)
            {
                await WriteJsonAsync(stream, 400, "{\"error\":\"file_and_slide_required\"}").ConfigureAwait(false);
                return;
            }

            if (!SeewoBridge.IsSeewoKey(file) && !File.Exists(file))
            {
                await WriteJsonAsync(stream, 404, "{\"error\":\"file_not_found\"}").ConfigureAwait(false);
                return;
            }

            _service.RequestJump(file, slide);
            await WriteJsonAsync(stream, 202,
                JsonSerializer.Serialize(new { ok = true, file, slide }, JsonOptions)).ConfigureAwait(false);
        }

        private static async Task<(string? Body, string? Error)> ReadBodyAsync(
            NetworkStream stream, byte[] leftover, Dictionary<string, string> headers, CancellationToken token)
        {
            if (!headers.TryGetValue("Content-Length", out var raw)) return ("", null);
            if (!int.TryParse(raw, out var length) || length < 0) return (null, "bad_content_length");
            if (length == 0) return ("", null);
            if (length > MaxBodyBytes) return (null, "body_too_large");

            var body = new byte[length];
            var read = Math.Min(leftover.Length, length);
            if (read > 0) Array.Copy(leftover, body, read);
            while (read < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(read, length - read), token).ConfigureAwait(false);
                if (n <= 0) break;
                read += n;
            }
            // 对端提前断开时不能把半截 JSON 当成完整请求继续处理。
            if (read < length) return (null, "body_incomplete");
            return (Encoding.UTF8.GetString(body, 0, read), null);
        }

        private static Task WriteBodyErrorAsync(NetworkStream stream, string error)
        {
            return WriteJsonAsync(stream, 400,
                JsonSerializer.Serialize(new { error }, JsonOptions));
        }

        private bool TokenOk(string? given)
        {
            if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(given)) return false;
            var a = Encoding.UTF8.GetBytes(given);
            var b = Encoding.UTF8.GetBytes(_token);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static Dictionary<string, string> ParseHeaders(string[] lines)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }
            return headers;
        }

        private static string QueryValue(string query, string key)
        {
            foreach (var pair in query.Split('&'))
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0) continue;
                if (!pair.Substring(0, idx).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                try { return Uri.UnescapeDataString(pair.Substring(idx + 1)); }
                catch { return pair.Substring(idx + 1); }
            }
            return "";
        }

        private static async Task WriteJsonAsync(NetworkStream stream, int status, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var reason = status switch
            {
                200 => "OK",
                202 => "Accepted",
                400 => "Bad Request",
                401 => "Unauthorized",
                404 => "Not Found",
                409 => "Conflict",
                _ => "Error",
            };
            var head = "HTTP/1.1 " + status + " " + reason + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Connection: close\r\n"
                + "Cache-Control: no-store\r\n"
                + "X-Content-Type-Options: nosniff\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
            await stream.WriteAsync(body).ConfigureAwait(false);
        }
    }
}