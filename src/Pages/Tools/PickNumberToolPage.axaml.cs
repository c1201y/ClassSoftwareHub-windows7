using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 随机抽号（对齐网页版 tools/PickNumberTool.vue）：
/// 加密随机抽号（Fisher–Yates）+ 滚动动画 + 不重复记录（本地存档）+ 公平性自检（2 万次直方图 + 卡方）+ 随机分组。
/// 2026-09-27（Nick）：抽取对象可以是号码范围，也可以是导入的班级名单（Excel / txt）。
/// 2026-09-29（Nick）：名单要能预览、能就地改名、能导出回 Excel / txt —— 见 Views\RosterEditorDialog。
/// </summary>
public sealed partial class PickNumberToolPage : PageBase
{
    private const int RollTicks = 16;
    private const int FairTotal = 20000;

    private static readonly string[] GroupModeItems = { "按组数（分成 N 组）", "按人数（每组 N 人）" };

    private readonly DispatcherTimer _rollTimer;
    private readonly List<int> _used = new();
    private List<int> _pending = new();
    private int _ticks;
    private bool _ready;
    private bool _updating;   // RefreshHints 正在改控件（改 Maximum 会连带触发 ValueChanged，得挡掉）

    // ── 名单模式（2026-09-27 Nick 提）──
    private readonly List<string> _roster = new();
    private readonly List<string> _usedNames = new();
    private List<string> _pendingNames = new();
    private string _rosterSource = "";   // 导入时的文件名，只用来显示
    private int _savedMode;              // 从存档读出来的模式，铺完控件才生效
    private string _groupText = "";      // 最近一次分组结果的纯文本（给「复制结果」用）

    private double _nameScale = 1.0;     // 结果字号的缩放系数（2026-10-06 滑块可调；2026-10-07 上限提到 3.0，号码也吃它）
    private List<string>? _lastNames;     // 最近一次（非滚动中）展示的名单，滑块改字号时重绘用
    private List<int>? _lastNumbers;     // 同上，号码模式（2026-10-07：号码也吃缩放）

    // ── 滚动性能（2026-10-09，真机反馈"抽号偶尔卡一下"）──
    // 原实现每个 tick 都把 ResultHost 清空重建：TextBlock/Grid 全新分配 + 视觉树/逻辑树重挂 +
    // 样式重新应用，16 个 tick 连续重建大字号结果树，是卡顿的主要来源。
    // 现在结构不变时复用既有 TextBlock，每 tick 只改 Text / FontSize / Opacity；
    // 结构（名单 / 号码、单抽 / 多抽、个数、单抽可用宽）变化才整组重建。
    private readonly List<TextBlock> _resultBlocks = new();
    private string? _resultLayoutKey;    // 当前结果区的结构签名

    /// <summary>
    /// 滚动动画专用的轻量随机数：每个 tick 只负责"看起来在滚"，不产生结果，
    /// 没必要走加密随机。最终抽中的结果与公平性自检仍然全部出自加密随机（<see cref="RandInt"/>）。
    /// </summary>
    private static readonly Random RollRandom = new();

    public PickNumberToolPage()
    {
        InitializeComponent();

        // 原版 DispatcherQueue.CreateTimer() + IsRepeating=true → DispatcherTimer 天生重复
        _rollTimer = new DispatcherTimer();
        _rollTimer.Interval = TimeSpan.FromMilliseconds(65);
        _rollTimer.Tick += (_, _) => RollTick();

        // 离开页面把抽号滚动停掉
        Unloaded += (_, _) => _rollTimer.Stop();

        foreach (var item in GroupModeItems) GroupModeBox.Items.Add(item);
        GroupModeBox.SelectedIndex = 0;

        LoadConfig();

        // 抽取对象：两条 Checked 已经挂在 XAML 上（PoolMode_Changed），这里只按存档恢复选中。
        // ⚠️ IsChecked 不能在 XAML 里写 True —— 见 App.xaml 里 CshModeCardStyle 的注释。
        if (_savedMode == 1) ModeRosterRadio.IsChecked = true;
        else ModeRangeRadio.IsChecked = true;

        FromBox.ValueChanged += (_, _) => OnSettingChanged();
        ToBox.ValueChanged += (_, _) => OnSettingChanged();
        CountBox.ValueChanged += (_, _) => OnSettingChanged();
        NoRepeatBox.Checked += (_, _) => OnSettingChanged();
        NoRepeatBox.Unchecked += (_, _) => OnSettingChanged();

        _ready = true;
        SyncPoolMode();
        RefreshHints();

        // 功能：抽号 / 随机分组 —— 一次只面对一件事（两件事共用上面的抽取对象设置）
        TaskDrawRadio.IsChecked = true;
        SyncTask();

        // 分组方式一换，「组数」这个标题就得跟着换（按人数时它其实叫「每组人数」）
        GroupModeBox.SelectionChanged += (_, _) => SyncGroupModeHeader();
        SyncGroupModeHeader();
    }


    /// <summary>「按组数」/「按人数」切换时，同步旁边那个数字框的标题与提示。</summary>
    private void SyncGroupModeHeader()
    {
        GroupValueBox.Header = GroupModeBox.SelectedIndex <= 0 ? "组数" : "每组人数";
        RefreshGroupRangeHint();
    }

    /// <summary>分组页那一行小字：说清"要分的到底是哪个池子、有多少人/号"。</summary>
    private void RefreshGroupRangeHint()
    {
        if (UseRoster)
        {
            var total = _roster.Count;
            GroupRangeHint.Text = total > 0
                ? $"抽取池：导入的名单，共 {total} 人。"
                : "抽取池：尚未导入名单，请在上方导入。";
            return;
        }

        GroupRangeHint.Text = PoolSize > 0
            ? $"抽取池：{Lo} ~ {Hi}，共 {PoolSize} 个号码。"
            : "抽取池：请先在上方填写有效的号码范围。";
    }

    /// <summary>把这个工具丢到工具浮窗里跑（浮窗和这一页共用同一个抽号存档）。</summary>
    private void OpenPalette_Click(object? sender, RoutedEventArgs e)
        => Views.ToolPaletteWindow.ShowTool("pick-number");

    // ══════════ 功能切换：抽号 / 随机分组 ══════════
    private void TaskMode_Changed(object? sender, RoutedEventArgs e) => SyncTask();

    /// <summary>按「功能」卡片切左右两块视图。两块共用上面的「抽取对象」设置。</summary>
    private void SyncTask()
    {
        var draw = TaskDrawRadio.IsChecked == true;
        DrawView.IsVisible = draw;
        GroupView.IsVisible = !draw;

        // ⚠️ 只在已经挂进视觉树之后才 ChangeView：构造函数里控件还没进树，
        //    那时调等于"在布局过程中请求滚动"，原 WinUI 会判成 Layout cycle。
        var view = draw ? DrawView : GroupView;
        // ⚠️ 原版 ChangeView(0, 0, null) → Avalonia 没有，直接写 Offset（SettingsPage 同款写法）
        if (view.IsLoaded) view.Offset = new Vector(0, 0);
    }

    // ══════════ 抽取对象：号码范围 / 名单 ══════════
    private bool UseRoster => ModeRosterRadio.IsChecked == true;

    private void PoolMode_Changed(object? sender, RoutedEventArgs e) => OnPoolModeChanged();

    private void OnPoolModeChanged()
    {
        if (!_ready) return;
        SyncPoolMode();
        SaveConfig();
        RefreshHints();
    }

    /// <summary>按当前模式收起 / 放出参数行里的那一组输入控件。</summary>
    private void SyncPoolMode()
    {
        var roster = UseRoster;
        RangePanel.IsVisible = !roster;
        RosterPanel.IsVisible = roster;

        // ⚠️ 2026-10-07：切了抽取对象就把另一模式的"上次结果"清掉 ——
        //    否则拖「结果字号」滑块时，ShowNames / ShowResult 会把**两种**结果都重绘出来，
        //    结果区里会同时出现号码和名字（而且那个号码早就不是当前模式抽的了）。
        if (roster) _lastNumbers = null;
        else _lastNames = null;

        // 「抽取数量 / 不重复」两种模式共用（上游 dv1.1.1）：旧版它俩锁在「号码」那一组里，
        // 名单模式一整组被收起来，于是名单模式压根没有"抽几个"（2026-10-05 反馈）。
        // 两种模式的摆位不同：
        //   · 号码模式：并到第一行右边 ⇒ 整条参数带仍是一行，与旧版一致；
        //   · 名单模式：自己占第二行 ⇒ 名单那一排（标签 + 四个按钮）本来就长，
        //     同一行再塞数量框，窗口一窄就会被裁掉。见 ParamGrid 上的注释。
        //   上游靠 Grid 的 ColumnSpacing="22" 拉开间距，Avalonia 这边用 Margin 模拟。
        Grid.SetRow(BatchPanel, roster ? 1 : 0);
        Grid.SetColumn(BatchPanel, roster ? 0 : 1);
        BatchPanel.Margin = roster ? new Thickness(0, 8, 0, 0) : new Thickness(22, 0, 0, 0);

        // 2026-09-29：勾选框挤在控制行里，文案只能短 —— "抽过的名字不再出现"这种补充说明挪到悬停提示
        ToolTip.SetTip(NoRepeatBox, roster ? "抽过的名字不再出现" : "抽过的不再出现");
        RefreshRosterText();
    }

    /// <summary>名单相关按钮的可用性（名单为空时"查看 / 编辑""清空""导出"都没意义）。</summary>
    private void RefreshRosterText()
    {
        var has = _roster.Count > 0;
        ClearRosterButton.IsEnabled = has;
        OpenRosterButton.IsEnabled = has;
    }

    /// <summary>
    /// 打开「查看 / 编辑名单」弹窗，回来时把改动收下。
    ///
    /// 弹窗里是"当场改、当场删"的语义，所以这里只做两件事：把结果写回抽取池、把界面刷一遍。
    /// ⚠️ 名单变了要顺手清掉「抽号记录」—— 记录里存的是**旧名字**，
    ///    留着会让"不重复"认不出改名后的人（同一个人被再抽一次）。
    /// </summary>
    private async void OpenRoster_Click(object? sender, RoutedEventArgs e)
    {
        HideError();
        var dialog = new Views.RosterEditorDialog(_roster, _rosterSource);
        await dialog.ShowAsync(GetTopLevel());

        var updated = dialog.Result;
        var same = updated.Count == _roster.Count && !updated.Where((n, i) => n != _roster[i]).Any();
        if (same) return;

        _roster.Clear();
        _roster.AddRange(updated);
        _usedNames.Clear();

        SaveConfig();
        RefreshRosterText();
        RefreshHints();
        Toast.Text = $"名单已更新，共 {_roster.Count} 人（抽号记录已重置）。";
    }

    // ══════════ 设置 ══════════
    private int From => double.IsNaN(FromBox.Value) ? 0 : (int)Math.Floor(FromBox.Value);
    private int To => double.IsNaN(ToBox.Value) ? 0 : (int)Math.Floor(ToBox.Value);
    private int Lo => Math.Min(From, To);
    private int Hi => Math.Max(From, To);
    /// <summary>号码范围上限（跟浮窗版一致）。一万个号足够任何班级场景，再大就是误输入了。</summary>
    private const int MaxRange = 9999;

    /// <summary>
    /// 池子大小 —— **已经夹到合法区间**，所以任何调用点都拿不到一个荒谬的数。
    ///
    /// ⚠️ 为什么必须夹：原来这里直接写 `Hi - Lo + 1`。
    ///   · 极端输入下（如 To = 2000000000）这个减法会在 int 上溢出变负；
    ///   · 老师在数字框里粘一个 100000000，`Enumerable.Range(Lo, size).ToList()` 会立刻申请
    ///     数亿个 int（几 GB）→ 先是界面冻死，再大一点直接 OutOfMemoryException。
    /// 这里用 long 算再夹到 MaxRange，从根上保证"绝不分配巨型列表"。
    /// 至于"要告诉老师范围填太大了"，由 <see cref="TryUseRange"/> 负责，不靠这里静默夹取。
    /// </summary>
    private int PoolSize
    {
        get
        {
            var raw = (long)Hi - Lo + 1;
            if (raw <= 0) return 0;
            return (int)Math.Min(raw, MaxRange);
        }
    }

    /// <summary>范围是否合法可用；不合法时给出能直接显示给老师的原因。</summary>
    private bool TryUseRange(out int size, out string error)
    {
        var raw = (long)Hi - Lo + 1;
        if (raw <= 1)
        {
            size = 0;
            error = "请填写有效的号码范围（如 1 ~ 50）";
            return false;
        }

        if (raw > MaxRange)
        {
            size = 0;
            error = $"号码范围过大（{raw:N0} 个），本工具上限为 {MaxRange}。请检查起止数字是否输入有误。";
            return false;
        }

        size = (int)raw;
        error = "";
        return true;
    }

    private int WantCount => double.IsNaN(CountBox.Value) ? 1 : Math.Max(1, (int)Math.Floor(CountBox.Value));
    private bool NoRepeat => NoRepeatBox.IsChecked == true;

    private void OnSettingChanged()
    {
        // ⚠️ _updating：RefreshHints 改 CountBox.Maximum 时会把超标的 Value 夹回来，
        //    那一下也会走到这里 —— 那不是"用户改了设置"，别再存一遍、也别再刷一次提示（会形成回环）。
        if (!_ready || _updating) return;
        SaveConfig();
        RefreshHints();
    }

    private int UsedInRange => _used.Where(n => n >= Lo && n <= Hi).Distinct().Count();

    private void RefreshHints()
    {
        // ⚠️ 下面第一件事就是改 CountBox.Maximum —— 而改上限会把超标的 Value 夹进来、
        //    触发一次 ValueChanged；用 _updating 挡掉，免得被当成"用户改了设置"再存一遍
        //    （上游 dv1.1.1 同款写法）。
        _updating = true;
        try { RefreshHintsCore(); }
        finally { _updating = false; }
    }

    /// <summary>
    /// 提示 + 「抽取数量」的上限。
    ///
    /// ⚠️ 上限按当前池子走（与浮窗版 MiniPickNumber 同一套规则）：名单模式跟人数、号码模式跟号码范围 ——
    ///    名单只有 10 人时，框里就不该填得出 11。旧版这个框的 Maximum 在 XAML 里写死 50，
    ///    名单超过 50 人的班就一次抽不完（2026-10-05 顺带修掉）。
    /// </summary>
    private void RefreshHintsCore()
    {
        CountBox.Maximum = Math.Max(1, UseRoster ? _roster.Count : PoolSize);

        if (UseRoster) { RefreshRosterHints(); return; }

        SummaryText.Text = $"本次设置：从 {Lo} ~ {Hi} 中抽取 {WantCount} 个号" + (NoRepeat ? "，抽过的不再出现" : "");

        // 参数行右侧那句状态 —— 就在起止框旁边，不用去别处找
        PoolStatusText.Text = PoolSize > 0 ? PoolStatus(PoolSize, UsedInRange, "个号") : "请填写有效的号码范围（如 1 ~ 50）";

        UsedRow.IsVisible = _used.Count > 0;
        UsedText.Text = _used.Count > 0 ? $"已抽取 {_used.Count} 个：{Head(_used.Select(n => n.ToString()))}" : "";
        RefreshGroupRangeHint();
    }

    private void RefreshRosterHints()
    {
        var total = _roster.Count;
        var used = _usedNames.Count;

        SummaryText.Text = $"本次设置：从名单中抽取 {WantCount} 人" + (NoRepeat ? "，抽过的不再出现" : "");
        PoolStatusText.Text = total == 0
            ? "名单是空的 · 可先单击「示例名单」看一份现成的"
            : PoolStatus(total, used, "人");

        UsedRow.IsVisible = used > 0;
        UsedText.Text = used > 0 ? $"已抽取 {used} 人：{Head(_usedNames)}" : "";
        RefreshGroupRangeHint();
    }

    /// <summary>
    /// 参数行右侧那句状态：池子多大、抽掉多少、还剩多少。
    /// 抽完时直接把"怎么重来"写在这儿 —— 用户看到"剩 0 个"的第一反应就是找按钮。
    /// </summary>
    private string PoolStatus(int size, int used, string unit)
    {
        if (!NoRepeat) return $"共 {size} {unit}";
        var remain = Math.Max(0, size - used);
        return remain == 0
            ? $"共 {size} {unit} · 已抽完，如需重新抽取请单击「重置记录」"
            : $"共 {size} {unit} · 已抽 {used} · 剩 {remain}";
    }

    /// <summary>记录行只列前 12 个，剩下的用省略号 —— 五十几个名字铺满两行反而看不清。</summary>
    private static string Head(IEnumerable<string> items)
    {
        var list = items.ToList();
        var shown = string.Join("、", list.Take(12));
        return list.Count > 12 ? shown + $" 等 {list.Count} 个" : shown;
    }

    // ══════════ 抽号 ══════════
    private static int RandInt(int n) => n <= 1 ? 0 : RandomNumberGenerator.GetInt32(n);

    private List<int>? PickOnce(int k)
    {
        var size = PoolSize;
        if (size <= 0 || k > size) return null;

        List<int> available;
        if (NoRepeat)
        {
            var usedSet = new HashSet<int>(_used);
            available = Enumerable.Range(Lo, size).Where(n => !usedSet.Contains(n)).ToList();
            if (available.Count < k) return null;
        }
        else
        {
            available = Enumerable.Range(Lo, size).ToList();
        }

        for (var i = available.Count - 1; i > 0; i--)
        {
            var j = RandInt(i + 1);
            (available[i], available[j]) = (available[j], available[i]);
        }
        return available.Take(k).ToList();
    }

    private void Draw_Click(object? sender, RoutedEventArgs e)
    {
        HideError();

        if (UseRoster) { DrawFromRoster(); return; }

        var k = WantCount;
        if (!TryUseRange(out var size, out var rangeError)) { ShowError(rangeError); return; }
        if (k > size) { ShowError($"单次最多抽取 {size} 个号"); return; }
        if (NoRepeat && UsedInRange + k > size)
        {
            ShowError("范围内号码不足，请单击「重置记录」后重新抽取");
            return;
        }

        var final = PickOnce(k);
        if (final is null) { ShowError("抽号失败，请检查范围与数量"); return; }

        _pending = final;
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽号中";
        _rollTimer.Start();
    }

    private void DrawFromRoster()
    {
        var k = WantCount;
        var total = _roster.Count;
        if (total == 0) { ShowError("名单是空的，请单击上方的「导入名单」；不清楚格式可先单击「示例名单」。"); return; }
        if (k > total) { ShowError($"名单共 {total} 人，单次最多抽取 {total} 个"); return; }

        List<string> available;
        if (NoRepeat)
        {
            var usedSet = new HashSet<string>(_usedNames, StringComparer.OrdinalIgnoreCase);
            available = _roster.Where(n => !usedSet.Contains(n)).ToList();
            if (available.Count < k)
            {
                ShowError("名单中未抽取的人数不足，请单击「重置记录」后重新抽取");
                return;
            }
        }
        else
        {
            available = _roster.ToList();
        }

        Shuffle(available);
        _pendingNames = available.Take(k).ToList();
        _ticks = 0;
        DrawButton.IsEnabled = false;
        DrawButton.Content = "抽号中";
        _rollTimer.Start();
    }

    /// <summary>Fisher–Yates（和号码模式同一套洗牌，保证公平性一致）。</summary>
    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ── 结果格尺寸：原版由 VariableSizedWrapGrid 的 ItemWidth/ItemHeight 定格 ──
    //
    // ⚠️ 2026-10-06（Nick 实机反馈「抽号字多显示不开」）：
    //    原来是「宽 150 × **高 92**」的硬格子 + 写死字号。号码只有 1~4 位数看不出问题，
    //    但名单模式抽到 4 个字以上的名字时，42px 的字折成两行就有 100+ 高 —— 超出的部分
    //    被格子直接裁掉，屏幕上看到的就是「字显示不开 / 半截字」。
    //    现在改成：
    //      · 宽度**钉住**（150 → 176），保证多个人并排时列宽整齐；
    //      · 高度只给 **MinHeight**（不再是 Height）—— 内容高就让它自己长，WrapPanel 里
    //        这一行跟着高，外层 ScrollViewer 负责滚动，**永远不裁字**；
    //      · 名字字号按字数收档（见 <see cref="NameFontSize"/>），长名字先折行、再收字号；
    //      · 号码字号对齐上游 dv1.1.0 的 72（结果区是这一页的主角）。
    // 2026-10-08（Nick）：100% 基准 72 → 96（"字号还是太小了"）；上限档显示不全改由
    //    <see cref="ShowResult"/> 的**自动收字号**根治（放不下就缩，绝不裁半截字）。
    private const double NumberCellWidth = 176;
    private const double NumberCellMinHeight = 120;
    private const double NumberBaseFontSize = 96;

    /// <summary>结果字号的行高估算系数（给"按高度收字号"用，宁可比真实行高略大也别裁字）。</summary>
    private const double LineHeightFactor = 1.4;

    /// <summary>
    /// 名单模式的名字字号：**按字数收档**。
    ///
    /// 目标：在 176 宽（可用 164）的格子里，最多折成两行就把人名字放下 ——
    /// 中文一个字约占 1 个字宽，所以「一行放得下几个字」≈ 164 / 字号。
    /// 三字名（最常见）保持 44 不缩；越长的名字字号越小，宁可字小也不要折成四五行。
    /// </summary>
    private static double NameFontSize(string name)
    {
        var len = name.Length;
        if (len <= 3) return 44;
        if (len <= 5) return 34;
        if (len <= 8) return 26;
        if (len <= 12) return 20;
        return 16;
    }

    private void RollTick()
    {
        _ticks++;
        var roster = UseRoster;

        if (_ticks >= RollTicks)
        {
            _rollTimer.Stop();
            DrawButton.IsEnabled = true;
            DrawButton.Content = "开始抽号";

            if (roster) ShowNames(_pendingNames, rolling: false);
            else ShowResult(_pending, rolling: false);

            if (NoRepeat)
            {
                if (roster) _usedNames.AddRange(_pendingNames);
                else _used.AddRange(_pending);
                SaveConfig();
                RefreshHints();
            }
            return;
        }

        // 滚动中：号码模式刷随机号，名单模式刷随机名字（轻量随机，结果与展示无关）
        if (roster)
        {
            var total = _roster.Count;
            ShowNames(Enumerable.Range(0, _pendingNames.Count).Select(_ => _roster[RollRandom.Next(total)]).ToList(), rolling: true);
        }
        else
        {
            ShowResult(Enumerable.Range(0, _pending.Count).Select(_ => Lo + RollRandom.Next(PoolSize)).ToList(), rolling: true);
        }
    }

    private void ShowResult(IReadOnlyList<int> numbers, bool rolling)
    {
        // ⚠️ 2026-10-08（Nick 截图：300% 时数字上半截被裁）：
        //    号码字号 = 基准 × 滑块，300% 就是 288px —— 塞在 176 宽的格子里必然超界，
        //    超界部分在不同容器上表现成"上半截被裁"。根治办法与名单模式一致：
        //      · 单抽 = 铺满整张结果卡（不进 176 小格子），宽、高放不下都自动收字号；
        //      · 多抽 = 仍是 176 格子，字号放不下就收 —— 收完也比旧版 100% 大得多。
        //    口径：**宁可字小一点，也绝不出半截字**。
        var single = numbers.Count == 1;
        var cardW = ResultCard.Bounds.Width;
        if (cardW <= 0) cardW = 460;              // 结果卡还没量过尺寸时的兜底
        var cardH = ResultCard.Bounds.Height;
        if (cardH <= 0) cardH = 320;

        EnsureResultBlocks(isNames: false, single: single, count: numbers.Count,
            singleAvailW: Math.Max(NumberCellWidth - 12, cardW - 40));

        for (var i = 0; i < numbers.Count; i++)
        {
            var text = numbers[i].ToString();
            var size = NumberBaseFontSize * _nameScale;

            if (single)
            {
                // 单抽：可用宽 = 结果卡内容宽（Padding 20 左右各一）；高同理（上下各留 20）
                var availW = Math.Max(NumberCellWidth - 12, cardW - 40);
                var availH = Math.Max(NumberCellMinHeight, cardH - 40);

                var est = EstimateTextWidth(text, size);
                if (est > availW) size = Math.Max(11, size * availW / est);
                if (size * LineHeightFactor > availH) size = Math.Max(11, availH / LineHeightFactor);
            }
            else
            {
                // 多抽：176 格子，宽（164 可用）放不下就收字号；行高再兜一道，别让一行字超出结果卡
                var availW = NumberCellWidth - 12;
                var est = EstimateTextWidth(text, size);
                if (est > availW) size = Math.Max(11, size * availW / est);
                if (size * LineHeightFactor > NumberCellMinHeight * 2)
                    size = Math.Max(11, NumberCellMinHeight * 2 / LineHeightFactor);
            }

            var tb = _resultBlocks[i];
            tb.Text = text;
            tb.FontSize = size;
            tb.Opacity = rolling ? 0.72 : 1;
        }
        ResultHint.IsVisible = numbers.Count == 0;
        if (!rolling) _lastNumbers = numbers.ToList();
    }

    /// <summary>
    /// 保证结果区里有一组与目标结构一致的控件（结构签名变了才整组重建，否则全部复用）。
    /// 每格一个 TextBlock：单抽 = 铺满结果卡的 Grid；多抽 = 176 宽的标准格（<see cref="Cell"/>）。
    /// 滚动动画期间结构与个数不变，因此整个滚动过程零新建、零 GC 压力。
    /// </summary>
    private void EnsureResultBlocks(bool isNames, bool single, int count, double singleAvailW)
    {
        var key = $"{isNames}|{single}|{count}|{(single ? (int)singleAvailW : 0)}";
        if (key == _resultLayoutKey) return;
        _resultLayoutKey = key;

        ResultHost.Children.Clear();
        _resultBlocks.Clear();

        for (var i = 0; i < count; i++)
        {
            var tb = new TextBlock
            {
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (isNames)
            {
                // 名字单行显示（折行是上一版被否掉的观感）：放不下按字数收字号，… 兜底
                tb.TextWrapping = TextWrapping.NoWrap;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                tb.MaxWidth = single ? singleAvailW : NumberCellWidth - 12;
            }
            else if (single)
            {
                tb.MaxWidth = singleAvailW;
            }

            if (single)
                ResultHost.Children.Add(new Grid { MinHeight = NumberCellMinHeight, Margin = new Thickness(4, 6, 4, 6), Children = { tb } });
            else
                ResultHost.Children.Add(Cell(tb));

            _resultBlocks.Add(tb);
        }
    }

    /// <summary>
    /// 名单模式的结果：名字比号码长 —— 字号按字数收档（<see cref="NameFontSize"/>），
    /// 太长就折行；格子只钉宽度、不钉高度，所以折几行都不会被裁。
    /// </summary>
    private void ShowNames(IReadOnlyList<string> names, bool rolling)
    {
        // ⚠️ 2026-10-07（Nick 实机反馈「抽号那么大的地方，还要用 … 显示不开」）：
        //    只抽 1 人时，别再把名字塞进 176 宽的小格子里 —— 那样结果卡里明明空着一大片，
        //    长一点的名字还是被 164 的 MaxWidth 截成「华风夏韵洛…」。
        //    单抽 = 名字横跨整个结果卡（居中、不折行），并且放不下就**自动收字号**，宁可小一点也要看全。
        var single = names.Count == 1;
        var cardW = ResultCard.Bounds.Width;
        if (cardW <= 0) cardW = 460;              // 结果卡还没量过尺寸时的兜底

        EnsureResultBlocks(isNames: true, single: single, count: names.Count,
            singleAvailW: Math.Max(NumberCellWidth - 12, cardW - 40));

        for (var i = 0; i < names.Count; i++)
        {
            // 单抽：可用宽度 = 结果卡内容宽（Padding 20 左右各一）；多抽：仍旧按 176 格子算
            var allowed = single ? Math.Max(NumberCellWidth - 12, cardW - 40)
                                 : NumberCellWidth - 12;

            var size = NameFontSize(names[i]) * _nameScale;
            var est = EstimateTextWidth(names[i], size);
            if (est > allowed) size = Math.Max(11, size * allowed / est);   // 放不下就收字号，不出现 …

            var tb = _resultBlocks[i];
            tb.Text = names[i];
            tb.FontSize = size;
            tb.Opacity = rolling ? 0.72 : 1;
        }

        ResultHint.IsVisible = names.Count == 0;
        if (!rolling) _lastNames = names.ToList();
    }

    /// <summary>粗略估一行文字的像素宽（中日韩字 ≈ 1 个字宽，其它 ≈ 0.56）—— 给「自动收字号」用。</summary>
    private static double EstimateTextWidth(string s, double fontSize)
    {
        double units = 0;
        foreach (var ch in s) units += ch > 0x2E7F ? 1.0 : 0.56;
        return units * fontSize;
    }

    /// <summary>
    /// 一个结果格：宽度钉死、高度只给下限。
    ///
    /// ⛔ 别把 <c>MinHeight</c> 改回 <c>Height</c> —— 那正是 2026-10-06「字显示不开」的成因：
    ///    写了 Height 就等于告诉布局「内容只能这么高」，多出来的字会被裁掉且不会报错。
    /// </summary>
    private static Grid Cell(Control content) => new Grid
    {
        Width = NumberCellWidth,
        MinHeight = NumberCellMinHeight,
        Margin = new Thickness(4, 6, 4, 6),
        Children = { content },
    };

    private void ResetUsed_Click(object? sender, RoutedEventArgs e)
    {
        _used.Clear();
        _usedNames.Clear();
        SaveConfig();
        RefreshHints();
        Toast.Text = "已重置抽号记录";
    }

    // ══════════ 公平性自检 ══════════
    /// <summary>
    /// 在本机实抽 2 万次，画一张分布直方图 + 卡方结论。
    ///
    /// 2026-09-29：从"设置栏里的折叠区"改成"链接 + 浮出面板"。
    /// 折叠区那一版实测会被挤到可视区下面（AutomationId 还在，但高度被裁成 1px）——
    /// 对一个从没看过说明书的用户来说，等于这个功能不存在。浮出面板贴在链接上弹，
    /// 点哪儿看哪儿，也不占设置栏的高度。
    /// </summary>
    private void FairCheck_Click(object? sender, RoutedEventArgs e)
    {
        var size = PoolSize;
        if (size <= 1) { Toast.Text = "请先填写有效的号码范围"; return; }

        var buckets = Math.Min(10, size);
        var counts = new int[buckets];
        for (var i = 0; i < FairTotal; i++)
        {
            var v = Lo + RandInt(size);
            var bi = Math.Min(buckets - 1, (int)Math.Floor((v - Lo) / (double)size * buckets));
            counts[bi]++;
        }

        var expect = FairTotal / (double)buckets;
        double chi = 0;
        foreach (var c in counts) chi += Math.Pow(c - expect, 2) / expect;
        var max = counts.Max();

        // 图是每次现搭的：桶数跟着号码范围走，没法预先钉在 XAML 上
        var chart = new Grid { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        for (var i = 0; i < buckets; i++)
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < buckets; i++)
        {
            var start = Lo + (int)Math.Round((double)i * size / buckets);
            var end = Lo + (int)Math.Round((double)(i + 1) * size / buckets) - 1;
            var label = start == end ? start.ToString() : $"{start}-{end}";
            var pct = max > 0 ? counts[i] / (double)max : 0;

            var col = new StackPanel { Spacing = 4, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
            col.Children.Add(new Rectangle
            {
                Height = Math.Max(2, pct * 78),
                RadiusX = 3,
                RadiusY = 3,
                Fill = AccentBrush(),
            });
            col.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Opacity = 0.6,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            ToolTip.SetTip(col, $"{label}：{counts[i]} 次");
            Grid.SetColumn(col, i);
            chart.Children.Add(col);
        }

        var normal = chi < 27.9;
        var note = new TextBlock
        {
            Text = $"实抽 {FairTotal:N0} 次，分成 {buckets} 格，每格理论约 {Math.Round(expect)} 次；" +
                   $"实际 {counts.Min()} ~ {counts.Max()} 次（卡方 {chi:0.0}，" +
                   (normal ? "分布正常" : "本次分布略有偏差，可再次抽取") + "）",
            FontSize = 12.5,
            Opacity = 0.75,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "在本机实抽 2 万次，检验号码分布是否均匀。",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 4),
            Height = 124,
            Background = Res("ControlFillColorDefaultBrush"),
            Child = chart,
        });
        panel.Children.Add(note);

        // 原版 MenuFlyout 家族的 Flyout → Avalonia 同名 Flyout（Placement: Top 同语义）
        new Flyout { Content = panel, Placement = PlacementMode.Top }.ShowAt(FairLink);
    }

    // ══════════ 随机分组 ══════════
    /// <summary>
    /// 随机分组：号码池与名单池共用上面的抽取池设置。
    ///
    /// ⚠️ 分组**不受**「不重复」记录影响，也不消耗它 —— 每次单击都是对整池独立洗牌，
    ///    所以可以反复分组、没有次数限制。用户最常问的就是"能分几次"，这句话直接写在页面上了。
    /// </summary>
    private void Group_Click(object? sender, RoutedEventArgs e)
    {
        Toast.Text = "";
        if (UseRoster) { GroupRoster(); return; }

        if (!TryUseRange(out var size, out var rangeError)) { Toast.Text = rangeError; return; }
        if (size < 2) { Toast.Text = "至少要 2 个号码才能分组。"; return; }

        var byGroups = GroupModeBox.SelectedIndex <= 0;
        var value = GroupValue;
        var nums = Enumerable.Range(Lo, size).ToList();
        Shuffle(nums);

        var groupCount = Math.Max(1, byGroups ? Math.Min(value, size) : (int)Math.Ceiling(size / (double)value));
        var groups = new List<List<string>>();
        for (var i = 0; i < groupCount; i++) groups.Add(new List<string>());
        for (var i = 0; i < nums.Count; i++) groups[i % groupCount].Add(nums[i].ToString());
        // 号是整数，得按数值排（直接按字符串排会把 "10" 排到 "9" 前面）
        foreach (var g in groups) g.Sort((a, b) => int.Parse(a).CompareTo(int.Parse(b)));

        RenderGroups(groups, "个号");
    }

    /// <summary>名单分组：把导入的名字随机分成 N 组（或每组 N 人）。组内按姓名排序，看起来才整齐。</summary>
    private void GroupRoster()
    {
        var total = _roster.Count;
        if (total == 0) { Toast.Text = "尚未导入名单，请单击「导入名单」。"; return; }
        if (total < 2) { Toast.Text = "名单里至少要有 2 个人才能分组。"; return; }

        var byGroups = GroupModeBox.SelectedIndex <= 0;
        var value = GroupValue;
        var names = _roster.ToList();
        Shuffle(names);

        var groupCount = Math.Max(1, byGroups ? Math.Min(value, total) : (int)Math.Ceiling(total / (double)value));

        var groups = new List<List<string>>();
        for (var g = 0; g < groupCount; g++)
        {
            // 轮流发牌：第 g 组拿 names[g]、names[g+groupCount]、…（与号码模式同一种发法）
            var members = new List<string>();
            for (var i = g; i < names.Count; i += groupCount) members.Add(names[i]);
            members.Sort(StringComparer.CurrentCulture);
            groups.Add(members);
        }

        RenderGroups(groups, "人");
    }

    /// <summary>「组数 / 每组人数」框里的那个数（夹到合法区间，免得在 double 上溢出）。</summary>
    private int GroupValue
        => double.IsNaN(GroupValueBox.Value) ? 1 : Math.Max(1, Math.Min(9999, (int)Math.Floor(GroupValueBox.Value)));

    /// <summary>
    /// 画分组结果，同时攒一份纯文本给「复制结果」——
    /// 分完组通常要发到班级群里，复制出来的就是「第 1 组（6 人）：张三、李四…」这种能直接粘贴的格式。
    /// </summary>
    private void RenderGroups(List<List<string>> groups, string unit)
    {
        GroupHint.IsVisible = false;
        GroupsHost.Children.Clear();

        var text = new StringBuilder();
        var chipWidth = unit == "人" ? 92 : 46;

        for (var i = 0; i < groups.Count; i++)
        {
            var members = groups[i];
            text.Append($"第 {i + 1} 组（{members.Count} {unit}）：")
                .Append(string.Join("、", members))
                .Append('\n');

            // ColumnSpacing=10 → 第 1 列 Margin
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = $"第 {i + 1} 组 · {members.Count} {unit}",
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.7,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });

            // 原版 VariableSizedWrapGrid(ItemWidth=chipWidth, ItemHeight=30) → WrapPanel，格子尺寸由成员自带
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var m in members)
            {
                chips.Children.Add(new Border
                {
                    Width = chipWidth,
                    Height = 30,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 2),
                    Background = Res("ControlFillColorSecondaryBrush"),
                    Child = new TextBlock
                    {
                        Text = m,
                        FontSize = 13,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                });
            }
            chips.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(chips, 1);
            row.Children.Add(chips);
            GroupsHost.Children.Add(row);
        }

        _groupText = text.ToString().TrimEnd();
        CopyGroupsButton.IsEnabled = groups.Count > 0;
        Toast.Text = $"已分成 {groups.Count} 组，可反复分组。";
    }

    /// <summary>把分组结果按「第 N 组（x 人）：a、b…」复制到剪贴板，方便直接发群里。</summary>
    private async void CopyGroups_Click(object? sender, RoutedEventArgs e)
    {
        if (_groupText.Length == 0) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is not { } clip) return;
            await clip.SetTextAsync(_groupText);
            Toast.Text = "已复制分组结果";
        }
        catch { Toast.Text = "复制失败"; }
    }

    // ══════════ 本地存档 + 小工具 ══════════
    // 存档类型挪到 Data\PickNumberConfig.cs —— 工具页与置顶浮窗共用一份，字段不会再各写各的

    private void LoadConfig()
    {
        var cfg = PickNumberConfig.Load();

        FromBox.Value = cfg.From;
        ToBox.Value = cfg.To;
        CountBox.Value = cfg.Count;
        NoRepeatBox.IsChecked = cfg.NoRepeat;
        _used.Clear();
        _used.AddRange(cfg.Used);

        _roster.Clear();
        _roster.AddRange(cfg.Roster ?? new List<string>());
        _usedNames.Clear();
        _usedNames.AddRange(cfg.UsedNames ?? new List<string>());
        _rosterSource = cfg.RosterSource ?? "";
        _savedMode = cfg.Mode;

        // 2026-10-06：名字大小（滑块还没挂事件，这里设值不会触发回调；_ready 也还是 false）
        _nameScale = cfg.NameScale;
        if (NameScaleSlider is not null)
        {
            NameScaleSlider.Value = _nameScale;
            NameScaleText.Text = $"{Math.Round(_nameScale * 100)}%";
        }
    }

    private void SaveConfig()
    {
        // 两个界面（工具页 / 置顶浮窗）共用同一个存档类型，字段不会再各写各的
        new PickNumberConfig
        {
            From = From,
            To = To,
            Count = WantCount,
            NoRepeat = NoRepeat,
            Used = _used,
            Mode = UseRoster ? 1 : 0,
            Roster = _roster,
            UsedNames = _usedNames,
            RosterSource = _rosterSource,
            NameScale = _nameScale,
        }.Save();
    }

    /// <summary>滑块改字号：记下来、存盘、把当前结果按新字号重绘一遍。</summary>
    private void NameScaleSlider_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;

        // ⚠️ Avalonia 没有 StepFrequency：先吸附到 0.1 的整数倍，写回滑块（会再进来一圈，那一圈值已对齐）。
        var v = Math.Round(e.NewValue / 0.1) * 0.1;
        if (Math.Abs(NameScaleSlider.Value - v) > 0.001) { NameScaleSlider.Value = v; return; }

        _nameScale = v;
        NameScaleText.Text = $"{Math.Round(_nameScale * 100)}%";
        SaveConfig();
        // 两种结果都重绘：滑块叫「结果字号」了，号码和名字都该跟着变
        if (_lastNames is not null) ShowNames(_lastNames, rolling: false);
        if (_lastNumbers is not null) ShowResult(_lastNumbers, rolling: false);
    }

    // ══════════ 示例名单 ══════════
    //
    // 2026-09-29（Nick 提）：静态示例名单 —— 和「示例名单」对话框里那两张图、两份示例文件是同一批姓名。
    /// <summary>示例名单里的姓名（10 人）。与 Assets/roster 下的示例文件保持一致。</summary>
    private static readonly string[] SampleNames =
    {
        "张伟", "王伟", "王芳", "李伟", "王秀英", "李秀英", "李娜", "张秀英", "刘伟", "张敏",
    };

    private async void SampleRoster_Click(object? sender, RoutedEventArgs e)
    {
        HideError();
        var dialog = new Views.RosterSampleDialog(_roster.Count > 0);
        await dialog.ShowAsync(GetTopLevel());

        if (dialog.UseSampleRequested) UseSampleRoster();
    }

    /// <summary>把示例的 10 个姓名装进抽取池，并切到名单模式 —— 让用户立刻能试一次。</summary>
    private void UseSampleRoster()
    {
        _roster.Clear();
        _roster.AddRange(SampleNames);
        _usedNames.Clear();          // 名单换了，上一份名单的抽号记录就没意义了（与导入名单同一处理）
        _rosterSource = "示例名单";

        if (!UseRoster) ModeRosterRadio.IsChecked = true;

        SaveConfig();
        SyncPoolMode();
        RefreshHints();
        Toast.Text = $"已载入示例名单（{SampleNames.Length} 人）。可单击「开始抽号」，或切到「随机分组」试一次。";
    }

    // ══════════ 名单导入 / 清空 ══════════

    private async void ImportRoster_Click(object? sender, RoutedEventArgs e)
    {
        HideError();
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入名单",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new FilePickerFileType("名单文件（txt / csv / Excel）")
                    {
                        Patterns = NameRoster.TextExts.Concat(NameRoster.ExcelExts).Select(x => "*" + x).ToArray(),
                    },
                },
            });
            if (files.Count == 0) return;

            var file = files[0];
            var names = NameRoster.Read(file.Path.LocalPath);
            if (names.Count == 0)
            {
                ShowError("该文件中未读取到姓名。txt / csv 请每行一个（或用逗号分隔）；"
                          + "Excel 请将姓名放在同一列。需要对照可单击「示例名单」。");
                return;
            }

            _roster.Clear();
            _roster.AddRange(names);
            _usedNames.Clear();          // 名单换了，上一份名单的抽号记录就没意义了
            _rosterSource = file.Name;

            // 导完直接切到名单模式，省得再点一次
            if (!UseRoster) ModeRosterRadio.IsChecked = true;

            SaveConfig();
            SyncPoolMode();
            RefreshHints();
            Toast.Text = $"已导入 {names.Count} 个姓名。";
        }
        catch (Exception ex)
        {
            ShowError("读取名单失败：" + ex.Message);
        }
    }

    private void ClearRoster_Click(object? sender, RoutedEventArgs e)
    {
        _roster.Clear();
        _usedNames.Clear();
        _rosterSource = "";
        SaveConfig();
        SyncPoolMode();
        RefreshHints();
        Toast.Text = "已清空名单";
    }

    private Brush Res(string key) => Services.ThemeBrush.Get(this, key);

    /// <summary>FA ContentDialog 的宿主（原版 XamlRoot 的等价物）。</summary>
    private TopLevel? GetTopLevel() => TopLevel.GetTopLevel(this);

    private Brush AccentBrush() => Services.ThemeBrush.Get(this, "AccentFillColorDefaultBrush");

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;
}
