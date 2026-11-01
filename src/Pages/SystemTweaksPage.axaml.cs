using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.Tweaks;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 系统优化（自上游 dv1.2.0-insider1.8 移植，需求清单第 20 条）。对齐 Dism++「系统优化」里能安全落地的部分。
///
/// <para>
/// ⚠️ 这些项都在当前用户（<c>HKCU</c>），改完即时生效，<b>不弹 UAC</b>：
/// 切换只排队，点「应用更改」时统一写入；需要外壳重读的项由 <see cref="TweakRefresh"/> 标注，
/// 写完后统一发一条通知（不再默认杀 explorer）。
/// （机器级项在「系统功能」页，那里才提权。）
/// </para>
/// <para>
/// 按系统 build 自动<b>停用</b>不适用项（整张卡片置灰、点不动，而不是藏起来 ——
/// Win10 上看不到"任务栏靠左"这种 Win11 专有设置，但能知道有这一项）；
/// 本来就有多档的项用下拉框（照 Dism++ 的 <c>Type="Combo"</c>）。
/// </para>
/// <para>
/// 待应用入口是底部提示条：这一页是一长列卡片，用户拨完最下面那张不必滚回顶部去应用。
/// 提示被收起后，下一次拨动开关才会重新露出（应用完成后另有「重启资源管理器」的收尾态）。
/// </para>
/// </summary>
public sealed partial class SystemTweaksPage : PageBase
{
    /// <summary>待应用的改动：Id → <b>目标注册表值</b>（按 Id 去重，来回切换只留最后一次）。</summary>
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ToggleSwitch> _toggles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComboBox> _combos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SettingsCard> _cards = new(StringComparer.Ordinal);

    /// <summary>正在回填界面，此时控件的 IsCheckedChanged / SelectionChanged 是程序触发的。</summary>
    private bool _loading;

    private bool _busy;

    /// <summary>确认框是否已经弹着 —— 同一时刻只允许一个 ContentDialog，第二个会抛异常。</summary>
    private bool _dialogOpen;

    /// <summary>用户点过提示上的「暂不应用」。</summary>
    private bool _tipDismissed;

    /// <summary>底部提示当前在说哪件事 —— 决定它那个主按钮按下去是「应用」还是「重启资源管理器」。</summary>
    private TipMode _tipMode = TipMode.Pending;

    private enum TipMode
    {
        Pending,
        Applied,
        Failed,
    }

    public SystemTweaksPage()
    {
        InitializeComponent();

        // ⚠️ 必须等 Loaded 之后再读：控件要在库里建好、系统版本也要读得到。
        Loaded += (_, _) =>
        {
            if (RowsPanel.Children.Count == 0) BuildRows();
            ApplyOsFilter();
            LoadStates();
        };

        DetachedFromVisualTree += (_, _) => _tipDismissed = false;

        // 浮层每次提问都来取一份现场数据（见 BuildAiContext）
        AiFlyout.ContextProvider = BuildAiContext;
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        HubAiEntry.IsVisible = App.Settings.Current.HubAiEnabled;
        if (RowsPanel.Children.Count > 0) LoadStates();
    }

    // ── 构建与回填 ───────────────────────────────────────────────────────

    private void BuildRows()
    {
        RowsPanel.Children.Clear();
        _toggles.Clear();
        _combos.Clear();
        _cards.Clear();

        string? group = null;

        foreach (var def in TweakCatalog.All)
        {
            if (!string.Equals(group, def.Group, StringComparison.Ordinal))
            {
                group = def.Group;
                RowsPanel.Children.Add(new TextBlock
                {
                    Text = group,
                    FontSize = 14,
                    Margin = new Thickness(0, 10, 0, 2),
                });
            }

            var card = BuildRow(def);
            _cards[def.Id] = card;
            RowsPanel.Children.Add(card);
        }
    }

    private SettingsCard BuildRow(TweakDef def)
    {
        var card = new SettingsCard
        {
            Header = def.Header,
            Description = new TextBlock
            {
                Text = def.Description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75,
            },
        };

        if (def.IsChoice)
        {
            var combo = new ComboBox
            {
                Tag = def.Id,
                MinWidth = 150,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var items = new List<ComboBoxItem>();
            foreach (var choice in def.Choices)
                items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });

            combo.ItemsSource = items;
            combo.SelectionChanged += Row_SelectionChanged;

            _combos[def.Id] = combo;
            card.Content = combo;
        }
        else
        {
            var toggle = new ToggleSwitch
            {
                Tag = def.Id,
                OnContent = "开",
                OffContent = "关",
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            toggle.IsCheckedChanged += Row_Toggled;

            _toggles[def.Id] = toggle;
            card.Content = toggle;
        }

        return card;
    }

    /// <summary>
    /// 按当前系统的 build 号<b>停用</b>不适用的项（照 Dism++ 的 <c>&lt;Applicable&gt;</c>）。
    /// <para>
    /// 卡片留在原位、整张置灰、点不动。这样用户看到的是"系统里有这么一项，但这台机器用不上"，
    /// 而不是页面上凭空少了两个开关、让人以为功能被人砍了。
    /// ⚠️ 本仓库的 SettingsCard 没有内置的禁用视觉状态，所以这里显式降不透明度并补一句原因。
    /// </para>
    /// </summary>
    private void ApplyOsFilter()
    {
        var build = SystemInfo.OsBuild();
        var disabled = new List<string>();

        foreach (var def in TweakCatalog.All)
        {
            if (!_cards.TryGetValue(def.Id, out var card)) continue;

            var available = TweakRules.IsApplicable(def, build);
            card.IsEnabled = available;

            if (available)
            {
                card.Opacity = 1.0;
                if (card.Description is TextBlock block) block.Text = def.Description;
            }
            else
            {
                card.Opacity = 0.55;
                if (card.Description is TextBlock block)
                    block.Text = def.Description + "\n此项需要 " + RequiredSystemName(def) + "，当前系统不支持，已停用。";
                disabled.Add(def.Id);
            }
        }

        if (disabled.Count == 0)
            AppLog.Step("tweaks", $"系统 build {build}：全部 {TweakCatalog.All.Count} 项均可用");
        else
            AppLog.Step("tweaks", $"系统 build {build}：停用 {disabled.Count} 项（{string.Join("、", disabled)}）");
    }

    /// <summary>门槛翻成人话。</summary>
    private static string RequiredSystemName(TweakDef def)
    {
        if (def.MinBuild >= TweakCatalog.Win11Build) return "Windows 11 及以上系统";
        if (def.MaxBuild > 0 && def.MaxBuild < TweakCatalog.Win11Build) return "Windows 10";
        return "更高的系统版本";
    }

    /// <summary>把界面上所有控件按注册表当前值摆一遍。</summary>
    private void LoadStates()
    {
        _loading = true;
        try
        {
            var seen = new List<string>();

            foreach (var def in TweakCatalog.All)
            {
                if (def.IsChoice)
                {
                    if (!_combos.TryGetValue(def.Id, out var combo)) continue;

                    var index = IndexOfValue(def, TweakStore.EffectiveValue(def));
                    combo.SelectedIndex = index;
                    seen.Add($"{def.Id}={(index >= 0 ? def.Choices[index].Label : "未识别")}");
                }
                else
                {
                    if (!_toggles.TryGetValue(def.Id, out var toggle)) continue;

                    toggle.IsChecked = TweakStore.EffectiveValue(def) == def.OnValue;
                    seen.Add($"{def.Id}={(toggle.IsChecked == true ? "开" : "关")}");
                }
            }

            AppLog.Step("tweaks", $"按当前系统设置回填 {seen.Count} 项；当前状态：{string.Join("、", seen)}");
        }
        finally
        {
            _loading = false;
        }
    }

    private static int IndexOfValue(TweakDef def, int value)
    {
        for (var i = 0; i < def.Choices.Count; i++)
            if (def.Choices[i].Value == value) return i;

        return -1;   // 注册表里的值不在选项表里（系统版本差异或手工改过）：不假装选中某一档
    }

    // ── 切换 ─────────────────────────────────────────────────────────────

    private async void Row_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ToggleSwitch { Tag: string id } toggle) return;

        var def = TweakCatalog.All.FirstOrDefault(t => t.Id == id);
        if (def is null || def.IsChoice) return;

        var want = toggle.IsChecked == true;
        var target = want ? def.OnValue : def.OffValue;

        // 高危项：先问一句。取消就把开关拨回去（拨回时同样要压 _loading，否则会再弹一次）
        if (def.Risk == TweakRisk.Caution)
        {
            // ⚠️ 两个边界：同屏已经在弹一个 ContentDialog；等待期间页面被切走。
            if (_dialogOpen)
            {
                Revert(toggle, want);
                return;
            }

            bool ok;
            try
            {
                ok = await ConfirmAsync(def.Header,
                    $"将要修改系统设置：{def.Description}\n\n应用后若要还原，把这一项再改回去并重新应用。");
            }
            catch (Exception ex)
            {
                // 弹不出来就别改：宁可这一下不生效，也不能带着"没确认过"的状态往下走
                AppLog.Warn("tweaks", $"确认框未显示（{def.Id}）：{ex.Message}");
                ok = false;
            }

            if (!ok)
            {
                Revert(toggle, want);
                return;
            }
        }

        Queue(def, target, want ? "开" : "关");
    }

    /// <summary>
    /// 下拉框选了别的档。
    /// <para>
    /// ⚠️ 下拉框<b>不弹二次确认</b>：档位之间切换本身不是危险操作，而每选一档都弹一次
    /// 会让这一行没法用。所以标了 <see cref="TweakRisk.Caution"/> 的项都保留成开关。
    /// </para>
    /// </summary>
    private void Row_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ComboBox { Tag: string id } combo) return;

        var def = TweakCatalog.All.FirstOrDefault(t => t.Id == id);
        if (def is null || !def.IsChoice) return;

        var index = combo.SelectedIndex;
        if (index < 0 || index >= def.Choices.Count) return;

        var choice = def.Choices[index];
        Queue(def, choice.Value, choice.Label);
    }

    /// <summary>
    /// 记一笔待应用。
    /// <para>
    /// ⚠️ 判据用 <see cref="TweakStore.ReadValue"/>（注册表里的<b>实际值</b>），不用 <c>EffectiveValue</c>：
    /// 值缺失时后者会假装成默认档，那样"选了默认档"就会被当成"没变化"而永远写不进去。
    /// </para>
    /// </summary>
    private void Queue(TweakDef def, int target, string label)
    {
        if (TweakStore.ReadValue(def) == target) _pending.Remove(def.Id);
        else _pending[def.Id] = target;

        AppLog.Step("tweaks", _pending.ContainsKey(def.Id)
            ? $"排队：{def.Id} = {target}（{label}）"
            : $"取消排队：{def.Id}（与当前系统状态一致）");

        // 用户又动了一下开关 ⇒ 他认为还在改，可以重新提示一次
        _tipDismissed = false;
        _tipMode = TipMode.Pending;

        RefreshPendingUi();
    }

    /// <summary>把开关拨回原位，且不让这次回拨再触发一次排队。</summary>
    private void Revert(ToggleSwitch toggle, bool want)
    {
        _loading = true;
        try { toggle.IsChecked = !want; }
        finally { _loading = false; }
    }

    // ── 提示条 ───────────────────────────────────────────────────────────

    private void RefreshPendingUi()
    {
        // 「已应用」这一态在队列清空后仍要留着，否则「重启资源管理器」出口一闪就没了
        if (_pending.Count == 0 && _tipMode != TipMode.Applied)
        {
            PendingBar.IsVisible = false;
            _tipMode = TipMode.Pending;
            _tipDismissed = false;
            return;
        }

        RebuildPendingList();

        if (_tipMode == TipMode.Applied)
        {
            PendingTitle.Text = "已应用，未看到变化？";
            PendingSubtitle.Text = "已通知系统刷新。若任务栏或桌面没有变化，重启资源管理器即可看到效果。";
            ApplyButton.Content = "重启资源管理器";
        }
        else if (_tipMode == TipMode.Failed)
        {
            PendingTitle.Text = $"有 {_pending.Count} 项未生效";
            PendingSubtitle.Text = "可能被组策略或其他安全软件固定。可以再试一次，或重启系统后再看。";
            ApplyButton.Content = "重试";
        }
        else
        {
            var needsRefresh = _pending.Keys.Any(id =>
                TweakCatalog.All.FirstOrDefault(t => t.Id == id)?.Refresh != TweakRefresh.None);

            PendingTitle.Text = $"有 {_pending.Count} 项待应用";
            PendingSubtitle.Text = needsRefresh
                ? "应用后自动刷新任务栏与桌面。"
                : "只写当前用户设置，无需管理员权限。";
            ApplyButton.Content = "应用更改";
        }

        PendingBar.IsVisible = !_tipDismissed;
    }

    /// <summary>把队列摊成一份可逐项取消的清单：每行是「项名 → 目标值」加一个取消按钮。</summary>
    private void RebuildPendingList()
    {
        PendingList.Children.Clear();

        foreach (var (id, target) in _pending)
        {
            var def = TweakCatalog.All.FirstOrDefault(t => t.Id == id);
            if (def is null) continue;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 3, 0, 3),
            };

            var label = new TextBlock
            {
                Text = $"{def.Header} → {DescribeTarget(def, target)}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
            };

            var cancel = new Button
            {
                Content = "取消",
                Tag = def.Id,
                MinWidth = 0,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            cancel.Click += CancelPending_Click;

            Grid.SetColumn(cancel, 1);
            row.Children.Add(label);
            row.Children.Add(cancel);

            PendingList.Children.Add(row);
        }
    }

    /// <summary>目标值怎么念：开关报开 / 关，下拉框报它自己那一档的名字。</summary>
    private static string DescribeTarget(TweakDef def, int value)
    {
        if (def.IsChoice)
            foreach (var choice in def.Choices)
                if (choice.Value == value) return choice.Label;

        return value == def.OnValue ? "开" : "关";
    }

    /// <summary>
    /// 清单里点「取消」：只把这一项从队列里撤掉，然后把界面拨回注册表里的实际值。
    /// ⚠️ 必须回填，否则那个开关会停在一个"看起来改了、其实不会写入"的位置。
    /// </summary>
    private void CancelPending_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        if (_pending.Remove(id)) AppLog.Step("tweaks", $"从任务清单取消：{id}");

        _tipMode = TipMode.Pending;
        LoadStates();
        RefreshPendingUi();
    }

    private void DismissPending_Click(object? sender, RoutedEventArgs e)
    {
        // 只是收起来，队列原样保留 —— 下一次拨开关会重新露出
        _tipDismissed = true;
        PendingBar.IsVisible = false;

        if (_tipMode == TipMode.Applied) _tipMode = TipMode.Pending;
    }

    // ── 应用 ─────────────────────────────────────────────────────────────

    private async void ApplyPending_Click(object? sender, RoutedEventArgs e)
    {
        if (_tipMode == TipMode.Applied)
        {
            RestartExplorer();
            return;
        }

        try
        {
            await ApplyPendingAsync();
        }
        catch (Exception ex)
        {
            AppLog.Exception("tweaks", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task ApplyPendingAsync()
    {
        if (_busy || _pending.Count == 0) return;

        var changes = _pending
            .Where(kv => TweakCatalog.All.Any(t => t.Id == kv.Key))
            .Select(kv => (Def: TweakCatalog.All.First(t => t.Id == kv.Key), Value: kv.Value))
            .ToList();

        if (changes.Count == 0) return;

        _busy = true;
        ApplyButton.IsEnabled = false;
        PendingTitle.Text = "正在应用…";
        PendingSubtitle.Text = "正在写入当前用户设置，并按实际值核对结果。";

        TweakApplyResult result;
        try
        {
            result = await TweakStore.ApplyAsync(changes);
        }
        catch (Exception ex)
        {
            // ApplyAsync 自己吞了大部分异常，但 async void 里任何漏网的都会直接终止进程 —— 兜一层
            AppLog.Exception("tweaks", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
            RefreshPendingUi();
            return;
        }
        finally
        {
            _busy = false;              // 放在 finally：return 与正常结束都会经过这里
            ApplyButton.IsEnabled = true;
        }

        // 只有<b>确认没生效</b>的项留在队列里，方便直接重试；生效的全部出队。
        _pending.Clear();
        foreach (var (id, target) in TweakRules.RemainingAfterApply(changes, result.Failed))
            _pending[id] = target;

        LoadStates();   // 界面一律按注册表实际值重摆，不信"我以为写进去了"

        if (result.Matched == result.Requested)
        {
            // 状态全对、但写入过程报过错：既要说"生效了"，也不能把那条错误吞掉
            ShowNotice(
                result.Ok
                    ? $"已应用 {result.Matched} 项。"
                    : $"已应用 {result.Matched} 项，当前状态与目标一致，但写入过程有报错：{result.Error}",
                result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);

            // ⚠️ 这步必须在 finally 之后：放在 try 里会被 finally 的收尾动作看到空队列而把出口收掉。
            if (changes.Any(c => c.Def.Refresh != TweakRefresh.None))
            {
                _tipMode = TipMode.Applied;
                _tipDismissed = false;
                RefreshPendingUi();
            }
            else
            {
                _tipMode = TipMode.Pending;
                RefreshPendingUi();
            }
        }
        else
        {
            ShowNotice(
                $"已应用 {result.Matched}/{result.Requested} 项；未生效：{DescribeFailed(result.Failed)}。"
                + "可能被组策略或其他安全软件固定，剩下的项已留在任务列表里可重试。",
                InfoBarSeverity.Warning);

            // 先定状态再刷界面，否则会被"待应用"的文案盖回去
            _tipMode = TipMode.Failed;
            _tipDismissed = false;
            RefreshPendingUi();
        }
    }

    /// <summary>把失败项的名称拼出来（拿不到名字就退回 Id，总比只说"有 3 项"强）。</summary>
    private static string DescribeFailed(IReadOnlyList<string> failed)
    {
        if (failed.Count == 0) return "无";
        return string.Join("、", failed.Select(id =>
            TweakCatalog.All.FirstOrDefault(t => t.Id == id)?.Header ?? id));
    }

    private void RestartExplorer()
    {
        TweakStore.RestartExplorer();

        // ⚠️ 不能直接收掉提示：队列里可能还有没应用的改动，那会被一起藏起来。
        _tipMode = TipMode.Pending;
        RefreshPendingUi();

        ShowNotice("资源管理器已重启。若任务栏没有回来，用任务管理器重新运行 explorer.exe。", InfoBarSeverity.Success);
    }

    // ── 界面小工具 ────────────────────────────────────────────────────────

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (_dialogOpen) return false;

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                },
                PrimaryButtonText = "应用这项",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Warn("tweaks", "确认框没能弹出：" + ex.Message);
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        // NoticeBar 平时 Collapsed（避免那条外边距在页头下面撑出空档），用之前先放出来
        NoticeBar.IsVisible = true;
        NoticeBar.Severity = severity;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    // ── Hub AI ───────────────────────────────────────────────────────────

    /// <summary>弹出提问浮层。浮层是公共控件，别在这一页另写一套动画与发送逻辑。</summary>
    private void HubAiEntry_Click(object? sender, RoutedEventArgs e) => AiFlyout.Toggle();

    /// <summary>
    /// 给浮层准备这一页的现场数据（上游 2026-10-31 二版）。
    /// <para>
    /// 这份数据不只是"列清单"，而是让模型**不必再问、也不必自己筛**：
    /// 已开启 / 已停用 / 待应用三份名单直接给出 —— 几十项让它自己从全表里筛，既慢又容易漏；
    /// 每项附「本机是否适用」，免得它在 Win10 上推荐一个 Win11 才有的开关；
    /// 「本页不做哪些」写明 —— 问「能不能关 UAC」时能直接答"这里没有"，而不是瞎猜。
    /// </para>
    /// </summary>
    private PageContextDoc BuildAiContext()
    {
        var build = SystemInfo.OsBuild();
        var all = TweakCatalog.All;

        var on = all.Where(TweakStore.IsOn).ToList();
        var unavailable = all.Where(t => !TweakRules.IsApplicable(t, build)).ToList();

        var doc = new PageContextDoc(
            "系统优化",
            "这一页把当前用户（HKCU）的界面与使用习惯设置收成一份清单，写入不需要管理员权限、不弹 UAC。" +
            "提问的人通常想知道：这一项具体改了什么、开了会不会影响上课 / 投影 / 打印、为什么拨了没反应。" +
            "回答时以「全部条目」里的项名、分组、注册表路径与当前值为准，直接点名是哪一项、在哪一组；" +
            "不要给「清理磁盘」「更新驱动」这类与本页无关的泛泛建议。" +
            "另有一条硬约束：你无法替用户改设置 —— 写入只能由他自己拨开关、再点右下角提示条的主按钮完成，" +
            "所以不要说你「已经帮你打开了」，要告诉他去点哪一项。" +
            "说话用大白话：UAC、CLSID 这类术语只供你自己判断，用户很可能不知道它们是什么；" +
            "被问到「为什么没有某项」时，用一句「会动到系统安全与系统服务，这一页不提供」说清即可，不要罗列术语。");

        doc.Add("状态", new
        {
            系统build = build,
            系统版本 = build >= TweakCatalog.Win11Build ? "Windows 11" : "Windows 10",
            本页条目总数 = all.Count,
            已启用的项数 = on.Count,
            本机不适用的项数 = unavailable.Count,
            待应用项数 = _pending.Count,
            正在应用 = _busy,
        });

        // 三份名单：模型一眼看到"现在开着什么""哪些点不动""哪些还没写进去"
        doc.Add("当前已启用", on.Select(t => new { t.Id, t.Header, 分组 = t.Group }).ToList());

        doc.Add("本机不适用（界面上整卡置灰、点不动）", unavailable.Select(t => new
        {
            t.Id,
            t.Header,
            原因 = t.MinBuild > 0
                ? $"需要 Windows 11 及以上（build ≥ {t.MinBuild}），本机 {build}"
                : $"仅 Windows 10 有效（build ≤ {t.MaxBuild}），本机 {build}",
        }).ToList());

        doc.Add("已排队待应用（拨了但还没写入注册表）", _pending.Select(kv => new
        {
            Id = kv.Key,
            项名 = all.FirstOrDefault(t => t.Id == kv.Key)?.Header ?? kv.Key,
            目标值 = kv.Value,
            写入后 = all.FirstOrDefault(t => t.Id == kv.Key) is { } d
                ? (kv.Value == d.OnValue ? "开" : kv.Value == d.OffValue ? "关" : $"第 {kv.Value} 档")
                : "",
        }).ToList());

        doc.Add("全部条目", all.Select(t => new
        {
            t.Id,
            t.Header,
            分组 = t.Group,
            说明 = t.Description,
            注册表 = t.FullPath,
            当前值 = TweakStore.EffectiveValue(t),
            当前是否开启 = TweakStore.IsOn(t),
            开 = t.OnValue,
            关 = t.OffValue,
            可选档位 = t.IsChoice
                ? t.Choices.Select(c => new { 文字 = c.Label, 值 = c.Value }).ToList()
                : null,
            待应用的目标值 = _pending.TryGetValue(t.Id, out var target) ? target : (int?)null,
            需要刷新 = t.Refresh switch
            {
                TweakRefresh.Tray => "任务栏（写完发 TraySettings 通知）",
                TweakRefresh.Desktop => "桌面（写完发刷新通知）",
                _ => "不需要",
            },
            改前需二次确认 = t.Risk == TweakRisk.Caution,
            本机是否适用 = TweakRules.IsApplicable(t, build),
        }).ToList());

        doc.Add("本页不做的事（被问到就这样答，不要自己推测能不能改）", new[]
        {
            "UAC、SmartScreen、Defender 等安全设置 —— 改错会让程序打不开，本页一律不提供",
            "系统服务（SysMain、搜索索引、打印后台）—— 停了机器变慢且很难联想到是优化工具所为",
            "Windows 更新策略 —— 教室机不更新比更新更麻烦",
            "右键菜单禁用项 —— 靠删 CLSID 实现，影响面大且难还原",
            "IE / Edge / WinRAR 等具体软件的设置 —— 装了才有意义，不属于系统层",
            "机器级设置（休眠、快速启动、分页文件）—— 在「系统功能」页，那里才提权",
        });

        doc.Add("界面怎么操作", new
        {
            拨开关或改下拉框 = "只排队，不立即写入注册表",
            应用更改 = "页面右下角提示条的主按钮，一次写入全部待应用项",
            单项取消 = "提示条的任务清单里，每行右侧有「取消」",
            提示被收起后 = "页面顶部出现「待应用 N 项」按钮，点它把提示叫回来",
            提示条的位置 = "右下角浮出，不随页面滚动",
        });

        return doc;
    }
}
