using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.HubAi;

/// <summary>
/// 模型调用。只认 OpenAI 兼容协议，见 <see cref="AiProviders"/> 的说明。
///
/// <para>
/// ⛔⛔ 三条铁律：
/// <list type="number">
///   <item><b>密钥不进日志</b>。任何分支都只记状态码与错误类型，⛔ 不把请求头、请求体打出去。</item>
///   <item><b>错误要说人话</b>。<c>401</c> 不能原样甩给用户，要翻成"密钥无效"。</item>
///   <item><b>流式必须可取消</b>。用户点「停止」要立刻停，不能等模型吐完。</item>
/// </list>
/// </para>
/// </summary>
public static class AiClient
{
    /// <summary>
    /// 单例 <see cref="HttpClient"/>。⛔ 别每次请求都 new —— 短连接会把端口耗光，
    /// 而 AI 请求是长连接、多次往返的，复用收益很明显。
    /// </summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = Core.HttpTls.SslOptions(),
    })
    {
        // ⛔ 不能给 HttpClient 设总超时：流式回答要读几分钟，总超时到点会把**正在输出的正文**砍掉。
        //    超时改由两处分别控制：发请求等响应头（RequestTimeout）、两次收到数据之间（IdleReadTimeout）。
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>连接建立后等响应头的上限（思考型模型首个 token 可能要等几十秒）。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    /// <summary>流式读取时，两次收到数据之间的最长间隔。</summary>
    private static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 回答被 <c>max_tokens</c> 截断时追加在正文末尾的说明。
    /// <para>
    /// 直接写进 <see cref="AiTurn.Content"/> 而不只是打日志：① 用户在气泡里立刻看得见，
    /// 不会把半截结论当成完整答案；② 这句话会跟着这一轮进历史，用户追问"继续"时
    /// 模型知道自己上次没说完，能接着写而不是重头再来。
    /// </para>
    /// <para>
    /// ⚠️ 措辞不写"错误"：内容本身是有效的，只是没写完，说成错误会让用户以为要重试。
    /// </para>
    /// </summary>
    private const string TruncatedNotice =
        "\n\n（回答已达到本次输出的长度上限，上面是写到一半的部分。可以回复「继续」让我接着写，或让我分段说明。）";

    /// <summary>
    /// 连通性测试：发一个 <c>max_tokens=1</c> 的最小请求。
    /// ⚠️ 只花一个 token 的钱，但足以分辨"密钥错 / 地址错 / 模型错 / 网络不通"。
    /// </summary>
    public static async Task<AiTestResult> TestAsync(AiConfig cfg, CancellationToken ct = default)
    {
        if (!cfg.IsUsable) return AiTestResult.Fail("请先填写服务地址和模型名。");

        var watch = Stopwatch.StartNew();

        try
        {
            using var request = BuildRequest(EndpointOf(cfg.BaseUrl), cfg, new
            {
                model = cfg.Model,
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1,
                stream = false,
            });

            // HttpClient 已放开总超时，这里补上"测试请求"自己的上限
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(ct);
            watch.Stop();

            if (response.IsSuccessStatusCode)
            {
                AppLog.Step("hubai", $"连通性测试｜结果=通过｜HTTP={(int)response.StatusCode}｜耗时={watch.ElapsedMilliseconds}ms");
                return AiTestResult.Pass($"连接正常（{watch.ElapsedMilliseconds} ms）", watch.ElapsedMilliseconds);
            }

            var reason = Explain(response.StatusCode, body);
            AppLog.Warn("hubai", $"连通性测试｜结果=失败｜HTTP={(int)response.StatusCode}｜原因={reason}");
            return AiTestResult.Fail(reason, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return AiTestResult.Fail("请求超时或已取消。若开启了代理，请确认代理可用。", watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            watch.Stop();
            AppLog.Exception("hubai", ex, $"连通性测试｜层=网络｜模型={cfg.Model}｜主机={GithubRoute.HostOf(cfg.BaseUrl)}");
            return AiTestResult.Fail($"无法连接：{ex.Message}", watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            watch.Stop();
            AppLog.Exception("hubai", ex, $"连通性测试｜模型={cfg.Model}｜主机={GithubRoute.HostOf(cfg.BaseUrl)}");
            return AiTestResult.Fail("测试失败，详情见日志。", watch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 走一轮对话：流式吐文本，同时把模型要求的工具调用收齐。
    ///
    /// <para>
    /// ⛔ 这个函数**只解析、不执行**。工具由调用方（<c>AiChatService</c>）执行完再回灌，
    /// 这样"模型能做什么"完全由宿主决定，模型没有任何直接能力。
    /// </para>
    /// <para>
    /// ⚠️ 工具调用在 SSE 里是**分片**到达的：第一个 chunk 给 id 与函数名，后续 chunk 只追加
    /// <c>arguments</c> 的字符串片段。必须按 <c>index</c> 累积，不能见到一个就跑一个。
    /// </para>
    /// </summary>
    /// <param name="onDelta">每收到一段文本回调一次（UI 用它做逐字渲染）。</param>
    public static async Task<AiTurn> StreamTurnAsync(
        AiConfig cfg,
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        Action<string>? onDelta,
        Action<string>? onReasoning = null,
        CancellationToken ct = default)
    {
        if (!cfg.IsUsable) throw new InvalidOperationException("尚未配置模型服务。");

        using var request = BuildRequest(EndpointOf(cfg.BaseUrl), cfg, BuildBody(cfg, messages, tools, stream: true));

        // 只给"等响应头"设上限；拿到响应头后 disarm，否则总超时到点会把长回答拦腰砍断。
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(RequestTimeout);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var reason = Explain(response.StatusCode, body);
            AppLog.Warn("hubai", $"流式请求｜结果=失败｜HTTP={(int)response.StatusCode}｜主机={GithubRoute.HostOf(cfg.BaseUrl)}｜原因={reason}");
            throw new InvalidOperationException(reason);
        }

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var buffers = new Dictionary<int, ToolCallBuffer>();
        var usage = AiUsage.Empty;

        // 服务端给的收尾原因（stop / length / tool_calls / content_filter…）。
        // ⛔ 必须看它：被 max_tokens 截断时服务端**照样发 [DONE]、照样正常关流**，
        //    不看这个字段就会把半截结论当成完整回答交给用户，而且没有任何提示。
        var finishReason = "";

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 是否收到 SSE 的正常结束标记。干净 EOF 与 [DONE] 必须区分开，否则 CDN 提前关连接时
        // 半截结论会被当成完整回答直接展示给用户。
        var done = false;

        // 多行 data: 的暂存区。SSE 规范里**一个事件可以由连续多行 data: 组成**，
        // 事件边界是空行，不是 data: 行。
        var eventData = new StringBuilder();

        // 首行标记：BOM 只可能出现在流的最开头，剥一次就够（见下面的 TrimStart）。
        var firstLine = true;

        // 把一个（可能由多行拼成的）事件解析掉。done 在收到 [DONE] 时置位，
        // 调用方每发完一条就要检查它 —— 服务商发完 [DONE] 不一定关连接，
        // 继续 read 会一直等到空闲超时，把一次成功的回答变成 TimeoutException。
        void DispatchEvent()
        {
            if (eventData.Length == 0) return;

            var payload = eventData.ToString();
            eventData.Clear();

            if (payload == "[DONE]") { done = true; return; }

            var chunk = AccumulateChunk(payload, content, reasoning, buffers, onDelta, onReasoning);
            if (chunk.Usage is { } chunkUsage) usage = chunkUsage;
            if (!string.IsNullOrEmpty(chunk.FinishReason)) finishReason = chunk.FinishReason!;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // ⚠️ 移植：.NET 6 的 StreamReader.ReadLineAsync 没有带 CancellationToken 的重载
            //    （上游 net8 有）。这里用 WhenAny 把取消接上，并额外加一道**空闲超时**：
            //    连接没断、但对方再也不吐数据时，绝不能无限期挂着。
            // ⚠️ 每行一个独立的 CTS，读完就 Cancel：Delay 任务随之结束、定时器立刻释放。
            //    以前用 Task.Delay(Infinite, ct) 且从不释放，一整个回答下来会攒下几十上百个
            //    永不完成的定时器，直到整轮对话被取消才回收。
            var readTask = reader.ReadLineAsync();
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleReadTimeout);
            var idleTask = Task.Delay(Timeout.Infinite, idle.Token);

            if (await Task.WhenAny(readTask, idleTask) != readTask)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"模型服务已连接，但连续 {IdleReadTimeout.TotalSeconds:0} 秒没有返回新数据。");
            }

            idle.Cancel();      // 让 idleTask 立即结束，释放定时器

            var line = await readTask;
            if (line is null)
            {
                // ⚠️ 断流前先把缓冲里那条「没有空行收尾」的事件补发出去：
                //    有的服务商发完最后一块（甚至 [DONE]）就直接关连接，不补发会丢掉结尾。
                DispatchEvent();

                // 连接被对端提前关闭且没有 [DONE]：不能把半截回答当作完整结论返回。
                if (!done)
                    throw new IOException("模型流式响应被提前关闭（未收到 [DONE]），本次回答不完整，请重试。");
                break;
            }

            // ⚠️ UTF-8 BOM：有的服务商在流开头带 BOM，读出来是 "\uFEFFdata:…"，
            //    而 StartsWith(Ordinal) 不认这个前缀 ⇒ 首块正文**静默丢失**（用户看到回答开头缺一句）。
            //    StreamReader 只能吃掉"字节级 BOM"（detectEncodingFromByteOrderMarks 默认开，
            //    这里确实已吃掉），字符级 BOM 得自己剥。只剥首行，正文中间的 \uFEFF 是内容，不能动。
            if (firstLine)
            {
                line = line.TrimStart('\uFEFF');
                firstLine = false;
            }

            // 空行 = 一个 SSE 事件结束（规范里唯一的事件边界）
            if (line.Length == 0)
            {
                DispatchEvent();
                if (done) break;
                continue;
            }

            // SSE：只关心 data: 行；注释行（以 : 开头）与 event: / id: / retry: 一律跳过。
            // ⚠️ 它们夹在事件中间也没关系，事件边界只由空行决定，不能在这里结算。
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.AsSpan(5).Trim().ToString();
            if (payload.Length == 0) continue;

            // ⛔ [DONE] 必须**立刻结算并跳出**：它是完整事件，不必等空行；而服务商发完它
            //    **不一定关连接**，继续 read 会挂到 120 秒空闲超时，把一次成功的回答变成超时错误。
            if (payload == "[DONE]")
            {
                DispatchEvent();   // 先把可能还没收到空行的最后一块补发掉
                done = true;
                break;
            }

            // ⛔ 多行 data:：一个事件可以由连续多行 data: 组成，按 "\n" 拼起来再整体解析。
            //    以前逐行独立解析，第二行起必然是半截 JSON → JsonException 被当心跳吞掉，
            //    那段内容永久丢失（而且完全无声）。
            // ⚠️ 但缓冲不能无条件累积：有的服务商**一个事件只发一行 data: 且不发空行**，
            //    那种流在旧代码里是能正常跑的，改成"只认空行"就会把两条 JSON 粘成一条、
            //    两块正文一起丢（等于修复一个 bug 又造一个新的）。所以这里补一条判定：
            //    上一段已经是完整的 JSON 对象 ⇒ 它自己就是一个事件，先结算掉再收新的。
            //    正常服务商（OpenAI / DeepSeek / 通义 / Kimi / GLM）都会发空行，走不到这条。
            if (eventData.Length > 0 && LooksLikeCompleteJson(eventData))
            {
                DispatchEvent();
                if (done) break;
            }

            if (eventData.Length > 0) eventData.Append('\n');
            eventData.Append(payload);
        }

        var calls = buffers
            .OrderBy(kv => kv.Key)
            .Select(kv => new AiToolCall(
                kv.Value.Id.Length > 0 ? kv.Value.Id : $"call_{kv.Key}",
                kv.Value.Name,
                kv.Value.Args.ToString()))
            .Where(c => c.Name.Length > 0)
            .ToList();

        if (finishReason == "length")
        {
            // ⚠️ AiTurn 定义在 AiProvider.cs（本轮只允许改这个文件与 AiChatService），
            //    没有能承载"被截断"的字段，所以按 AiTimeBudget.Describe 的既有做法：
            //    正文末尾补一句说明 + 记一条警告。调用方不用改、界面上立刻看得见、
            //    这句话也会跟着 turn.Content 进历史，用户追问"继续"时模型知道上次没说完。
            AppLog.Warn("hubai",
                $"流式请求｜结果=回答被截断｜原因=finish_reason=length｜已收正文={content.Length} 字｜" +
                $"工具调用={calls.Count}｜模型={cfg.Model}");

            // 有工具调用时正文不是"结论"，截断点很可能正切在 arguments 的 JSON 中间 ——
            // 这时候往正文里塞一句话只会更乱，交给上层按工具失败处理。
            if (calls.Count == 0)
            {
                content.Append(TruncatedNotice);
                onDelta?.Invoke(TruncatedNotice);
            }
        }

        return new AiTurn(content.ToString(), reasoning.ToString(), calls, usage);
    }

    /// <summary>
    /// 缓冲里的内容是否已经是一个**完整**的 JSON 对象。
    /// 只用于一条兜底：判断"上一条 data: 是不是早就该结算了"（服务商漏发空行时）。
    /// 正常服务商（OpenAI / DeepSeek / 通义 / Kimi / GLM）都会发空行，走不到这里。
    /// </summary>
    private static bool LooksLikeCompleteJson(StringBuilder sb)
    {
        try
        {
            using var doc = JsonDocument.Parse(sb.ToString());
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>流式响应的一块增量：正文字 + 工具调用分片。</summary>
    private sealed class ToolCallBuffer
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Args = new();
    }

    /// <summary>
    /// 一块 SSE 数据解析出来的**元信息**（正文/思考/工具分片是直接写进入参缓冲的，不进这里）。
    /// <para>
    /// ⚠️ 抽成结构而不是继续返回 <c>AiUsage?</c>：<c>finish_reason</c> 和 <c>usage</c> 可能
    /// 出现在**不同**的块里（多数服务商把 usage 放在 [DONE] 前那个 choices 为空数组的块，
    /// 而 finish_reason 在最后一块正常 delta 上），一个返回值槽位装不下两样东西。
    /// 用 <c>struct</c> + <c>default</c> 表达"这块什么都没有"，不需要可空判断。
    /// </para>
    /// </summary>
    private readonly struct ChunkMeta
    {
        /// <summary>本块携带的用量；<c>null</c> = 这块没有。</summary>
        public AiUsage? Usage { get; init; }

        /// <summary>本块携带的收尾原因（stop / length / tool_calls / content_filter）；<c>null</c> = 这块没有。</summary>
        public string? FinishReason { get; init; }
    }

    /// <summary>处理一块 SSE 数据，把正文/思考/工具分片累积进入参缓冲，返回本块的元信息。</summary>
    private static ChunkMeta AccumulateChunk(
        string json,
        StringBuilder content,
        StringBuilder reasoning,
        Dictionary<int, ToolCallBuffer> buffers,
        Action<string>? onDelta,
        Action<string>? onReasoning)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            // ⚠️ usage 要在看 choices 之前处理：带用量那一块的 choices 往往是**空数组**，
            //    先判断 choices 就会把用量直接丢掉。
            AiUsage? usage = null;
            if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new AiUsage(
                    u.TryGetProperty("prompt_tokens", out var pt) ? IntOf(pt) : 0,
                    u.TryGetProperty("completion_tokens", out var ctk) ? IntOf(ctk) : 0,
                    u.TryGetProperty("total_tokens", out var tt) ? IntOf(tt) : 0);
            }

            if (!doc.RootElement.TryGetProperty("choices", out var choices))
                return new ChunkMeta { Usage = usage };

            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return new ChunkMeta { Usage = usage };

            // ⛔ finish_reason 必须在取 delta **之前**读：截断/收尾那一块的 delta 常常是
            //    空对象或干脆没有 delta 字段，先 return 就会把它永久漏掉。
            //    这正是"回答被 max_tokens 截断却毫无提示"的根因 —— 流照常 [DONE]，
            //    客户端只认 delta，于是把半截结论当成了完整回答。
            string? finishReason = null;
            if (choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                var value = fr.GetString();
                if (!string.IsNullOrEmpty(value)) finishReason = value;
            }

            var meta = new ChunkMeta { Usage = usage, FinishReason = finishReason };

            if (!choices[0].TryGetProperty("delta", out var delta))
                return meta;

            // 思考过程（DeepSeek / 通义 / Kimi / GLM 都叫 reasoning_content，字段名是统一的）
            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                var think = rc.GetString();
                if (!string.IsNullOrEmpty(think))
                {
                    reasoning.Append(think);
                    onReasoning?.Invoke(think);
                }
            }

            // 正文
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    content.Append(s);
                    onDelta?.Invoke(s);
                }
            }

            // 工具调用（分片）
            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var index = tc.TryGetProperty("index", out var idx) ? IntOf(idx) : 0;

                    if (!buffers.TryGetValue(index, out var buffer))
                    {
                        buffer = new ToolCallBuffer();
                        buffers[index] = buffer;
                    }

                    if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        buffer.Id = id.GetString() ?? buffer.Id;

                    if (!tc.TryGetProperty("function", out var fn)) continue;

                    if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        buffer.Name = name.GetString() ?? buffer.Name;

                    if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        buffer.Args.Append(args.GetString());
                }
            }

            return meta;
        }
        catch (JsonException)
        {
            // 个别服务商会插非 JSON 的心跳，忽略即可
            return default;
        }
    }

    /// <summary>
    /// 宽容地读一个整数。⚠️ 不能直接 <c>GetInt32()</c>：服务商偶尔把 token 数 / index 写成
    /// <c>12.0</c>（JSON 浮点），GetInt32 会抛 InvalidOperationException，而这里只兜了 JSON 语法错误。
    /// </summary>
    private static int IntOf(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Number) return 0;
        if (el.TryGetInt32(out var i)) return i;
        return el.TryGetDouble(out var d) && double.IsFinite(d) ? (int)Math.Round(d) : 0;
    }

    /// <summary>拼请求体。工具清单只在传了的时候才带，避免"没装工具包却收到空 tools 数组"。</summary>
    private static Dictionary<string, object?> BuildBody(
        AiConfig cfg,
        IReadOnlyList<AiMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = cfg.Model,
            ["messages"] = messages.Select(m => ToPayload(m, cfg)).ToList(),
            ["stream"] = stream,
            ["temperature"] = cfg.Temperature,
        };

        // 流式响应默认**不**返回用量，必须显式索取 —— 否则界面上"本次消耗 token"永远是 0。
        if (stream) body["stream_options"] = new { include_usage = true };

        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Id,
                    description = t.Description,
                    parameters = t.Parameters,
                },
            }).ToList();
        }

        ApplyThinking(body, cfg);

        return body;
    }

    /// <summary>
    /// 按服务商翻译"思考开关"（已逐家对照官方文档核对）。
    ///
    /// <para>
    /// ⚠️ 同一个能力，四家四种写法：
    /// <list type="bullet">
    ///   <item>DeepSeek / Kimi / GLM：嵌套 <c>thinking: { type }</c>，Kimi 要 <c>keep</c>、GLM 要 <c>clear_thinking</c>；</item>
    ///   <item>通义：顶层布尔 <c>enable_thinking</c>，且**开启时必须带 <c>incremental_output</c>**，否则直接报错；</item>
    ///   <item>OpenAI o 系列：没有开关，只有 <c>reasoning_effort</c>。</item>
    /// </list>
    /// ⛔ 别把用户选的值原样透传 —— 发错字段轻则无效，重则 400。
    /// </para>
    /// </summary>
    private static void ApplyThinking(Dictionary<string, object?> body, AiConfig cfg)
    {
        var profile = cfg.ThinkingProfile;

        // 强制思考的模型（glm-5.3 等）关不掉，传 disabled 会直接报错
        var enabled = profile.ForcedThinking || cfg.Thinking;

        switch (profile.Style)
        {
            case ThinkingStyle.ThinkingObject:
                body["thinking"] = new { type = enabled ? "enabled" : "disabled" };
                break;

            case ThinkingStyle.EnableThinkingFlag:
                body["enable_thinking"] = enabled;
                // DashScope 在 enable_thinking=true 时不带这个参数会报错
                if (enabled) body["incremental_output"] = true;
                break;

            case ThinkingStyle.ThinkingObjectKeep:
                // keep=all：把历史思考留在上下文里，多步工具调用才连得上
                body["thinking"] = new { type = enabled ? "enabled" : "disabled", keep = "all" };
                break;

            case ThinkingStyle.ThinkingObjectPreserve:
                body["thinking"] = new { type = enabled ? "enabled" : "disabled", clear_thinking = false };
                break;

            case ThinkingStyle.EffortOnly:
            case ThinkingStyle.None:
            default:
                break;
        }

        if (profile.SupportsEffort && enabled)
            body["reasoning_effort"] = NormalizeEffort(cfg.Effort, cfg.ProviderId);
    }

    /// <summary>强度取值各家不同：DeepSeek/GLM 认 max，OpenAI 最高只到 high。</summary>
    private static string NormalizeEffort(string? effort, string providerId)
    {
        var value = (effort ?? "").Trim().ToLowerInvariant();
        if (value != "low" && value != "high" && value != "max") value = "high";

        if (providerId == "openai" && value == "max") return "high";
        return value;
    }

    /// <summary>
    /// 一条消息的线上格式。<c>assistant</c> 带工具调用时 <c>content</c> 必须是 null，
    /// <c>tool</c> 消息必须带 <c>tool_call_id</c> —— 少一样服务端直接 400。
    /// </summary>
    private static object ToPayload(AiMessage m, AiConfig cfg)
    {
        if (m.Role == "assistant")
        {
            var payload = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = m.Content.Length > 0 ? m.Content : null,
            };

            if (m.ToolCalls is { Count: > 0 })
            {
                payload["tool_calls"] = m.ToolCalls.Select(t => new
                {
                    id = t.Id,
                    type = "function",
                    function = new { name = t.Name, arguments = t.Arguments },
                }).ToList();
            }

            // ⚠️ 带 tools 的思考模型（DeepSeek / Kimi / GLM）要求历史 reasoning 原样回传，
            //    缺了会返回 400。不回传思考的服务商就别多塞这个字段。
            if (cfg.ThinkingProfile.RequiresReasoningEcho && !string.IsNullOrEmpty(m.Reasoning))
                payload["reasoning_content"] = m.Reasoning;

            return payload;
        }

        if (m.Role == "tool")
        {
            return new { role = "tool", tool_call_id = m.ToolCallId ?? "", content = m.Content };
        }

        return new { role = m.Role, content = m.Content };
    }

    // ══════════════════════════════════════════════════════
    //  内部
    // ══════════════════════════════════════════════════════

    private static string EndpointOf(string baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? b
            : b + "/chat/completions";
    }

    private static HttpRequestMessage BuildRequest(string url, AiConfig cfg, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        // 本地模型通常不校验密钥，空着就不发 Authorization 头（有些实现见到空 Bearer 会报错）
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

        return request;
    }

    /// <summary>把 HTTP 状态码翻成用户能看懂的一句话。</summary>
    private static string Explain(HttpStatusCode status, string? body)
    {
        var snippet = (body ?? "").Trim();
        if (snippet.Length > 160) snippet = snippet[..160] + "…";

        return status switch
        {
            HttpStatusCode.Unauthorized => "密钥无效或未填写。",
            HttpStatusCode.Forbidden => "密钥无权访问该模型，请检查服务商后台的权限设置。",
            HttpStatusCode.NotFound => "地址或模型名不存在，请检查服务地址与模型名称。",
            HttpStatusCode.TooManyRequests => "请求过于频繁或额度已用尽，请稍后再试。",
            HttpStatusCode.PaymentRequired => "该账户余额不足。",
            _ when (int)status >= 500 => $"服务商暂时不可用（HTTP {(int)status}）。",
            _ => $"请求失败（HTTP {(int)status}）" + (snippet.Length > 0 ? $"：{snippet}" : "。"),
        };
    }
}
