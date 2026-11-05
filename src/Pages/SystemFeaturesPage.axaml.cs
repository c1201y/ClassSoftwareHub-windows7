using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data.SysFeatures;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 系统功能（自上游 dv1.2.0-insider1.8 移植，需求清单第 20 条）。
///
/// <para>
/// 与「系统优化」页的分工：那一页全是 <c>HKCU</c> 的界面设置、不提权；这一页全是
/// <c>HKLM</c> 与 <c>powercfg</c>、<b>必须提权</b>，部分项要重启才生效。
/// </para>
///
/// <para>
/// 交互照上游定案「排队 + 一次弹 UAC」：拨开关只进队列，
/// 点底部提示条的主按钮时，<see cref="SystemFeatures.ApplyAsync"/> 把所有改动
/// 拼成一个临时脚本跑一次，只弹一次授权。
/// </para>
///
/// <para>
/// 状态<b>只从服务层读</b>，页面不缓存：这些值用户可能在系统设置或组策略里改过，
/// 每次进来重读一遍最省事（读注册表是微秒级）。回填必须压在 <c>_loading</c> 里，
/// 否则 <c>IsCheckedChanged</c> / <c>SelectionChanged</c> 会把程序写入当成用户操作。
/// </para>
/// </summary>
public sealed partial class SystemFeaturesPage : PageBase
{
    private static readonly IReadOnlyList<SysFeatureDef> All = SystemFeatureCatalog.All;

    private static readonly Dictionary<string, SysFeatureDef> ById =
        All.ToDictionary(d => d.Id, StringComparer.Ordinal);

    /// <summary>待应用的改动：Id → 目标值（按 Id 去重，来回拨只留最后一次）。</summary>
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);

    /// <summary>按 Id 反查生成出来的控件。</summary>
    private readonly Dictionary<string, ToggleSwitch> _toggles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComboBox> _combos = new(StringComparer.Ordinal);

    /// <summary>正在回填界面，此时的 IsCheckedChanged / SelectionChanged 是程序触发的。</summary>
    private bool _loading;

    private bool _busy;

    /// <summary>确认框是否已经弹着 —— 同一时刻只允许一个 ContentDialog，第二个会抛异常。</summary>
    private bool _dialogOpen;

    /// <summary>用户点过「暂不应用」；之后再拨开关才重新露出。</summary>
    private bool _tipDismissed;

    private bool _failedMode;

    public SystemFeaturesPage()
    {
        InitializeComponent();

        // 浮层每次提问都来取一份现场数据（见 BuildAiContext）
        AiFlyout.ContextProvider = BuildAiContext;

        Loaded += (_, _) =>
        {
            if (RowsPanel.Children.Count == 0) BuildRows();
            LoadStates();
        };

        DetachedFromVisualTree += (_, _) => _tipDismissed = false;
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);

        HubAiEntry.IsVisible = App.Settings.Current.HubAiEnabled;

        // 从别的页面切回来时也重读一遍：可能刚在系统设置里改过
        if (RowsPanel.Children.Count > 0) LoadStates();
    }

    // ── 读取 ─────────────────────────────────────────────────────────────

    private void LoadStates()
    {
        _loading = true;
        try
        {
            foreach (var def in All)
            {
                var value = def.Read();

                if (def.Kind == SysFeatureKind.Toggle)
                {
                    if (!_toggles.TryGetValue(def.Id, out var toggle)) continue;

                    toggle.IsChecked = value is int v && v != 0;
                    toggle.IsEnabled = value is not null && !_busy;
                }
                else
                {
                    if (!_combos.TryGetValue(def.Id, out var combo)) continue;

                    combo.SelectedIndex = value is int v ? IndexOf(def, v) : -1;
                    combo.IsEnabled = value is not null && !_busy;
                }
            }

            // 休眠关着时快速启动不可能生效：整行停用，比让用户点了没反应好
            if (SystemFeatures.Hibernate() != FeatureState.On &&
                _toggles.TryGetValue("fast-startup", out var fast))
                fast.IsEnabled = false;

            var bytes = SystemFeatures.HibernateFileBytes();
            HibernateFileText.Text = bytes > 0 ? AppLog.Size(bytes) : "不存在（休眠已关闭）";
        }
        finally
        {
            _loading = false;
        }
    }

    private static int IndexOf(SysFeatureDef def, int value)
    {
        if (def.Choices is null) return -1;

        for (var i = 0; i < def.Choices.Count; i++)
            if (def.Choices[i].Value == value) return i;

        // 值不在档位里（系统里是别的数字）：不选中，别乱指一档骗人
        return -1;
    }

    // ── 生成界面 ─────────────────────────────────────────────────────────

    private void BuildRows()
    {
        RowsPanel.Children.Clear();
        _toggles.Clear();
        _combos.Clear();

        string? group = null;

        foreach (var def in All)
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

            RowsPanel.Children.Add(BuildRow(def));
        }
    }

    private SettingsCard BuildRow(SysFeatureDef def)
    {
        var card = new SettingsCard
        {
            Header = def.Header,
            Description = new TextBlock
            {
                Text = def.Description ?? "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75,
            },
        };

        if (def.Glyph.Length > 0)
            card.HeaderIcon = new FontIcon { Glyph = def.Glyph };

        if (def.Kind == SysFeatureKind.Toggle)
        {
            var toggle = new ToggleSwitch
            {
                Tag = def.Id,
                OnContent = "已开启",
                OffContent = "已关闭",
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            toggle.IsCheckedChanged += Row_Toggled;

            _toggles[def.Id] = toggle;
            card.Content = toggle;
        }
        else
        {
            var combo = new ComboBox
            {
                Tag = def.Id,
                MinWidth = 150,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var items = new List<ComboBoxItem>();
            foreach (var choice in def.Choices ?? Array.Empty<SysChoice>())
                items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });

            combo.ItemsSource = items;
            combo.SelectionChanged += Row_SelectionChanged;

            _combos[def.Id] = combo;
            card.Content = combo;
        }

        return card;
    }

    // ── 排队 ─────────────────────────────────────────────────────────────

    private void Row_Toggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ToggleSwitch { Tag: string id } toggle) return;
        if (!ById.TryGetValue(id, out var def)) return;

        Queue(def, toggle.IsChecked == true ? 1 : 0);
    }

    private void Row_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _busy) return;
        if (sender is not ComboBox { Tag: string id } combo) return;
        if (!ById.TryGetValue(id, out var def)) return;
        if (combo.SelectedItem is not ComboBoxItem { Tag: int value }) return;

        Queue(def, value);
    }

    private void Queue(SysFeatureDef def, int target)
    {
        // 与当前系统值一致就不排队（否则"拨回去"也会留下一条什么都不改的待应用项）
        if (def.Read() == target) _pending.Remove(def.Id);
        else _pending[def.Id] = target;

        AppLog.Step("sysfeatures", _pending.ContainsKey(def.Id)
            ? $"加入待应用清单｜项目={def.Id}｜目标={target}｜说明={def.Label(target)}"
            : $"移出待应用清单｜项目={def.Id}｜原因=与当前系统状态一致");

        _tipDismissed = false;
        _failedMode = false;
        UpdatePendingUi();
    }

    private void CancelPending_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (!ById.TryGetValue(id, out var def)) return;

        _pending.Remove(id);

        // 界面上那一行要拨回注册表里的真实值，否则它停在一个"看着改了、其实不会写入"的位置
        _loading = true;
        var actual = def.Read();

        if (def.Kind == SysFeatureKind.Toggle && _toggles.TryGetValue(id, out var toggle))
            toggle.IsChecked = actual is int v && v != 0;
        else if (_combos.TryGetValue(id, out var combo))
            combo.SelectedIndex = actual is int v2 ? IndexOf(def, v2) : -1;

        _loading = false;

        AppLog.Step("sysfeatures", $"移出待应用清单｜项目={id}");
        UpdatePendingUi();
    }

    // ── 提示条 ───────────────────────────────────────────────────────────

    private void UpdatePendingUi()
    {
        PendingBar.IsVisible = _pending.Count > 0;

        if (_pending.Count == 0)
        {
            _failedMode = false;
            _tipDismissed = false;
            return;
        }

        BuildPendingList();

        PendingTitle.Text = _failedMode
            ? $"有 {_pending.Count} 项未生效"
            : $"有 {_pending.Count} 项待应用";
        PendingSubtitle.Text = _failedMode
            ? "可能被组策略限定。可以再试一次，或逐项取消。"
            : "应用时会请求一次管理员授权。";
        ApplyButton.Content = _failedMode ? "重试" : "应用更改";

        // 用户点过「暂不应用」：收起整条提示，直到下一次拨动开关
        PendingBar.IsVisible = !_tipDismissed;
    }

    /// <summary>清单：每行「项名 → 目标值」+ 一颗「取消」。</summary>
    private void BuildPendingList()
    {
        PendingList.Children.Clear();

        foreach (var (id, value) in _pending)
        {
            if (!ById.TryGetValue(id, out var def)) continue;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 3, 0, 3),
            };

            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Text = $"{def.Header}  →  {def.Label(value)}",
            };

            var cancel = new Button
            {
                Content = "取消",
                Tag = id,
                MinWidth = 0,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            cancel.Click += CancelPending_Click;

            Grid.SetColumn(cancel, 1);
            row.Children.Add(text);
            row.Children.Add(cancel);

            PendingList.Children.Add(row);
        }
    }

    private void DismissPending_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 只是收起来，队列原样保留
        _tipDismissed = true;
        PendingBar.IsVisible = false;
    }

    private async void ApplyPending_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await ApplyPendingAsync();
        }
        catch (Exception ex)
        {
            // async void 里漏出去的异常会直接终止进程 —— 兜到底
            AppLog.Exception("sysfeatures", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    // ── 应用 ─────────────────────────────────────────────────────────────

    private async Task ApplyPendingAsync()
    {
        if (_busy || _pending.Count == 0) return;
        if (_dialogOpen) return;

        var changes = _pending
            .Where(kv => ById.ContainsKey(kv.Key))
            .Select(kv => (Def: ById[kv.Key], Value: kv.Value))
            .ToList();

        if (changes.Count == 0) return;

        // 整批一次总确认：逐项弹窗的话，十几项要点十几次（与"一次 UAC"是同一个道理）
        var needRestart = changes.Any(c => c.Def.NeedsRestart);
        var warn = changes.FirstOrDefault(c => c.Def.Confirm is not null).Def?.Confirm;

        var message = $"将修改 {changes.Count} 项系统设置，需要一次管理员授权。" +
                      (warn is null ? "" : $"\n\n{warn}") +
                      (needRestart ? "\n\n其中有的项要重启后才生效。" : "");

        if (!await ConfirmAsync("应用系统设置", message)) return;

        _busy = true;
        try
        {
            var result = await SystemFeatures.ApplyAsync(changes);

            if (!result.Ok)
            {
                ShowNotice(result.Error ?? "应用失败。", InfoBarSeverity.Error);
                _failedMode = true;
                UpdatePendingUi();
                return;
            }

            if (result.AllMatched)
            {
                _pending.Clear();
                ShowNotice($"已应用 {result.Requested} 项。", InfoBarSeverity.Success);
                _failedMode = false;
            }
            else
            {
                // 只有确认没生效的项留在队列里，方便直接重试
                var failed = new HashSet<string>(result.Failed, StringComparer.Ordinal);
                foreach (var id in _pending.Keys.ToList())
                    if (!failed.Contains(id)) _pending.Remove(id);

                ShowNotice(
                    $"已应用 {result.Matched}/{result.Requested} 项，未生效：{string.Join("、", result.Failed.Select(NameOf))}。",
                    InfoBarSeverity.Warning);

                _failedMode = _pending.Count > 0;
            }
        }
        finally
        {
            _busy = false;
            LoadStates();
            UpdatePendingUi();
        }
    }

    private static string NameOf(string id) => ById.TryGetValue(id, out var def) ? def.Header : id;

    // ── Hub AI ───────────────────────────────────────────────────────────

    private void HubAiEntry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => AiFlyout.Toggle();

    /// <summary>
    /// 给浮层准备这一页的现场数据。
    /// <para>
    /// 这一页的项直接动系统，用户最爱问的是「改了会不会有影响」「为什么点了没反应」，
    /// 所以每项都附**当前值 + 目标值 + 是否要重启 + 读不到的原因**。
    /// </para>
    /// </summary>
    private PageContextDoc BuildAiContext()
    {
        var doc = new PageContextDoc(
            "系统功能",
            "这一页修改的是整台电脑的系统设置（HKLM 与电源方案），需要管理员授权，部分项要重启才生效。" +
            "回答时以「全部条目」里的当前值为准，直接点名是哪一项；不要给与本页无关的建议。" +
            "⚠️ 你无法替用户改设置，也不要说「已经帮你改好了」—— 写入只能由用户拨动开关、" +
            "再点右下角提示条的主按钮完成，而且必须经过一次管理员授权。");

        doc.Add("状态", new
        {
            待应用项数 = _pending.Count,
            正在应用 = _busy,
            休眠文件 = SystemFeatures.HibernateFileBytes() is var b && b > 0 ? AppLog.Size(b) : "不存在",
        });

        doc.Add("全部条目", All.Select(d =>
        {
            var value = d.Read();
            return new
            {
                d.Id,
                d.Header,
                分组 = d.Group,
                说明 = d.Description,
                当前值 = value,
                当前显示 = value is int v ? d.Label(v) : "读取不到",
                可选档位 = d.Choices?.Select(c => new { c.Label, c.Value }).ToList(),
                待应用目标值 = _pending.TryGetValue(d.Id, out var t) ? t : (int?)null,
                需要重启 = d.NeedsRestart,
                改前提示 = d.Confirm,
            };
        }).ToList());

        doc.Add("界面怎么操作", new
        {
            拨开关或改下拉框 = "只排队，不改系统",
            应用更改 = "右下角提示条的主按钮，一次授权、一次写入全部待应用项",
            单项取消 = "提示条清单里每行右侧的「取消」",
            提示被收起 = "页面顶部出现「待应用 N 项」按钮",
        });

        return doc;
    }

    // ── 界面小工具 ───────────────────────────────────────────────────────

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
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Catch("sysfeatures", "显示确认框", ex);
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
}
