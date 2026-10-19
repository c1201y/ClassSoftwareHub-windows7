using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「开机启动项」列表里的一行。
/// ⚠️ 与 <see cref="StartupPage"/> 同文件 —— 本项目的行类型都跟页面放一起。
/// </summary>
public sealed class StartupRow
{
    private bool _enabled;
    private bool _busy;

    private StartupRow(StartupItem item)
    {
        Item = item;
        _enabled = item.Enabled;
    }

    public static StartupRow From(StartupItem item) => new(item);

    public StartupItem Item { get; }

    public string Name => Item.Name;

    /// <summary>⚠️ 只认 E7/E8/E9 老码位的字形 —— ED 那段是新版图标字体，Win10 上会显示空框。</summary>
    public const string RowGlyph = "\uE945";

    public string Glyph => RowGlyph;

    public string SourceLabel => Item.SourceLabel;

    public string Sub => Item.Missing ? Item.Command + "　（找不到对应文件）" : Item.Command;

    public string SubTooltip => Item.Missing
        ? Item.Command + "\n\n命令行指向的文件已经不存在，多半是软件卸载后留下的死项。"
        : Item.Command;

    /// <summary>该项当前是否处于"开机自启"状态。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
        }
    }

    /// <summary>正在切换 —— 挡住重复操作，同时让开关自己变灰。</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
        }
    }

    public bool CanToggle => !_busy;
}

/// <summary>
/// 开机启动项：把"开机时会自己启动的东西"列全，并允许逐个关掉。
/// 实现细节（状态位怎么写的、为什么不删值）见 <see cref="StartupItems"/> 的类注释。
/// （2026-10-18 自上游 1.2.0 移植到 Avalonia。）
/// </summary>
public partial class StartupPage : Platform.PageBase
{
    private List<StartupRow> _rows = new();
    private CancellationTokenSource? _cts;
    private bool _ready;
    private bool _busy;
    private bool _dialogOpen;

    /// <summary>行对象 → 本行的开关（改状态时同步控件）。</summary>
    private readonly Dictionary<StartupRow, ToggleSwitch> _toggles = new();

    /// <summary>行对象 → 「已禁用」标签。</summary>
    private readonly Dictionary<StartupRow, Border> _badges = new();

    public StartupPage()
    {
        InitializeComponent();

        // 构造期控件还没进可视化树，扫描放挂树（与其它页一致）
        AttachedToVisualTree += (_, _) =>
        {
            if (!_ready && _cts is null) _ = ScanAsync();
        };

        // 离开页面就掐掉扫描
        DetachedFromVisualTree += (_, _) => CancelScan();
    }

    private void CancelScan()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;

        try
        {
            cts.Cancel();
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "取消扫描开机启动项");
        }
    }

    // ── 扫描 ──────────────────────────────────────────────────────────

    private async void Rescan_Click(object? sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        CancelScan();
        var cts = new CancellationTokenSource();
        _cts = cts;

        _busy = true;
        _ready = false;

        LoadingPanel.IsVisible = true;
        EmptyPanel.IsVisible = false;
        EntryListPanel.Children.Clear();
        _toggles.Clear();
        _badges.Clear();
        CountLine.Text = "正在读取…";

        try
        {
            var items = await StartupItems.ScanAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            _rows = items.Select(StartupRow.From).ToList();

            EntryListPanel.Children.Clear();
            foreach (var row in _rows)
                EntryListPanel.Children.Add(BuildRowVisual(row));

            _ready = true;
            RenderCount();

            EmptyPanel.IsVisible = _rows.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // 页面切走了，正常
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "扫描开机启动项");

            _ready = true;
            CountLine.Text = "";
            EmptyText.Text = "读取开机启动项时出错。可以点右上角「重新扫描」再试一次。";
            EmptyPanel.IsVisible = true;
        }
        finally
        {
            LoadingPanel.IsVisible = false;
            _busy = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    private void RenderCount()
    {
        var total = _rows.Count;
        if (total == 0)
        {
            CountLine.Text = "";
            return;
        }

        var off = _rows.Count(r => !r.Enabled);
        var text = off == 0
            ? $"共 {total} 项，全部处于启用状态"
            : $"共 {total} 项，其中 {off} 项已禁用";

        var admin = _rows.Count(r => r.Item.NeedsAdmin);
        if (admin > 0) text += $"　·　{admin} 项为所有用户级，改动时需要管理员授权";

        CountLine.Text = text;
    }

    // ── 行控件 ────────────────────────────────────────────────────────

    private Control BuildRowVisual(StartupRow row)
    {
        var grid = new Grid { MinHeight = 36, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new FontIcon
        {
            Glyph = row.Glyph, FontSize = 15, Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(glyph);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 2);
        text.Margin = new Thickness(8, 0, 0, 0);

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameLine.Children.Add(new TextBlock
        {
            Text = row.Name, FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        });

        // 「已禁用」只在关着的时候出现 —— 开着不用再写一遍"已启用"，那是废话
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush.Get(this, "SystemFillColorNeutralBackgroundBrush"),
            IsVisible = !row.Enabled,
            Child = new TextBlock { Text = "已禁用", FontSize = 11, Opacity = 0.75 },
        };
        _badges[row] = badge;
        nameLine.Children.Add(badge);
        text.Children.Add(nameLine);

        // 命令行 / 快捷方式目标。⛔ 找不到对应文件是**很有用**的信息，单独写一句。
        var sub = new TextBlock
        {
            Text = row.Sub, FontSize = 12, Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        ToolTip.SetTip(sub, row.SubTooltip);
        text.Children.Add(sub);
        grid.Children.Add(text);

        var source = new TextBlock
        {
            Text = row.SourceLabel, FontSize = 11, Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(source, 3);
        source.Margin = new Thickness(12, 0, 12, 0);
        grid.Children.Add(source);

        // 行尾的启停开关（⚠️ Avalonia 的 ToggleSwitch 用 IsChecked，不是 IsOn）。
        var toggle = new ToggleSwitch
        {
            IsChecked = row.Enabled,
            MinWidth = 0,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = row.CanToggle,
        };
        ToolTip.SetTip(toggle, "开机时是否自动启动");
        toggle.IsCheckedChanged += (_, _) => OnRowToggled(toggle, row);
        _toggles[row] = toggle;
        Grid.SetColumn(toggle, 4);
        grid.Children.Add(toggle);

        return grid;
    }

    // ── 启停 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 拨动一行尾部的开关。
    /// ⚠️ 两条早退判据都不能删：① 绑定回灌；② 值没变（只是初始化）。
    /// </summary>
    private async void OnRowToggled(ToggleSwitch toggle, StartupRow row)
    {
        if (row.Busy) return;

        var want = toggle.IsChecked == true;
        if (want == row.Enabled) return;

        row.Busy = true;
        toggle.IsEnabled = false;

        try
        {
            var error = await StartupItems.SetEnabledAsync(row.Item, want, CancellationToken.None);

            if (error.Length == 0)
            {
                row.Enabled = want;
                if (_badges.TryGetValue(row, out var badge)) badge.IsVisible = !row.Enabled;
                if (_toggles.TryGetValue(row, out var t)) t.IsChecked = row.Enabled;
                RenderCount();
            }
            else
            {
                // ⛔ 必须**直接拨控件**，不能只写 `row.Enabled = !want`。
                toggle.IsChecked = !want;
                await ShowMessageAsync(want ? "启用未成功" : "禁用未成功", error);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("startup", ex, "切换开机启动项");
            toggle.IsChecked = !want;
            await ShowMessageAsync("操作未成功", ex.Message);
        }
        finally
        {
            row.Busy = false;
            toggle.IsEnabled = true;
        }
    }

    // ── 说明 ──────────────────────────────────────────────────────────

    private async void About_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "开机启动项：这里扫的是什么",
            Content = BuildAboutBody(),
            CloseButtonText = "关闭",
        };

        await ShowDialogAsync(dialog);
    }

    private static Control BuildAboutBody()
    {
        var panel = new StackPanel { Spacing = 14 };

        void Item(string title, string body)
        {
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock { Text = title, FontSize = 13 });
            block.Children.Add(new TextBlock
            {
                Text = body, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(block);
        }

        Item("扫的是这五处",
             "注册表的 Run 键（当前用户 / 所有用户 / 所有用户的 32 位）与启动文件夹（当前用户 / 所有用户）。"
             + "一次性启动项（RunOnce）不在此列 —— 它执行过一次就自动消失，没有「开机自启」这层含义。");

        Item("关掉一项会发生什么",
             "只是关掉这一项的「开机自启」：程序还在、文件一个不动、注册表里的命令行也原样保留。"
             + "启用状态记录在 Windows 自己的启动状态位里 —— 与任务管理器的「启动」页是同一份记录，"
             + "所以在任何一边改动，另外两边都会跟着变。");

        Item("「所有用户」级的项",
             "这类项的状态位需要管理员权限才能改写，按下去时会弹一次授权确认。"
             + "取消授权不会改动任何东西。");

        Item("「找不到对应文件」",
             "命令行指向的文件已经不存在时会标出来。这类多半是软件卸载之后留下的死项，"
             + "Windows 启动时本来就会跳过它们，可以放心关掉。");

        return panel;
    }

    // ── 弹窗（ContentDialog 同时只能开一个，统一走这两个入口） ──────────

    private async Task ShowDialogAsync(ContentDialog dialog)    {
        if (_dialogOpen) return;

        _dialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Info("startup", $"弹窗没弹出来：{ex.GetType().Name}");
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "关闭",
        };

        return ShowDialogAsync(dialog);
    }
}
