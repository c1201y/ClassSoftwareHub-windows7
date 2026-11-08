using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 结果列表里的一行。包一层是为了让勾选状态能双向绑回控件（<see cref="GhostFile.GhostEntry"/> 本身是只读的）。
/// </summary>
public sealed class GhostRow : INotifyPropertyChanged
{
    private bool _selected = true;      // 默认全勾上：用户扫完通常就是想把它们清掉

    public GhostRow(GhostFile.GhostEntry entry) => Entry = entry;

    public GhostFile.GhostEntry Entry { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    /// <summary>末尾的空格用 ␣ 画出来 —— 不然一行「新建文件夹」和「新建文件夹 」在屏幕上完全一样。</summary>
    public string DisplayName => Entry.DisplayName;

    public string SubText => (Entry.IsDirectory ? "文件夹" : "文件") + "　·　" + Entry.Reason;

    public string SizeText => Entry.IsDirectory ? "" : FormatSize(Entry.Size);

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "",
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#") + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB",
    };
}

/// <summary>
/// 「幽灵文件清理」页（自上游 dv1.2.0-insider1.10 移植，实验性功能）。
///
/// <para>
/// 判定与删除都在 <see cref="GhostFile"/>，这里只管界面与那道确认流程。
/// </para>
///
/// <para>
/// 相对上游的偏离：
/// ① <c>ListView + DataTemplate + x:Bind</c> → 代码生成行（<see cref="BuildRows"/>），
///    本仓库动态列表统一这么做（<c>PageFilePage</c> / <c>AppDataPage</c>），绑定问题少。
/// ② 上游只有「删除选中」按钮，没有全选、没有计数。这里补一组：底部常驻条上的
///    「已选 N 项 / 共 M 项」+ 全选 + 删除 —— 主控要求，且 380 高的列表滚起来时操作条不该跟着跑掉。
/// ③ 扫描加了取消：<see cref="GhostFile.Scan"/> 接受 <see cref="CancellationToken"/>，
///    离开页面时把还在跑的那一趟停掉（目录条目多时这趟能跑几十秒）。
/// ④ <c>ContentDialog.XamlRoot</c> 不存在；<c>DispatcherQueue.CreateTimer</c> → <see cref="DispatcherTimer"/>。
/// ⑤ 删除的文案改了：现在优先走回收站，不再一律说「永久删除」。
/// </para>
/// </summary>
public sealed partial class GhostFilePage : PageBase
{
    private List<GhostRow> _rows = new();

    /// <summary>当前这一趟扫描的取消令牌（离开页面 / 重新扫描时用）。</summary>
    private CancellationTokenSource? _scanCts;

    private bool _scanning;

    private bool _busy;

    /// <summary>正在批量改勾选状态，此时的 IsCheckedChanged 是程序触发的。</summary>
    private bool _syncing;

    /// <summary>确认框是否已经弹着 —— 同一时刻只允许一个 ContentDialog，第二个会抛异常。</summary>
    private bool _dialogOpen;

    /// <summary>当前目录在保护名单里（只检查、不删除）。</summary>
    private bool _protected;

    public GhostFilePage()
    {
        InitializeComponent();

        // 回车直接扫，省得每次去点按钮
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = ScanAsync();
        };
    }

    /// <summary>离开页面：把还在跑的扫描停掉（它攥着目录句柄，不该继续跑）。</summary>
    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();

        try
        {
            _scanCts?.Cancel();
        }
        catch (Exception ex)
        {
            AppLog.Catch("ghost", "离开页面时取消扫描", ex);
        }
    }

    // ── 扫描 ─────────────────────────────────────────────────────────────

    private async void Scan_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ScanAsync();
        }
        catch (Exception ex)
        {
            // async void 里漏出去的异常会直接终止进程 —— 兜到底
            AppLog.Exception("ghost", ex, "扫描幽灵文件");
            ShowMessage(InfoBarSeverity.Error, "扫描时出错", ex.Message);
        }
    }

    private async Task ScanAsync()
    {
        var path = (PathBox.Text ?? "").Trim().Trim('"');
        if (path.Length == 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "还没有填目录", "请填写幽灵文件所在的那一级文件夹。");
            return;
        }

        if (_scanning) return;

        var cts = new CancellationTokenSource();
        _scanCts = cts;

        _scanning = true;
        HideResult();
        SetScanningUi(true, "正在扫描…");

        try
        {
            var result = await Task.Run(() => GhostFile.Scan(path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            ApplyScanResult(result);
        }
        catch (OperationCanceledException)
        {
            // 用户又点了扫描 / 离开了页面：静默收场，不报错
        }
        catch (Exception ex)
        {
            AppLog.Exception("ghost", ex, "扫描幽灵文件");
            ShowMessage(InfoBarSeverity.Warning, "扫描时出错", ex.Message);
        }
        finally
        {
            _scanning = false;
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            SetScanningUi(false, "");
        }
    }

    /// <summary>把扫描结果铺到界面上（初次扫描和删完重扫都走这里）。</summary>
    private void ApplyScanResult(GhostFile.ScanResult result)
    {
        // 先把上一次的结果收干净：删完重扫时如果一条都不剩，底部操作条不能还挂着旧条目
        // （否则「删除选中」会拿着已经删掉的记录再删一次）
        ResultPanel.IsVisible = false;
        ActionBar.IsVisible = false;
        RefreshRow.IsVisible = false;
        _rows = new List<GhostRow>();
        RowList.Children.Clear();
        _protected = false;

        if (result.Error.Length > 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "没法扫描这个目录", result.Error);
            return;
        }

        if (result.Items.Count == 0)
        {
            // 这一支很重要：扫不到不等于没救 —— 文件系统里根本没这东西，那是资源管理器没刷新视图。
            var detail = result.Total == 0
                ? "这个目录当前是空的。"
                : $"这个目录里共 {result.Total} 个条目，名字和访问都正常。";

            ShowMessage(InfoBarSeverity.Success, "没有发现幽灵文件",
                detail + "如果资源管理器里还挂着一个已经删掉的东西，那多半是显示没有刷新，点下面的按钮让它重新读一遍。");
            RefreshRow.IsVisible = true;
            return;
        }

        _rows = result.Items.Select(item => new GhostRow(item)).ToList();

        BuildRows();

        ResultPanel.IsVisible = true;
        ActionBar.IsVisible = true;
        ResultTitle.Text = $"扫描结果（{_rows.Count} 项）";

        // 保护目录：照样列出来（用户有权看），但删除按钮停用并说明原因
        _protected = GhostFile.IsProtected(result.Items[0].Parent, out var reason);
        DeleteButton.IsEnabled = !_protected;

        SelSubtitle.Text = _protected
            ? reason + " 这里只做检查，不会删除任何东西。"
            : "删除优先走回收站；系统关闭回收站或文件过大时可能被直接永久删除，无法保证可还原。";

        UpdateSummary();

        AppLog.Info("ghost", $"扫到 {_rows.Count} 个幽灵条目（该目录共 {result.Total} 个）");
    }

    // ── 列表行 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 每行是一个 Grid（勾选框 / 名字与原因 / 大小），对应上游
    /// <c>ListView.ItemTemplate</c> 里那张三列 56 高的模板。
    /// </summary>
    private void BuildRows()
    {
        RowList.Children.Clear();

        // 列表是代码建的，勾选状态直接接事件 —— 不走绑定，避免反射绑定的坑
        foreach (var row in _rows)
        {
            var item = row;

            var grid = new Grid { MinHeight = 56, Margin = new Thickness(0, 0, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new CheckBox
            {
                IsChecked = item.Selected,
                MinWidth = 0,
                Padding = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(check, "勾选这一项");
            check.IsCheckedChanged += (_, _) =>
            {
                item.Selected = check.IsChecked == true;
                if (!_syncing) UpdateSummary();
            };
            grid.Children.Add(check);

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            text.Children.Add(new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = item.SubText,
                FontSize = 12,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
            grid.Children.Add(text);

            var size = new TextBlock
            {
                Text = item.SizeText,
                FontSize = 12,
                Opacity = 0.6,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(size, 2);
            grid.Children.Add(size);

            RowList.Children.Add(grid);
        }
    }

    /// <summary>刷新「已选 N 项 / 共 M 项」与全选框的三态。</summary>
    private void UpdateSummary()
    {
        var total = _rows.Count;
        var picked = _rows.Count(r => r.Selected);

        SelTitle.Text = $"已选 {picked} 项 / 共 {total} 项";

        _syncing = true;
        try
        {
            SelectAll.IsChecked = picked == 0 ? false
                                : picked == total ? true
                                : (bool?)null;
        }
        finally
        {
            _syncing = false;
        }

        if (!_busy) DeleteButton.IsEnabled = picked > 0 && !_protected;
    }

    private void SelectAll_Changed(object? sender, RoutedEventArgs e)
    {
        if (_syncing || _busy) return;

        // 三态里「不确定」等同于「全部取消」：用户从半选状态点一下，期望是明确的全选或全不选
        var select = SelectAll.IsChecked == true;

        foreach (var row in _rows) row.Selected = select;

        // 行的勾选框也要跟着走：直接重建最省事，行数不会太多
        BuildRows();
        UpdateSummary();
    }

    // ── 删除 ─────────────────────────────────────────────────────────────

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await DeleteAsync();
        }
        catch (Exception ex)
        {
            AppLog.Exception("ghost", ex, "清理幽灵文件");
            ShowMessage(InfoBarSeverity.Error, "清理时出错", ex.Message);
        }
    }

    private async Task DeleteAsync()
    {
        if (_busy) return;

        var picked = _rows.Where(r => r.Selected).ToList();
        if (picked.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, "没有选中任何条目", "勾选要删除的条目后再点删除。");
            return;
        }

        if (GhostFile.IsProtected(picked[0].Entry.Parent, out var why))
        {
            ShowMessage(InfoBarSeverity.Warning, "这个目录在保护范围内", why);
            return;
        }

        if (!await ConfirmAsync(picked.Count)) return;

        _busy = true;
        DeleteButton.IsEnabled = false;

        try
        {
            var failures = new List<string>();
            var ok = 0;
            var permanent = 0;

            foreach (var row in picked)
            {
                var (success, error, hard) = await Task.Run(() =>
                {
                    var done = GhostFile.Delete(row.Entry, out var message, out var wasPermanent);
                    return (done, message, wasPermanent);
                });

                if (success)
                {
                    ok++;
                    if (hard) permanent++;
                }
                else if (failures.Count < 3)
                {
                    failures.Add($"{row.DisplayName}：{error}");
                }
            }

            AppLog.Info("ghost",
                $"删除完成：成功 {ok} 个（其中 {permanent} 个无法确认是否可还原），失败 {picked.Count - ok} 个");

            // 删完立刻重扫一遍，让列表反映真实情况（别让用户对着已经删掉的东西再点一次）
            var parent = picked[0].Entry.Parent;
            PathBox.Text = parent;

            var result = await Task.Run(() => GhostFile.Scan(parent));
            ApplyScanResult(result);

            if (failures.Count == 0)
            {
                ShowMessage(InfoBarSeverity.Success, $"已清理 {ok} 个条目",
                    permanent == 0
                        ? "已尝试送入回收站；系统关闭回收站或文件过大时可能被直接永久删除，"
                          + "无法保证可还原，请以回收站里的实际内容为准。列表已重新扫描。"
                        : $"已尝试为 {ok} 个条目送入回收站，其中 {permanent} 个无法确认是否真的进了回收站。"
                          + "系统关闭回收站或文件过大时 Shell 会静默永久删除，请以回收站实际内容为准。列表已重新扫描。");
            }
            else
            {
                ShowMessage(InfoBarSeverity.Warning,
                    $"已清理 {ok} 个，{picked.Count - ok} 个没删掉",
                    "没删掉的原因：" + string.Join("；", failures));
            }
        }
        finally
        {
            _busy = false;
            UpdateSummary();
            DeleteButton.IsEnabled = _rows.Count > 0 && !_protected;
        }
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        var path = (PathBox.Text ?? "").Trim().Trim('"');
        if (path.Length == 0) return;

        GhostFile.RefreshExplorer(path);
        ShowMessage(InfoBarSeverity.Informational, "已通知资源管理器刷新",
            "回到资源管理器看看那个图标还在不在。如果还在，把它所在的文件夹关掉再重新打开。");
    }

    // ── 浏览 ─────────────────────────────────────────────────────────────

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // ⚠️ Win7 走 FilePickerCompat 兜底（Avalonia 选择器在「库」里选位置会静默失败）。
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            var r = await FilePickerCompat.PickFolderAsync(top, "选择要检查的文件夹");
            if (r.Error is not null)
            {
                AppLog.Warning("ghost", "选择文件夹｜结果=被拒绝｜原因=" + r.Error);
                ShowMessage(InfoBarSeverity.Warning, "无法打开文件夹选择器", "可以直接在输入框里手动填写路径。");
                return;
            }

            if (r.Files.Count == 0) return;

            PathBox.Text = r.Files[0];
            await ScanAsync();
        }
        catch (Exception ex)
        {
            AppLog.Exception("ghost", ex, "选择文件夹");
            ShowMessage(InfoBarSeverity.Warning, "无法打开文件夹选择器", "可以直接在输入框里手动填写路径。");
        }
    }

    // ── 确认与提示 ───────────────────────────────────────────────────────

    /// <summary>
    /// 二次确认，带 3 秒缓冲。
    ///
    /// <para>
    /// 3 秒是刻意留的：这一页的操作往往是在「咦怎么删不掉」的烦躁状态下做的 ——
    /// 手快连点两下就把别的东西删了。缓冲期间主按钮是禁用的，按钮上的数字一跳一跳，
    /// 用户能看清自己在确认什么。
    /// </para>
    ///
    /// <para>
    /// 文案不再写死「永久删除」：现在优先进回收站，只有进不了的那些才是永久的
    /// （见 <see cref="GhostFile.Delete"/> 的三层回退）。
    /// </para>
    /// </summary>
    private async Task<bool> ConfirmAsync(int count)
    {
        if (_dialogOpen) return false;

        _dialogOpen = true;

        var dialog = new ContentDialog
        {
            Title = "确认清理",
            Content = new TextBlock
            {
                Text = $"将清理 {count} 个条目。\r\n"
                       + "优先尝试送入回收站；系统关闭回收站或文件过大时可能被直接永久删除，"
                       + "无法保证可以还原，请以回收站里的实际内容为准。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = "清理（3）",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };

        // ⚠️ WinUI DispatcherQueueTimer → Avalonia DispatcherTimer
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        var left = 3;
        timer.Tick += (_, _) =>
        {
            left--;
            if (left > 0)
            {
                dialog.PrimaryButtonText = $"清理（{left}）";
                return;
            }

            timer.Stop();
            dialog.PrimaryButtonText = "清理";
            dialog.IsPrimaryButtonEnabled = true;
        };

        timer.Start();

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            AppLog.Exception("ghost", ex, "删除确认对话框");
            return false;
        }
        finally
        {
            timer.Stop();
            _dialogOpen = false;
        }
    }

    private void SetScanningUi(bool scanning, string text)
    {
        ScanRing.IsActive = scanning;
        ScanButton.IsEnabled = !scanning;
        ScanState.Text = text;
    }

    private void HideResult()
    {
        ResultPanel.IsVisible = false;
        ActionBar.IsVisible = false;
        MessageBar.IsOpen = false;
        MessageBar.IsVisible = false;
        RefreshRow.IsVisible = false;
        DeleteButton.IsEnabled = true;

        _rows = new List<GhostRow>();
        RowList.Children.Clear();
        _protected = false;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        // MessageBar 平时 Collapsed（避免那条外边距在页头下面撑出空档），用之前先放出来
        MessageBar.IsVisible = true;
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;

        if (severity != InfoBarSeverity.Success) RefreshRow.IsVisible = false;
    }
}
