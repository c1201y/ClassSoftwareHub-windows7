using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 文件快搜的主体控件（浮窗 + 主界面页面共用）。搜索防抖、键盘操作与结果操作全在这里，
/// 两处外壳只决定它长在多大的窗口里。
/// </summary>
public sealed partial class FileSearchView : UserControl
{
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _queryCts;
    private bool _attached;
    private int _lastResultCount = -1;

    public FileSearchView()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };

        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    /// <summary>把焦点放进搜索框（浮窗显示 / 页面进入时调用），并全选上次的关键词。</summary>
    public void FocusSearch()
    {
        try
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
        }
        catch { }
    }

    /// <summary>当前结果条数（页面状态栏用）。</summary>
    public int ResultCount => _lastResultCount < 0 ? 0 : _lastResultCount;

    // ── 生命周期 ─────────────────────────────────────────────────────────

    private void Attach()
    {
        if (_attached) return;
        _attached = true;

        FileSearchService.Current.Changed += OnIndexChanged;
        FileSearchService.Current.EnsureStarted();
        RefreshStatus();

        _ = RunSearchAsync();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        FileSearchService.Current.Changed -= OnIndexChanged;

        try { _queryCts?.Cancel(); } catch { }
        _queryCts = null;
        _debounce.Stop();
    }

    private void OnIndexChanged()
    {
        // 服务可能从后台线程通知：状态栏与列表都只允许在 UI 线程改
        Dispatcher.UIThread.Post(() =>
        {
            RefreshStatus();
            // 索引刚建好且用户已经输入了关键词 → 用新索引再搜一次
            if (!string.IsNullOrWhiteSpace(QueryBox.Text) && !FileSearchService.Current.IsBusy)
                _ = RunSearchAsync();
        });
    }

    // ── 搜索 ────────────────────────────────────────────────────────────

    private void Query_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RunSearchAsync()
    {
        var query = QueryBox.Text ?? "";

        try { _queryCts?.Cancel(); } catch { }
        var cts = new CancellationTokenSource();
        _queryCts = cts;

        if (string.IsNullOrWhiteSpace(query))
        {
            ResultList.ItemsSource = Array.Empty<FileSearchItem>();
            _lastResultCount = 0;
            EmptyHint.IsVisible = true;
            EmptyHint.Text = "输入文件名或路径片段开始搜索，多个关键词用空格分开";
            RefreshStatus();
            UpdateSelectionButtons();
            return;
        }

        try
        {
            var results = await FileSearchService.Current.SearchAsync(query, FileSearchService.MaxResults, cts.Token);
            if (cts.IsCancellationRequested) return;

            ResultList.ItemsSource = results;
            _lastResultCount = results.Count;
            EmptyHint.IsVisible = results.Count == 0;
            EmptyHint.Text = FileSearchService.Current.Count == 0
                ? "索引还没建立好，稍等几秒再试"
                : "没有找到匹配的文件，换个关键词试试";

            if (results.Count > 0) ResultList.SelectedIndex = 0;
            RefreshStatus();
            UpdateSelectionButtons();
        }
        catch (OperationCanceledException)
        {
            // 输入又变了，这次查询作废
        }
        catch (Exception ex)
        {
            AppLog.Catch("file-search", "执行文件搜索", ex);
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var svc = FileSearchService.Current;
        Busy.IsActive = svc.IsBusy;
        Busy.IsVisible = svc.IsBusy;

        var status = svc.StatusText;
        if (_lastResultCount >= 0 && !string.IsNullOrWhiteSpace(QueryBox.Text))
            status = $"结果 {_lastResultCount} 条 · " + status;

        StatusText.Text = status;
        ToolTip.SetTip(StatusText, status);

        // 索引进行中允许点“重建”（= 放弃这次、重来），但按钮文案提示一下当前状态
        RebuildButton.Content = svc.IsBusy ? "重建中…" : "重建索引";
        RebuildButton.IsEnabled = !svc.IsBusy;
    }

    private void UpdateSelectionButtons()
    {
        var has = ResultList.SelectedItem is FileSearchItem;
        OpenButton.IsEnabled = has;
        RevealButton.IsEnabled = has;
        CopyButton.IsEnabled = has;
    }

    private void List_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelectionButtons();

    private void Query_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                OpenSelected();
                break;

            case Key.Down:
                e.Handled = true;
                if (ResultList.ItemCount > 0)
                {
                    if (ResultList.SelectedIndex < 0) ResultList.SelectedIndex = 0;
                    ResultList.Focus();
                }
                break;
        }
    }

    private void List_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (ResultList.SelectedItem is FileSearchItem) OpenSelected();
    }

    // ── 结果操作 ─────────────────────────────────────────────────────────

    private FileSearchItem? Selected => ResultList.SelectedItem as FileSearchItem;

    private void Open_Click(object? sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        var item = Selected ?? (ResultList.ItemCount > 0 ? ResultList.ItemsSource?.Cast<FileSearchItem>().FirstOrDefault() : null);
        if (item is null) return;

        if (!FileSearchService.TryOpen(item))
            StatusText.Text = "打不开：" + item.Path;
    }

    private void Reveal_Click(object? sender, RoutedEventArgs e)
    {
        if (FileSearchService.TryReveal(Selected)) return;
        StatusText.Text = "定位失败：" + (Selected?.Path ?? "");
    }

    private void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item is null) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                _ = clipboard.SetTextAsync(item.Path);
                StatusText.Text = "路径已复制";
            }
        }
        catch (Exception ex)
        {
            AppLog.Catch("file-search", "复制结果路径", ex);
        }
    }

    private void Rebuild_Click(object? sender, RoutedEventArgs e)
    {
        FileSearchService.Current.Rebuild();
        RefreshStatus();
    }
}
