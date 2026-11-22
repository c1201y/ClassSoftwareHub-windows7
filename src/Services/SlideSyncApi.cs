using System;
using System.Collections.Generic;
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

                    if (path.Equals("/api/slidesync/health", StringComparison.OrdinalIgnoreCase))
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

                    if (method == "GET" && path.Equals("/api/slidesync/status", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonAsync(stream, 200, StatusJson()).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && path.Equals("/api/slidesync/subjects", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonAsync(stream, 200, SubjectsJson()).ConfigureAwait(false);
                        return;
                    }

                    if (method == "GET" && path.Equals("/api/slidesync/lesson", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonAsync(stream, 200, LessonJson()).ConfigureAwait(false);
                        return;
                    }

                    if (method == "POST" && path.Equals("/api/slidesync/resume", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = await ReadBodyAsync(stream, leftover, headers, cts.Token).ConfigureAwait(false);
                        await ResumeAsync(stream, body).ConfigureAwait(false);
                        return;
                    }

                    if (method == "POST" && path.Equals("/api/slidesync/jump", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = await ReadBodyAsync(stream, leftover, headers, cts.Token).ConfigureAwait(false);
                        await JumpAsync(stream, body).ConfigureAwait(false);
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
            catch (JsonException)
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
            catch (JsonException)
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

        private static async Task<string> ReadBodyAsync(
            NetworkStream stream, byte[] leftover, Dictionary<string, string> headers, CancellationToken token)
        {
            var length = 0;
            if (headers.TryGetValue("Content-Length", out var raw)) int.TryParse(raw, out length);
            if (length <= 0) return "";
            if (length > MaxBodyBytes) throw new InvalidDataException("body too large");

            var body = new byte[length];
            var read = Math.Min(leftover.Length, length);
            if (read > 0) Array.Copy(leftover, body, read);
            while (read < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(read, length - read), token).ConfigureAwait(false);
                if (n <= 0) break;
                read += n;
            }
            return Encoding.UTF8.GetString(body, 0, read);
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