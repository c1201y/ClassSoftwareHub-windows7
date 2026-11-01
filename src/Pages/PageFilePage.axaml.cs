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
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 虚拟内存 / 分页文件（自上游 dv1.2.0-insider1.8 移植，需求清单第 19 条）。
///
/// <para>
/// 搬的是系统「虚拟内存」对话框，换成 Fluent 的排法：上面选驱动器，下面给这一个驱动器
/// 三选一（自定义 / 系统管理 / 无）。改动只进队列，点提示条的主按钮时**一次授权**写完。
/// </para>
///
/// <para>
/// ⛔ 状态**只从服务层读**，页面不缓存：这些配置可能刚在系统属性里改过。
/// 回填必须压在 <c>_loading</c> 里 —— <c>IsCheckedChanged</c> / <c>Checked</c> / <c>ValueChanged</c>
/// 在程序赋值时同样会触发，不压住就会把"回填"当成"用户改了"。
/// </para>
///
/// <para>
/// ⚠️ 这一页的改动**必须重启才生效**（系统在启动阶段按配置创建或删除 pagefile.sys），
/// 所以页面上有一条常驻说明，应用成功后的提示也要再讲一次。
/// </para>
/// </summary>
public sealed partial class PageFilePage : PageBase
{
    /// <summary>本机的固定磁盘（含当前的与待应用的目标值）。</summary>
    private List<PageFileDrive> _drives = new();

    /// <summary>待应用的改动：盘符 → 目标配置。</summary>
    private readonly Dictionary<string, PageFilePlan> _pending = new(StringComparer.Ordinal);

    /// <summary>列表每行右列那个「分页文件大小」文本框，改值时只动它、不重建列表。</summary>
    private readonly Dictionary<string, TextBlock> _rowSizeText = new(StringComparer.Ordinal);

    /// <summary>当前注册表里是不是「自动管理」。</summary>
    private bool _autoCurrent;

    /// <summary>应用之后打算变成「自动管理」还是手动。</summary>
    private bool _autoPending;

    /// <summary>自动管理这一档有没有被改过（它本身也算一项待应用）。</summary>
    private bool _autoChanged;

    private string? _selected;

    /// <summary>正在回填界面，此时的控件事件是程序触发的。</summary>
    private bool _loading;

    /// <summary>正在重建列表并恢复选中，此时的 SelectionChanged 是程序触发的。</summary>
    private bool _syncing;

    private bool _busy;

    /// <summary>确认框是否已经弹着 —— 同一时刻只允许一个 ContentDialog，第二个会抛异常。</summary>
    private bool _dialogOpen;

    /// <summary>用户点过「暂不应用」；之后再改才重新弹。</summary>
    private bool _tipDismissed;

    private bool _failedMode;

    private bool _initialized;

    public PageFilePage()
    {
        InitializeComponent();

        // NumberBox.ValueChanged 是 TypedEventHandler，XAML 里挂不上（编译器只认 RoutedEvent / .NET 事件）
        NumMin.ValueChanged += Number_Changed;
        NumMax.ValueChanged += Number_Changed;

        // 浮层每次提问都来取一份现场数据（见 BuildAiContext）
        AiFlyout.ContextProvider = BuildAiContext;

        Loaded += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            Reload();
        };

        DetachedFromVisualTree += (_, _) => _tipDismissed = false;
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);

        HubAiEntry.IsVisible = App.Settings.Current.HubAiEnabled;

        // 从别的页面切回来时也重读一遍：这些配置可能刚在系统属性里改过
        if (_initialized) Reload();
    }

    // ── 读取 ─────────────────────────────────────────────────────────────

    private void Reload()
    {
        _loading = true;
        try
        {
            _drives = PageFileService.Drives();
            _autoCurrent = PageFileService.IsAutomatic();
            _autoPending = _autoCurrent;
            _autoChanged = false;
            _pending.Clear();

            SwAutomatic.IsChecked = _autoCurrent;

            EmptyHint.IsVisible = _drives.Count == 0;

            SumFloor.Text = $"{PageFileRules.MinAllowedMb} MB";

            var allocated = _drives.Sum(d => d.FileBytes);
            SumAllocated.Text = allocated > 0 ? AppLog.Size(allocated) : "无";

            // 默认选中系统盘：用户十有八九是来改它的
            _selected = _drives.FirstOrDefault(d => d.Drive == PageFileService.SystemDrive)?.Drive
                        ?? _drives.FirstOrDefault()?.Drive;

            RefreshRows();
            UpdateEditability();

            AppLog.Step("pagefile", $"读取：自动管理={_autoCurrent}，驱动器 {_drives.Count} 个（"
                + string.Join("、", _drives.Select(d => $"{d.Drive}={d.Plan.SizeText}")) + "）");
        }
        finally
        {
            _loading = false;
        }

        UpdatePendingUi();
    }

    /// <summary>重建列表并恢复选中，然后回填右侧选项区。</summary>
    private void RefreshRows()
    {
        BuildDriveRows();

        _syncing = true;
        try
        {
            var index = _selected is null ? -1 : _drives.FindIndex(d => d.Drive == _selected);
            DriveList.SelectedIndex = index;
        }
        finally
        {
            _syncing = false;
        }

        FillEditor(_selected);
    }

    /// <summary>每行是一个 Grid（盘符与卷标 / 可用空间 / 目标大小）。</summary>
    private void BuildDriveRows()
    {
        DriveList.Items.Clear();
        _rowSizeText.Clear();

        foreach (var drive in _drives)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,110"),
                Margin = new Thickness(0, 6, 0, 6),
            };
            row.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            row.ColumnDefinitions[1].Width = GridLength.Auto;
            row.ColumnDefinitions[2].Width = new GridLength(110);

            var name = new TextBlock
            {
                Text = drive.Display,
                FontSize = 13.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var free = new TextBlock
            {
                Text = drive.FreeText,
                FontSize = 12,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 16, 0),
            };
            Grid.SetColumn(free, 1);

            // 右列显示**目标**大小（有待应用改动时就是改动后的），而不是当前的
            var target = _pending.TryGetValue(drive.Drive, out var plan) ? plan : drive.Plan;

            var size = new TextBlock
            {
                Text = target.SizeText,
                FontSize = 13,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(size, 2);

            row.Children.Add(name);
            row.Children.Add(free);
            row.Children.Add(size);

            row.Tag = drive.Drive;
            DriveList.Items.Add(row);

            _rowSizeText[drive.Drive] = size;
        }
    }

    /// <summary>
    /// 只把某一行的右列文字换掉。
    /// <para>
    /// ⚠️ 不能在改数字时重建整张列表：重建会连带 <see cref="FillEditor"/> 把数字框重新赋值，
    /// 用户正在输入的光标位置会被重置（打「14336」打到一半跳回去）。所以这时只改一个 <c>TextBlock</c>。
    /// </para>
    /// </summary>
    private void UpdateRowSize(string driveId)
    {
        if (!_rowSizeText.TryGetValue(driveId, out var text)) return;

        var drive = _drives.FirstOrDefault(d => d.Drive == driveId);
        if (drive is null) return;

        text.Text = (_pending.TryGetValue(driveId, out var plan) ? plan : drive.Plan).SizeText;
    }

    /// <summary>把某个驱动器的目标配置回填到选项区。</summary>
    private void FillEditor(string? driveId)
    {
        var drive = _drives.FirstOrDefault(d => d.Drive == driveId);

        if (drive is null)
        {
            SelTitle.Text = "未选择驱动器";
            SelSub.Text = "";
            UpdateEditorEnabled();
            return;
        }

        var plan = _pending.TryGetValue(drive.Drive, out var target) ? target : drive.Plan;

        _loading = true;
        try
        {
            SelTitle.Text = $"所选驱动器：{drive.Display}";

            var parts = new List<string> { drive.FreeText, $"共 {drive.TotalMb:N0} MB" };
            if (drive.FileText.Length > 0) parts.Add($"当前文件 {drive.FileText}");
            SelSub.Text = string.Join(" · ", parts);

            RbCustom.IsChecked = plan.Mode == PageFileMode.Custom;
            RbSystem.IsChecked = plan.Mode == PageFileMode.SystemManaged;
            RbNone.IsChecked = plan.Mode == PageFileMode.None;

            // 切到「自定义」时给一对有依据的初值：已有配置就沿用，没有就按可用空间取一档
            NumMin.Value = plan.Mode == PageFileMode.Custom ? plan.MinMb : SuggestMin(drive);
            NumMax.Value = plan.Mode == PageFileMode.Custom ? plan.MaxMb : SuggestMax(drive);
        }
        finally
        {
            _loading = false;
        }

        UpdateEditorEnabled();
        ShowShapeHint(null, isError: false);
    }

    /// <summary>选项区能不能改（自动管理开着时，逐盘设置由系统接管）。</summary>
    private void UpdateEditorEnabled()
    {
        var editable = SwAutomatic.IsChecked != true && _drives.Count > 0;

        RbCustom.IsEnabled = editable;
        RbSystem.IsEnabled = editable;
        RbNone.IsEnabled = editable;

        var custom = editable && RbCustom.IsChecked == true;
        NumMin.IsEnabled = custom;
        NumMax.IsEnabled = custom;
    }

    /// <summary>
    /// 没有现成配置时的初始大小。取可用空间的 1/8，卡在 512 ~ 4096 之间 ——
    /// 只是个起点，用户可以随手改，⛔ 不是"推荐值"（推荐值怎么算各家说法不一，不在这里编）。
    /// </summary>
    private static int SuggestMin(PageFileDrive drive) =>
        Math.Clamp(drive.UsableMb / 8, 512, 4096);

    private static int SuggestMax(PageFileDrive drive) =>
        Math.Clamp(drive.UsableMb / 4, 1024, 8192);

    // ── 交互 ─────────────────────────────────────────────────────────────

    private void Automatic_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        _autoPending = SwAutomatic.IsChecked == true;
        _autoChanged = _autoPending != _autoCurrent;

        // 自动管理由系统全权决定各盘，逐盘的手动改动不再有意义
        if (_autoPending) _pending.Clear();

        UpdateEditability();
        RefreshRows();

        AppLog.Step("pagefile", _autoChanged
            ? $"排队：自动管理 = {(_autoPending ? "开" : "关")}"
            : "自动管理回到原状态");

        _tipDismissed = false;
        _failedMode = false;
        UpdatePendingUi();
    }

    private void DriveList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        if (DriveList.SelectedItem is Control { Tag: string drive })
        {
            _selected = drive;
            FillEditor(drive);
        }
        else
        {
            FillEditor(null);
        }
    }

    private void Mode_Checked(object? sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        // 勾到「自定义」而数字框还是空的：先把建议值填上，否则会立刻报"不能小于 16"
        var drive = _drives.FirstOrDefault(d => d.Drive == _selected);
        if (drive is not null && RbCustom.IsChecked == true && IsBlank(NumMin))
        {
            _loading = true;
            NumMin.Value = SuggestMin(drive);
            NumMax.Value = SuggestMax(drive);
            _loading = false;
        }

        UpdateEditorEnabled();
        QueueFromEditor();
    }

    private void Number_Changed(object? sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _busy || _syncing) return;

        // 清空数字框时 Value 是 NaN，此时不要拿 0 去覆盖用户正在输入的内容
        if (double.IsNaN(args.NewValue)) return;

        QueueFromEditor();
    }

    private static bool IsBlank(NumberBox box) => box.Value is double v && double.IsNaN(v);

    private static int IntOf(NumberBox box) =>
        box.Value is double v && !double.IsNaN(v) ? (int)Math.Round(v) : 0;

    /// <summary>把选项区当前的选择写成一条目标配置（校验不过就只提示、不入队）。</summary>
    private void QueueFromEditor()
    {
        var drive = _drives.FirstOrDefault(d => d.Drive == _selected);
        if (drive is null) return;

        // 自动管理开着时，逐盘设置会被系统覆盖，不让它进队列
        if (SwAutomatic.IsChecked == true) return;

        var mode = RbNone.IsChecked == true ? PageFileMode.None
                 : RbSystem.IsChecked == true ? PageFileMode.SystemManaged
                 : PageFileMode.Custom;

        var plan = new PageFilePlan(drive.Drive, mode, IntOf(NumMin), IntOf(NumMax));

        var error = PageFileRules.Validate(plan, drive.UsableMb);
        if (error is not null)
        {
            // ⚠️ 校验不过**不排队**，但要把原因当场显示出来 —— 否则用户以为设好了，
            //    等重启之后才发现还是原来的配置。
            ShowShapeHint(error, isError: true);
            return;
        }

        if (PageFileRules.SameAs(new[] { plan }, new[] { drive.Plan })) _pending.Remove(drive.Drive);
        else _pending[drive.Drive] = plan;

        AppLog.Step("pagefile", _pending.ContainsKey(drive.Drive)
            ? $"排队：{drive.Drive} = {plan.SizeText}"
            : $"取消排队：{drive.Drive}（与当前配置一致）");

        ShowShapeHint(null, isError: false);

        // 只换这一行的右列文字，不重建列表（见 UpdateRowSize 的注释）
        UpdateRowSize(drive.Drive);

        _tipDismissed = false;
        _failedMode = false;
        UpdatePendingUi();
    }

    private void ShowShapeHint(string? text, bool isError)
    {
        if (string.IsNullOrEmpty(text))
        {
            ShapeHint.IsVisible = false;
            return;
        }

        ShapeHint.Text = text;
        ShapeHint.IsVisible = true;
        ShapeHint.Foreground = isError
            ? ThemeBrush.Get(this, "SystemFillColorCriticalBrush")
            : ThemeBrush.Get(this, "TextFillColorSecondaryBrush");
    }

    private void UpdateEditability()
    {
        // 自动管理开着时，逐盘设置由系统接管：控件走自带的禁用视觉
        DriveList.IsEnabled = SwAutomatic.IsChecked != true;
        UpdateEditorEnabled();
    }

    // ── 待应用与提示条 ───────────────────────────────────────────────────

    private int PendingCount => (_autoChanged ? 1 : 0) + (_autoPending ? 0 : _pending.Count);

    private void UpdatePendingUi()
    {
        var count = PendingCount;

        if (count == 0)
        {
            PendingBar.IsVisible = false;
            _failedMode = false;
            _tipDismissed = false;
            return;
        }

        BuildPendingList();

        PendingTitle.Text = _failedMode
            ? "虚拟内存设置没有生效"
            : $"有 {count} 项待应用";
        PendingSubtitle.Text = _failedMode
            ? "可能被组策略或安全软件拦下，可以再试一次，或逐项取消。"
            : "应用时请求一次管理员授权，重启后生效。";
        ApplyButton.Content = _failedMode ? "重试" : "应用更改";

        // 用户点过「暂不应用」：收起整条提示，直到下一次改动
        PendingBar.IsVisible = !_tipDismissed;
    }

    /// <summary>清单：每行「对象 → 目标值」+ 一颗「取消」。</summary>
    private void BuildPendingList()
    {
        PendingList.Children.Clear();

        if (_autoChanged)
            AddPendingRow("自动管理所有驱动器的分页文件大小",
                _autoPending ? "开启" : "关闭",
                cancel: null);

        if (_autoPending) return;

        foreach (var (drive, plan) in _pending)
            AddPendingRow(drive, plan.SizeText, cancel: drive);
    }

    private void AddPendingRow(string title, string value, string? cancel)
    {
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
        };
        var inlines = text.Inlines!;
        inlines.Add(new Avalonia.Controls.Documents.Run { Text = title });
        inlines.Add(new Avalonia.Controls.Documents.Run
        {
            Text = "  →  " + value,
            Foreground = ThemeBrush.Get(this, "TextFillColorSecondaryBrush"),
        });

        row.Children.Add(text);

        if (cancel is not null)
        {
            var button = new Button
            {
                Content = "取消",
                Tag = cancel,
                MinWidth = 0,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            button.Click += CancelPending_Click;

            Grid.SetColumn(button, 1);
            row.Children.Add(button);
        }

        PendingList.Children.Add(row);
    }

    private void CancelPending_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string driveId }) return;

        _pending.Remove(driveId);

        // 选项区要拨回真实配置，否则它停在一个"看着改了、其实不会写入"的位置
        if (_selected == driveId) FillEditor(driveId);
        UpdateRowSize(driveId);

        AppLog.Step("pagefile", $"取消排队：{driveId}");
        UpdatePendingUi();
    }

    private void DismissPending_Click(object? sender, RoutedEventArgs e)
    {
        // 只是收起来，队列原样保留
        _tipDismissed = true;
        PendingBar.IsVisible = false;
    }

    private async void ApplyPending_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ApplyPendingAsync();
        }
        catch (Exception ex)
        {
            // async void 里漏出去的异常会直接终止进程 —— 兜到底
            AppLog.Exception("pagefile", ex, "应用更改");
            ShowNotice("应用失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    // ── 应用 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 提交时用的完整配置：界面上每个盘的当前选择，**加上界面上看不到的盘**。
    /// <para>
    /// ⚠️ 后半句是关键：<c>PagingFiles</c> 是整体覆盖写的。如果某台机器配了一个当前没挂上的盘
    /// （U 盘位、没分配盘符的卷），不带回去就等于替用户删掉了那一行配置。
    /// </para>
    /// </summary>
    private List<PageFilePlan> BuildTargetPlans()
    {
        var plans = _drives
            .Select(d => _pending.TryGetValue(d.Drive, out var plan) ? plan : d.Plan)
            .ToList();

        plans.AddRange(PageFileService.OrphanPlans());

        return plans;
    }

    private async Task ApplyPendingAsync()
    {
        if (_busy || PendingCount == 0) return;
        if (_dialogOpen) return;

        var plans = BuildTargetPlans();

        // 与服务层同一套校验：越界的大小写进去之后，Windows 不会报错，只是这个盘实际没有分页文件
        if (!_autoPending)
        {
            if (PageFileRules.ValidateAll(plans) is { } none)
            {
                ShowNotice(none, InfoBarSeverity.Warning);
                return;
            }

            foreach (var plan in plans)
            {
                var free = _drives.FirstOrDefault(d => d.Drive == plan.Drive)?.UsableMb ?? 0;
                if (PageFileRules.Validate(plan, free) is { } error)
                {
                    ShowNotice($"{plan.Drive} {error}", InfoBarSeverity.Warning);
                    return;
                }
            }
        }

        var summary = _autoPending
            ? "改为由系统自动管理所有驱动器的分页文件大小。"
            : string.Join("\n", plans
                .Where(p => p.Mode != PageFileMode.None)
                .Select(p => $"{p.Drive}  {p.SizeText}"));

        if (!await ConfirmAsync("应用虚拟内存设置",
                $"{summary}\n\n需要一次管理员授权。改动在重启之后生效。"))
            return;

        _busy = true;
        PendingBar.IsVisible = false;

        try
        {
            var result = await PageFileService.ApplyAsync(_autoPending, plans);

            if (!result.Ok)
            {
                ShowNotice(result.Error ?? "应用失败。", InfoBarSeverity.Warning);
                _failedMode = true;
                return;
            }

            _pending.Clear();
            _autoCurrent = _autoPending;
            _autoChanged = false;

            Reload();
            ShowNotice("已应用，重启后生效。", InfoBarSeverity.Success);
        }
        finally
        {
            // ⚠️ 这里**只**放开忙碌标记与刷新提示条，不重读。
            //    重读会把 _pending 清空 —— 而失败分支特意保留了队列好让用户直接重试。
            _busy = false;
            UpdatePendingUi();
        }
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
            AppLog.Warn("pagefile", "确认框没能弹出：" + ex.Message);
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

    private void HubAiEntry_Click(object? sender, RoutedEventArgs e) => AiFlyout.Toggle();

    /// <summary>
    /// 给浮层准备这一页的现场数据。
    /// <para>
    /// 分页文件是少数「改错了不会立刻报错、但会以别的症状出现」的设置，所以把每个盘的
    /// 可用空间、当前配置、待应用目标都摆给模型 —— 它才能答"这盘够不够""我该设多大"。
    /// </para>
    /// </summary>
    private PageContextDoc BuildAiContext()
    {
        var doc = new PageContextDoc(
            "虚拟内存",
            "这一页管理的是分页文件（虚拟内存），配置存在 HKLM，需要管理员授权，**重启后才生效**。" +
            "回答时按下面的实际数据说话，直接点名是哪个盘；不要给与本页无关的建议。" +
            "⚠️ 你无法替用户改设置，也不要说「已经帮你改好了」—— 改动只能由用户自己选驱动器、" +
            "改选项、再点右下角提示条的主按钮完成。");

        doc.Add("状态", new
        {
            自动管理 = _autoCurrent,
            目标自动管理 = _autoChanged ? _autoPending : (bool?)null,
            待应用项数 = PendingCount,
            正在应用 = _busy,
            允许的最小值MB = PageFileRules.MinAllowedMb,
            当前已分配 = PageFileService.AllocatedBytes() is var b && b > 0 ? AppLog.Size(b) : "无",
        });

        doc.Add("驱动器", _drives.Select(d => new
        {
            d.Drive,
            卷标 = d.Label,
            可用MB = d.FreeMb,
            可用于分页文件MB = d.UsableMb,
            总容量MB = d.TotalMb,
            当前设置 = d.Plan.SizeText,
            当前初始大小MB = d.Plan.MinMb,
            当前最大值MB = d.Plan.MaxMb,
            当前文件大小 = d.FileText,
            待应用目标 = _pending.TryGetValue(d.Drive, out var t) ? t.SizeText : null,
        }).ToList());

        doc.Add("界面上看不到但配置里存在的驱动器",
            PageFileService.OrphanPlans().Select(p => new { p.Drive, 设置 = p.SizeText }).ToList());

        doc.Add("本页的硬规则", new
        {
            大小下限 = "16 MB",
            上限 = "不能超过该盘可用空间，否则分页文件建不出来，而系统不会报错",
            至少一个 = "不允许所有驱动器都设为「无分页文件」",
            生效时机 = "重启",
            三种设置的含义 = new
            {
                自定义大小 = "固定占用，初始值立即分配、最大值为上限",
                系统管理的大小 = "由系统按需要伸缩，注册表里写作 0 0",
                无分页文件 = "这个盘不放分页文件（注册表里整条不写，不是写 0）",
            },
        });

        doc.Add("界面怎么操作", new
        {
            选驱动器 = "点列表里的一行",
            改这一盘的设置 = "在下方三选一，选「自定义大小」时填初始与最大",
            应用 = "右下角提示条的主按钮，一次授权",
            单项取消 = "提示条清单里每行右侧的「取消」",
        });

        return doc;
    }
}
