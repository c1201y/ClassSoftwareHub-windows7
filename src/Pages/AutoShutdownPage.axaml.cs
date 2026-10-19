using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「自动关机」页（实验性功能，tag = <c>autoshutdown</c>；自上游 dv1.1.3 移植）。
///
/// 上半是总开关，中间是按星期排的时间表（周一~周日一列一天），
/// 下半是「把取消关机加到侧边栏」的快捷入口 + 限制说明。
///
/// ⚠️ 页面只负责**改配置**，真正到点执行在 <see cref="Data.AutoShutdown"/> 的定时巡检里；
///    这里绝不自己发关机命令（改完存盘即可，引擎每次 Tick 都重新读配置）。
///
/// ⚠️ Win7 移植说明：WinUI 的 <c>NumberBox</c>（Inline 步进）换成 Avalonia 的
///    <c>NumericUpDown</c>；<c>ToolTipService.SetToolTip</c> 换成 <c>ToolTip.Tip</c>；
///    <c>ContentDialog</c> 不带 XamlRoot（FluentAvalonia 直接 ShowAsync）。
/// </summary>
public sealed partial class AutoShutdownPage : PageBase
{
    /// <summary>侧边栏那颗「取消关机」的模块 id（与 Data/SidebarModule.cs 一致）。</summary>
    private const string CancelModuleId = "cancelshutdown";

    private AutoShutdownConfig _config = new();

    /// <summary>程序化改控件值时用它挡住 IsCheckedChanged，别把"回显"当成"用户改的"。</summary>
    private bool _loading;

    public AutoShutdownPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Reload();
    }

    // ── 载入 / 回显 ─────────────────────────────────────────

    private void Reload()
    {
        _config = AutoShutdownConfig.Load();

        _loading = true;
        EnableSwitch.IsChecked = _config.Enabled;
        _loading = false;

        ShowScheduleHint("");
        BuildSchedule();
        UpdateHints();
    }

    private void UpdateHints()
    {
        var today = AutoShutdownConfig.DayIndexOf(DateTime.Now);
        var count = _config.Days[today].Count;

        SwitchHint.Text = _config.Enabled
            ? $"已启用。今天（{AutoShutdownConfig.DayNames[today]}）有 {count} 个关机点，到点会按设定执行。"
            : "未启用 —— 打开后才会按下面的时间表执行。";

        SidebarHint.Text = HasCancelModule()
            ? "「取消关机」已在侧边栏上，可以在「侧边布局」里调整它的位置。"
            : "还没把「取消关机」加到侧边栏。";
    }

    private static bool HasCancelModule() =>
        (App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>()).Contains(CancelModuleId);

    // ── 时间表 ──────────────────────────────────────────────

    private void BuildSchedule()
    {
        ScheduleGrid.Children.Clear();
        ScheduleGrid.ColumnDefinitions.Clear();

        for (var day = 0; day < 7; day++)
        {
            ScheduleGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var column = new StackPanel { Spacing = 6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };

            column.Children.Add(new TextBlock
            {
                Text = AutoShutdownConfig.DayNames[day],
                FontSize = 12,
                Opacity = 0.7,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            });

            var points = _config.Days[day];
            for (var index = 0; index < points.Count; index++)
                column.Children.Add(BuildPointButton(day, index, points[index]));

            // 列底：加号 + 它右边那颗「套用前一天」。
            // ⚠️ 两颗并排放在一个横向 StackPanel 里（Nick 2026-10-12 指明"在加号右边再加一个"）。
            var actions = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            actions.Children.Add(BuildAddButton(day));
            actions.Children.Add(BuildCopyPrevButton(day));
            column.Children.Add(actions);

            Grid.SetColumn(column, day);
            ScheduleGrid.Children.Add(column);
        }
    }

    /// <summary>一颗关机点按钮：上行是时间，下行是方式（+延迟）。</summary>
    private Button BuildPointButton(int dayIndex, int pointIndex, ShutdownPoint point)
    {
        var caption = AutoShutdownConfig.NormalizeMethod(point.Method) == ShutdownMethods.Slide
            ? "滑动关机"
            : $"直接关机 · {point.DelaySeconds}s";

        var content = new StackPanel { Spacing = 1 };
        content.Children.Add(new TextBlock { Text = point.Time, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = caption, FontSize = 11, Opacity = 0.6 });

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Padding = new Thickness(10, 6, 10, 6),
        };
        ToolTip.SetTip(button, "点一下修改这个关机点，或者删掉它");

        var day = dayIndex;
        var index = pointIndex;
        button.Click += async (_, _) => await EditPointAsync(day, index);
        return button;
    }

    /// <summary>那一列的「+」。</summary>
    private Button BuildAddButton(int dayIndex)
    {
        var button = new Button
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(0),
            Width = 30,
            Height = 30,
            Content = new FontIcon { Glyph = "\uE710", FontSize = 12 },
        };
        ToolTip.SetTip(button, $"给{AutoShutdownConfig.DayNames[dayIndex]}加一个关机点");

        var day = dayIndex;
        button.Click += async (_, _) => await AddPointAsync(day);
        return button;
    }

    /// <summary>
    /// 加号右边那颗「套用前一天」：把**前一天**那一列整列拷到本列（周一取周日）。
    /// （2026-10-18 自上游 1.2.0 移植。）
    ///
    /// ⚠️ 语义是**覆盖**不是追加 —— "套用"就是把这一天排成前一天的样子；
    ///    本列已有关机点时先弹一句确认，不做无声覆盖。
    /// ⚠️ 前一天是空的就只提示、不动手 —— 否则"套用"会退化成"把本列清空"，那不是用户要的。
    /// </summary>
    private Button BuildCopyPrevButton(int dayIndex)
    {
        var prev = PreviousDay(dayIndex);

        var button = new Button
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(0),
            Width = 30,
            Height = 30,
            // E8C8（Copy）：MDL2 老码位，Win10 也有字形
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
        };
        ToolTip.SetTip(button,
            $"把{AutoShutdownConfig.DayNames[prev]}的关机点套用到{AutoShutdownConfig.DayNames[dayIndex]}（本列原有内容会被替换）");

        var day = dayIndex;
        button.Click += async (_, _) => await CopyPreviousDayAsync(day);
        return button;
    }

    /// <summary>本列的"前一天"（周一取周日）。</summary>
    private static int PreviousDay(int dayIndex) => (dayIndex + 6) % 7;

    private async Task CopyPreviousDayAsync(int dayIndex)
    {
        var prev = PreviousDay(dayIndex);
        var source = _config.Days[prev];
        var target = _config.Days[dayIndex];

        if (source.Count == 0)
        {
            ShowScheduleHint($"{AutoShutdownConfig.DayNames[prev]}还没有关机点，没有可以套用的内容。");
            return;
        }

        if (target.Count > 0)
        {
            // ⚠️ 原版对话框带 XamlRoot；FluentAvalonia 的 ContentDialog 没有 XamlRoot，直接 ShowAsync()。
            var confirm = new ContentDialog
            {
                Title = "套用前一天的关机点",
                Content = $"{AutoShutdownConfig.DayNames[dayIndex]}现在有 {target.Count} 个关机点，"
                          + $"套用{AutoShutdownConfig.DayNames[prev]}的 {source.Count} 个会把它们全部替换掉。",
                PrimaryButtonText = "替换",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        // ⚠️ 必须**深拷**：直接把同一批对象塞过去，之后改其中一天会连带改到另一天
        target.Clear();
        foreach (var p in source)
            target.Add(new ShutdownPoint
            {
                Time = p.Time,
                Method = p.Method,
                DelaySeconds = p.DelaySeconds,
            });

        SaveAndRefresh();
        ShowScheduleHint(
            $"已把{AutoShutdownConfig.DayNames[prev]}的 {source.Count} 个关机点套用到{AutoShutdownConfig.DayNames[dayIndex]}。");
    }

    /// <summary>时间表那块自己的提示行（套用结果 / 没什么可套用）。</summary>
    private void ShowScheduleHint(string text)
    {
        if (ScheduleHint is not null) ScheduleHint.Text = text;
    }

    // ── 增 / 改 / 删 ────────────────────────────────────────

    private async Task AddPointAsync(int dayIndex)
    {
        // 新点的默认值：**上次填过的那个**（沿用上次设置）；没有历史就放学时段 + 直接关机 + 一分钟延迟。
        // （2026-10-18 自上游 1.2.0 移植：Nick「一个一个配置太麻烦了，如果上次设置的啥，这次可以直接套用」。）
        var draft = _config.LastPoint is { } last
            ? new ShutdownPoint
            {
                Time = last.Time,
                Method = last.Method,
                DelaySeconds = last.DelaySeconds,
            }
            : new ShutdownPoint
            {
                Time = "17:30",
                Method = ShutdownMethods.Shutdown,
                DelaySeconds = 60,
            };

        if (await ShowPointDialogCoreAsync(draft, isNew: true) != PointDialogOutcome.Save) return;

        _config.Days[dayIndex].Add(draft);
        SaveAndRefresh();
    }

    private async Task EditPointAsync(int dayIndex, int pointIndex)
    {
        var current = _config.Days[dayIndex][pointIndex];

        // ⚠️ 先拷一份草稿进去编辑，用户点「取消」时原对象一个字段都不该被改过
        var draft = new ShutdownPoint
        {
            Time = current.Time,
            Method = current.Method,
            DelaySeconds = current.DelaySeconds,
        };

        var outcome = await ShowPointDialogCoreAsync(draft, isNew: false);

        switch (outcome)
        {
            case PointDialogOutcome.Save:
                current.Time = draft.Time;
                current.Method = draft.Method;
                current.DelaySeconds = draft.DelaySeconds;
                SaveAndRefresh();
                break;

            case PointDialogOutcome.Delete:
                _config.Days[dayIndex].RemoveAt(pointIndex);
                SaveAndRefresh();
                break;

            // Cancel：草稿丢掉，原样不动
        }
    }

    private void SaveAndRefresh()
    {
        // 「沿用上次设置」的落点：把这次保存的点记成下一次新增时的默认值。
        // （2026-10-18 自上游 1.2.0 移植：只是输入助手，不参与执行/巡检。）
        var latest = _config.Days.SelectMany(d => d).LastOrDefault();
        if (latest is not null)
        {
            _config.LastPoint = new ShutdownPoint
            {
                Time = latest.Time,
                Method = latest.Method,
                DelaySeconds = latest.DelaySeconds,
            };
        }

        _config.Save();
        BuildSchedule();
        UpdateHints();
    }

    // ── 关机点配置弹层 ──────────────────────────────────────

    private enum PointDialogOutcome { Save, Delete, Cancel }

    /// <summary>弹配置框。返回值 =「保存 / 删除 / 取消」（删除只在编辑既有时间点时才有这个键）。</summary>
    private async Task<PointDialogOutcome> ShowPointDialogCoreAsync(ShutdownPoint draft, bool isNew)
    {
        var (hour, minute) = SplitTime(draft.Time);

        // ⚠️ 时间用两个 NumericUpDown（时 / 分），不用 TimePicker：
        //    TimePicker 三列默认宽度大（塞进对话框会挤），弹出列表还会朝上盖住上面的内容。
        //    （上游用 NumberBox + Inline 步进并注释了宽度坑；Avalonia 的 NumericUpDown
        //      的步进按钮在右侧常驻，没有"获焦插清除键"的问题，宽度给 120 就够。）
        var hourBox = new NumericUpDown
        {
            Value = new decimal(hour), Minimum = 0, Maximum = 23, Increment = 1,
            AllowSpin = true, Width = 120, MinWidth = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FormatString = "0",
        };
        var minuteBox = new NumericUpDown
        {
            Value = new decimal(minute), Minimum = 0, Maximum = 59, Increment = 1,
            AllowSpin = true, Width = 120, MinWidth = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FormatString = "0",
        };

        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        timeRow.Children.Add(hourBox);
        timeRow.Children.Add(new TextBlock { Text = "时", FontSize = 12, Opacity = 0.8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        timeRow.Children.Add(minuteBox);
        timeRow.Children.Add(new TextBlock { Text = "分", FontSize = 12, Opacity = 0.8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });

        // 方式：两颗原生 RadioButton（⚠️ MinWidth 必须清零，默认 120 会把这一行撑开）
        var radioShutdown = new RadioButton
        {
            Content = "直接关机", GroupName = "ShutdownMethod", MinWidth = 0,
            IsChecked = AutoShutdownConfig.NormalizeMethod(draft.Method) == ShutdownMethods.Shutdown,
        };
        var radioSlide = new RadioButton
        {
            Content = "滑动关机", GroupName = "ShutdownMethod", MinWidth = 0,
            IsChecked = AutoShutdownConfig.NormalizeMethod(draft.Method) == ShutdownMethods.Slide,
        };

        var methodRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        methodRow.Children.Add(radioShutdown);
        methodRow.Children.Add(radioSlide);

        // 延迟：只有「直接关机」用得上
        var delayBox = new NumericUpDown
        {
            Value = new decimal(draft.DelaySeconds), Minimum = 0, Maximum = 86400, Increment = 10,
            AllowSpin = true, Width = 150, MinWidth = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FormatString = "0",
        };
        var delayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        delayRow.Children.Add(delayBox);
        delayRow.Children.Add(new TextBlock
        {
            Text = "秒后关机（0 = 立刻）", FontSize = 12, Opacity = 0.8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        var delaySection = new StackPanel { Spacing = 6 };
        delaySection.Children.Add(new TextBlock { Text = "延迟", FontSize = 12, Opacity = 0.6 });
        delaySection.Children.Add(delayRow);

        void SyncDelayVisibility() =>
            delaySection.IsVisible = radioShutdown.IsChecked == true;

        // ⚠️ 事件必须在控件都建好之后再挂 —— 初始化 IsChecked 时 Checked 就会触发一次
        radioShutdown.Checked += (_, _) => SyncDelayVisibility();
        radioSlide.Checked += (_, _) => SyncDelayVisibility();
        SyncDelayVisibility();

        // 内容宽 380：时间行 = 120 + 8 +「时」+ 8 + 120 + 8 +「分」≈ 300，380 留了余量
        var root = new StackPanel { Spacing = 16, Width = 380 };
        root.Children.Add(Section("时间", timeRow));
        root.Children.Add(Section("方式", methodRow));
        root.Children.Add(delaySection);

        var dialog = new ContentDialog
        {
            Title = isNew ? "添加关机点" : "关机点",
            Content = root,
            PrimaryButtonText = "保存",
            SecondaryButtonText = isNew ? "" : "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return PointDialogOutcome.Cancel;

        if (result == ContentDialogResult.Secondary) return PointDialogOutcome.Delete;

        // 保存：把控件里的值写回草稿（越界退回兜底值）
        draft.Time = $"{ReadNumber(hourBox, hour, 0, 23):00}:{ReadNumber(minuteBox, minute, 0, 59):00}";
        draft.Method = radioSlide.IsChecked == true ? ShutdownMethods.Slide : ShutdownMethods.Shutdown;
        draft.DelaySeconds = ReadNumber(delayBox, draft.DelaySeconds, 0, 86400);
        return PointDialogOutcome.Save;
    }

    private static StackPanel Section(string label, Control control)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.6 });
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>NumericUpDown 取整数：空值（用户清空过）和越界都退回兜底值。</summary>
    private static int ReadNumber(NumericUpDown box, int fallback, int min, int max)
    {
        var value = box.Value;                       // decimal?
        if (value is null) return fallback;
        return Math.Clamp((int)Math.Round(value.Value), min, max);
    }

    /// <summary>"HH:mm" → (时, 分)；认不出就给 17:30。</summary>
    private static (int Hour, int Minute) SplitTime(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)
            && TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", null, out var span))
            return (span.Hours, span.Minutes);

        return (17, 30);
    }

    // ── 顶部开关 / 侧边栏 ───────────────────────────────────

    private void EnableSwitch_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _config.Enabled = EnableSwitch.IsChecked == true;
        _config.Save();
        UpdateHints();
    }

    private void AddToSidebar_Click(object sender, RoutedEventArgs e)
    {
        var ids = (App.Settings.Current.SidebarModuleIds ?? Array.Empty<string>()).ToList();

        if (!ids.Contains(CancelModuleId))
        {
            ids.Add(CancelModuleId);                       // 追加到末尾
            App.Settings.Current.SidebarModuleIds = ids.ToArray();
            App.Settings.Save();
            Views.ToolSidebarWindow.ApplyModules();        // 真侧边栏立刻重建
        }

        SidebarHint.Text = App.Settings.Current.SidebarEnabled
            ? "已添加到侧边栏末尾。可以在「侧边布局」里调整它的位置。"
            : "已添加到侧边栏末尾，但侧边栏当前是隐藏的 —— 去「侧边布局」把它显示出来。";
    }

    private void OpenSidebarLayout_Click(object sender, RoutedEventArgs e)
        => App.MainWindow?.OpenToolSettings(typeof(SidebarLayoutPage));
}
