using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Diagnostics;
using ClassSoftwareHub.Desktop.Services.HubAi;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// Hub AI 提问浮层（自上游抽出的公共控件，已移植）。
///
/// <para>
/// 用法：页面根 Grid 里放一行 <c>&lt;controls:HubAiFlyout x:Name="AiFlyout" /&gt;</c>，
/// 页头放一个按钮，点击调 <see cref="Toggle"/>。
/// </para>
///
/// <para>
/// ⛔⛔ 这里**不是**另一套对话：跑的是真正的 <see cref="HubAiRuntime"/>，
/// 与 Hub AI 页同一套上下文、同一套工具、同一套存档格式；
/// 问答写进会话存档，跳到 Hub AI 页能翻到，思考过程、工具轨迹都在那儿。
/// </para>
///
/// <para>
/// ⭐ **每次展开都另起一段对话**（约定：新开一个浮层 = 新开一段对话）：
/// 展开时拿一份新的草稿运行时，不带上一次的消息；这一段里的追问仍接在同一条上。
/// </para>
/// </summary>
public sealed partial class HubAiFlyout : UserControl
{
    private HubAiRuntime? _runtime;

    /// <summary>本次展开这段对话的会话 id（null = 还没存档）。每次展开重置。</summary>
    private string? _sessionId;

    private StackPanel? _answerPanel;
    private string _answerRendered = "";
    private bool _busy;

    // ── 出场/收起动画（轻微放大 + 8px 归位 + 淡入） ──────────
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private int _appearVersion;

    /// <summary>
    /// 「正在思考」占位气泡：长时间思考时避免卡片毫无反应。
    /// 放在助手气泡**下面**、正文一到就撤掉 —— 独立控件而不是塞进气泡内部，
    /// 因为正文是 <see cref="MarkdownText.Apply"/> 每次整块重绘的，塞进去会被一起清掉。
    /// </summary>
    private Border? _thinkingBubble;
    private ProgressRing? _thinkingRing;
    private TextBlock? _thinkingText;

    /// <summary>
    /// 每秒刷新一次"已等 N 秒"。
    /// ⛔ 必须存成字段：局部变量建出来的计时器会被 GC 收走，收走之后不再 tick。
    /// </summary>
    private DispatcherTimer? _thinkingTimer;

    /// <summary>开始等待的时刻（<see cref="Environment.TickCount64"/>，毫秒）。</summary>
    private long _thinkingSince;

    /// <summary>
    /// 当前页面的现场数据生成器，由宿主页面在构造时挂上。
    /// <para>
    /// 每次发消息时**现采**一份结构化快照（页面名、角色、统计、清单、可执行操作），
    /// 不能像原来那样在数据就绪时算一次存着 —— 筛选、勾选、扫描进度随时在变。
    /// </para>
    /// </summary>
    public Func<PageContextDoc?>? ContextProvider { get; set; }

    public HubAiFlyout()
    {
        InitializeComponent();

        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);
        Card.RenderTransform = group;

        ApplySurface();
        ActualThemeVariantChanged += (_, _) => ApplySurface();
    }

    /// <summary>浮层当前是否展开（页面想在切走时收起，可以问它）。</summary>
    public bool IsOpen => Card.IsVisible;

    /// <summary>展开，并把焦点给输入框 —— 用户点进来的目的就是打字。</summary>
    public void Show()
    {
        if (IsOpen)
        {
            Input.Focus();
            return;
        }

        BeginConversation();

        _scale.ScaleX = 0.96;
        _scale.ScaleY = 0.96;
        _translate.Y = 8;
        Card.Opacity = 0;
        Card.IsVisible = true;

        _ = PlayAppearAsync(show: true);
        Input.Focus();
    }

    /// <summary>收起。动画结束后才真正隐藏（过早设会让动画直接消失）。</summary>
    public void Hide()
    {
        if (!IsOpen) return;

        _ = PlayAppearAsync(show: false, onDone: () => Card.IsVisible = false);
    }

    /// <summary>展开 / 收起切换（按钮点一下用这个）。</summary>
    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    // ── 按钮 ─────────────────────────────────────────────────────────────

    private void Close_Click(object? sender, RoutedEventArgs e) => Hide();

    /// <summary>升到完整页：那里能看到这次对话的思考过程、工具轨迹、推荐卡与用量。</summary>
    private void Expand_Click(object? sender, RoutedEventArgs e)
    {
        Hide();
        App.MainWindow?.Shell.NavigateTo("hubai");
    }

    private void Send_Click(object? sender, RoutedEventArgs e)
    {
        // 生成中这颗按钮就是「停止」
        if (_busy)
        {
            Stop();
            return;
        }

        _ = AskAsync();
    }

    private void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (_busy) return;
        _ = AskAsync();
    }

    private void Stop()
    {
        try { _runtime?.Stop(); } catch { }
    }

    // ── 运行时 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 开一段新对话：新的草稿运行时 + 清空气泡与上下文标记。
    /// ⚠️ 上一段还在生成就先停掉 —— 取消也会走收尾存档，已经写出来的部分不会白跑。
    /// </summary>
    private void BeginConversation()
    {
        if (_runtime is not null)
        {
            if (_runtime.IsRunning) _runtime.Stop();
            _runtime.Changed -= OnRuntimeChanged;
        }

        _runtime = HubAiRuntime.NewDraft();
        _runtime.Changed += OnRuntimeChanged;

        _sessionId = null;
        _answerPanel = null;
        _answerRendered = "";

        HideThinking();
        ShowStatusLine(null);

        Messages.Children.Clear();
        Intro.IsVisible = true;

        _busy = false;
        SetBusyUi(false);
    }

    /// <summary>运行时内容变了（增量正文）→ 刷新当前气泡。</summary>
    private void OnRuntimeChanged() => Dispatch(RenderLive);

    /// <summary>
    /// 把运行时的正文画进气泡。
    /// <para>
    /// ① 正文一到就撤掉「正在思考」气泡；
    /// ② 有正文但又在等工具时，阶段说明改到底部那行小字上（两处不会同时出现）。
    /// </para>
    /// </summary>
    private void RenderLive()
    {
        if (_runtime is null) return;

        var body = _runtime.LiveBody;

        if (body.Length > 0) HideThinking();
        else if (_busy) ShowThinking();          // 幂等：还空着就一直挂着

        ShowStatusLine(body.Length > 0 ? _runtime.LiveStatus : null);

        if (_answerPanel is null || body == _answerRendered) return;

        _answerRendered = body;
        MarkdownText.Apply(_answerPanel, body, ThemeBrush.Get(this, "TextFillColorPrimaryBrush"),
            fontSize: 12.5, lineHeight: 20);
        ScrollToEnd();
    }

    // ── 一轮问答 ─────────────────────────────────────────────────────────

    private async Task AskAsync()
    {
        if (_busy) return;

        var text = Input.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // 配置先查：没模型 / 配置不全时给一条明路，别让用户盯着空白
        var model = AiModelStore.Active;
        if (model is null)
        {
            AddBubble("还没有添加模型。请到「设置 → Hub AI → 模型服务」添加一个。", mine: false);
            return;
        }

        var cfg = model.ToConfig(SecretStore.Load(model.EffectiveSecretId));
        if (!cfg.IsUsable)
        {
            AddBubble("当前模型的配置不完整。请到「设置 → Hub AI → 模型服务」填写服务地址与模型名称。", mine: false);
            return;
        }

        // 理论上 Show() 里已经建好了；万一宿主直接调 Ask，补一份
        if (_runtime is null)
        {
            _runtime = HubAiRuntime.NewDraft();
            _runtime.Changed += OnRuntimeChanged;
        }

        var runtime = _runtime;
        if (runtime.IsRunning)
        {
            AddBubble("上一条还在回答，等它说完再问。", mine: false);
            return;
        }

        var sessionId = _sessionId;
        var before = runtime.Chat.Snapshot().Count;

        // 现场数据现采一份（页面此刻的样子）。失败就当没有，别挡住提问。
        var contextJson = BuildContextJson();

        Input.Text = string.Empty;
        Intro.IsVisible = false;

        AddBubble(text, mine: true);
        _answerPanel = AddBubble(string.Empty, mine: false);
        _answerRendered = "";

        _busy = true;
        SetBusyUi(true);

        // 从这里到第一个字回来之间可能是几十秒（读本机概况 + 等模型首包），
        // 期间界面上原本什么都没有，看着就像卡死了 —— 先挂上「正在思考」
        ShowThinking();

        try
        {
            // 每次现采一次本机概况：缓存下来的隔一会就不准，这一步也只要几百毫秒
            var snapshot = await MachineProbe.CollectAsync();

            runtime.Start(
                cfg,
                snapshot,
                ActiveTools.Load(),
                text,
                onCompleted: usage => Dispatch(() => Finish(runtime, sessionId, before, usage, error: null)),
                onFailed: ex => Dispatch(() => Finish(runtime, sessionId, before, AiUsage.Empty, error: ex)),
                pageContext: contextJson);
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "浮层提问失败：" + ex.Message);
            HideThinking();
            ShowStatusLine(null);
            AddBubble("出错了：" + ex.Message, mine: false);
            _busy = false;
            SetBusyUi(false);
        }
    }

    /// <summary>
    /// 一轮收尾：存档 + 复位界面。
    /// <para>
    /// ⚠️ 存档逻辑与 <c>HubAiPage.PersistSession</c> 是同一套判据：
    /// 只存「有正文的助手回合」，否则历史里会留下"只有思考、正文空白"的完成气泡。
    /// </para>
    /// </summary>
    private void Finish(HubAiRuntime runtime, string? sessionId, int before, AiUsage usage, Exception? error)
    {
        // ⚠️ 这段可能在浮层已经收起、甚至已经另起一段对话之后才回来（取消上一段就是要走这条路）。
        //    界面状态只归"当前这一段"管，存档照旧 —— 否则旧回合会把新对话的气泡/忙碌态搅乱。
        var current = ReferenceEquals(runtime, _runtime);

        if (current)
        {
            _busy = false;
            SetBusyUi(false);
            HideThinking();
            ShowStatusLine(null);
        }

        if (error is not null)
        {
            AppLog.Warn("hubai", "浮层对话失败：" + error.Message);
            if (current) AddBubble("出错了：" + error.Message, mine: false);
            return;
        }

        try
        {
            var turns = runtime.Chat.Snapshot().Skip(before).ToList();
            if (turns.Count == 0) return;

            if (!turns.Any(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)))
            {
                // 一句正文都没回来（只出了思考 / 工具调用）：给个提示，别留空气泡
                if (current && _answerPanel is { Children.Count: 0 })
                    AddBubble("这一轮没有文字回答，可以换个问法。", mine: false);
                return;
            }

            var title = turns.FirstOrDefault(m => m.Role == "user")?.Content ?? "未命名对话";

            var savedId = ChatSessionStore.SaveBranch(
                sessionId,
                parentId: null,
                turns,
                title,
                App.Settings.Current.HubAiModel,
                usage.TotalTokens);

            // 新对话第一次存档才拿到 id —— 运行时要从草稿键改挂到真实 id 上，
            // 这样跟到 Hub AI 页时是同一条（上下文接着聊，不是重新 Restore 一份）
            if (string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(savedId))
                HubAiRuntime.Adopt(runtime.SessionId, savedId);

            if (savedId is { Length: > 0 })
            {
                // 这一段往后就挂在保存下来的 id 上继续追加
                if (current) _sessionId = savedId;

                App.Settings.Current.HubAiLastSessionId = savedId;
                App.Settings.Save();

                // 让 Hub AI 页跳过来时直接切到这条会话（思考过程、工具轨迹都在那儿）
                Pages.HubAiPage.PendingReloadId = savedId;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "浮层存档失败：" + ex.Message);
        }
    }

    // ── 界面 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 现采这一页的现场数据，转成 JSON。
    /// 任何异常都吞掉返回 null：数据没生成出来只是"AI 少知道点情况"。
    /// </summary>
    private string? BuildContextJson()
    {
        if (ContextProvider is null) return null;

        try
        {
            var doc = ContextProvider();
            if (doc is null) return null;

            var json = doc.ToJson();
            AppLog.Step("hubai", $"页面现场数据 {doc.Page}：{json.Length} 字符");
            return json;
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "页面现场数据生成失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>加一条气泡。<paramref name="mine"/> = 用户自己说的（右对齐）。</summary>
    private StackPanel AddBubble(string text, bool mine)
    {
        var body = new StackPanel { Spacing = 6 };
        var foreground = mine
            ? ThemeBrush.Get(this, "TextOnAccentFillColorPrimaryBrush")
            : ThemeBrush.Get(this, "TextFillColorPrimaryBrush");

        if (text.Length > 0)
            MarkdownText.Apply(body, text, foreground, fontSize: 12.5, lineHeight: 20);

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            MaxWidth = 310,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = mine
                ? ThemeBrush.Get(this, "AccentFillColorDefaultBrush")
                : ThemeBrush.Get(this, "SubtleFillColorSecondaryBrush"),
            Child = body,
        };

        Messages.Children.Add(bubble);
        ScrollToEnd();
        return body;
    }

    private void ScrollToEnd()
    {
        try { MessagesScroll.ScrollToEnd(); }
        catch { }
    }

    /// <summary>生成中：发送键变「停止」。</summary>
    private void SetBusyUi(bool busy)
    {
        SendGlyph.Glyph = busy ? "\uE71A" : "\uE724";       // ■ 停止 / ➤ 发送
        ToolTip.SetTip(SendButton, busy ? "停止生成" : "发送");
    }

    // ── 「正在思考」 ───────────────────────────────────────────────────────

    /// <summary>
    /// 挂上「正在思考 · N 秒」气泡（幂等，已经在就不重复建）。
    /// 用 <see cref="ProgressRing"/> —— 转圈本身就是"它还活着"的证据，再加上秒数，
    /// 用户能判断是等一会儿还是按停止。
    /// </summary>
    private void ShowThinking()
    {
        if (_thinkingBubble is not null)
        {
            UpdateThinkingText();
            return;
        }

        _thinkingRing = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = true,
            IsIndeterminate = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _thinkingText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush.Get(this, "TextFillColorSecondaryBrush"),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(_thinkingRing);
        row.Children.Add(_thinkingText);

        _thinkingBubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeBrush.Get(this, "SubtleFillColorSecondaryBrush"),
            Child = row,
        };

        Messages.Children.Add(_thinkingBubble);

        _thinkingSince = Environment.TickCount64;
        UpdateThinkingText();
        StartThinkingTimer();
        ScrollToEnd();
    }

    /// <summary>撤掉「正在思考」气泡并停表。</summary>
    private void HideThinking()
    {
        try { _thinkingTimer?.Stop(); } catch { }

        if (_thinkingBubble is not null)
        {
            Messages.Children.Remove(_thinkingBubble);
            _thinkingBubble = null;
        }

        _thinkingRing = null;
        _thinkingText = null;
    }

    /// <summary>
    /// 每秒刷新一次文案。优先用运行时的阶段说明（那比笼统的"思考"更能解释为什么慢）。
    /// </summary>
    private void UpdateThinkingText()
    {
        if (_thinkingText is null) return;

        var status = _runtime?.LiveStatus;
        if (string.IsNullOrWhiteSpace(status) || status == "思考中") status = "正在思考";

        // 补一个省略号，读起来是"进行中"而不是一个静止的标签
        if (!status.EndsWith('…') && !status.EndsWith('.')) status += "…";

        var seconds = (int)((Environment.TickCount64 - _thinkingSince) / 1000);
        _thinkingText.Text = seconds >= 1 ? $"{status} {seconds} 秒" : status;
    }

    private void StartThinkingTimer()
    {
        _thinkingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        // ⛔ 先减再加：重复调用 ShowThinking 时不能挂上两个处理函数
        _thinkingTimer.Tick -= OnThinkingTick;
        _thinkingTimer.Tick += OnThinkingTick;
        _thinkingTimer.Start();
    }

    private void OnThinkingTick(object? sender, EventArgs e) => UpdateThinkingText();

    /// <summary>
    /// 底部那行阶段小字（有正文、又在等工具时才用）。
    /// 与「正在思考」气泡**互斥**：空正文时气泡说话，已有正文时这行说话。
    /// </summary>
    private void ShowStatusLine(string? status)
    {
        var text = status ?? string.Empty;

        if (StatusLine.Text != text) StatusLine.Text = text;
        StatusLine.IsVisible = text.Length > 0;
    }

    /// <summary>切回 UI 线程执行（运行时回调来自后台线程）。</summary>
    private void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ── 动画 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 出场 / 收起：轻微放大（0.96→1）+ 8px 归位 + 淡入，200ms、EaseOut；
    /// 收起 130ms、EaseIn。
    /// ⚠️ 手写插值而不是 <c>Animation.RunAsync(变换对象)</c>：直接驱动 RenderTransform
    ///    在本项目里出现过不生效/抢属性的问题（与 OobeMotion 同一套写法）。
    /// </summary>
    private async Task PlayAppearAsync(bool show, Action? onDone = null)
    {
        var version = ++_appearVersion;
        var durationMs = show ? 200d : 130d;
        var ease = show ? (Easing)new CubicEaseOut() : new CubicEaseIn();

        var fromScale = show ? _scale.ScaleX : 1d;
        var toScale = show ? 1d : 0.98d;
        var fromY = show ? _translate.Y : 0d;
        var toY = show ? 0d : 6d;
        var fromOpacity = show ? 0d : Card.Opacity;
        var toOpacity = show ? 1d : 0d;

        var watch = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            if (version != _appearVersion) return;

            var progress = Math.Min(1d, watch.Elapsed.TotalMilliseconds / durationMs);
            var eased = ease.Ease(progress);

            _scale.ScaleX = fromScale + (toScale - fromScale) * eased;
            _scale.ScaleY = _scale.ScaleX;
            _translate.Y = fromY + (toY - fromY) * eased;
            Card.Opacity = fromOpacity + (toOpacity - fromOpacity) * eased;

            if (progress >= 1d) break;
            await Task.Delay(16);
        }

        onDone?.Invoke();
    }

    /// <summary>
    /// 深色下的表面处理：背景用原生窗口底色，外面描一圈**主题色**边框。
    /// ⚠️ 浅色**不动** —— 浅色观感已确认，无需处理。
    /// </summary>
    private void ApplySurface()
    {
        try
        {
            if (ThemeBrush.IsDark(this))
            {
                Card.Background = ThemeBrush.Get(this, "SolidBackgroundFillColorBaseBrush");
                Card.BorderBrush = ThemeBrush.Get(this, "AccentFillColorDefaultBrush");
            }
            else
            {
                Card.Background = ThemeBrush.Get(this, "HubAiPanelBackground");
                Card.BorderBrush = ThemeBrush.Get(this, "HubAiPanelBorder");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("hubai", "浮层配色设置失败：" + ex.Message);
        }
    }
}
