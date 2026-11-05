using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 文件快搜的完整页面：磁盘范围、索引状态与完整搜索界面。
/// 搜索本体在 <see cref="Views.FileSearchView"/> 里，与浮窗共用。
/// </summary>
public sealed partial class FileSearchPage : PageBase
{
    private bool _loadingDrives;

    public FileSearchPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        FileSearchService.Current.EnsureStarted();
        BuildDriveList();
        SearchView.FocusSearch();
    }

    private void BuildDriveList()
    {
        _loadingDrives = true;
        try
        {
            DrivePanel.Children.Clear();

            var selected = App.Settings.Current.FileSearchDrives ?? Array.Empty<string>();
            var selectAll = selected.Length == 0;

            foreach (var root in FileSearchService.AvailableDrives())
            {
                var isOn = selectAll
                           || selected.Any(s => s.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase));

                var box = new CheckBox
                {
                    Content = root + "\\",
                    IsChecked = isOn,
                    MinWidth = 0,
                    Margin = new Avalonia.Thickness(0, 0, 0, 0),
                };
                box.IsCheckedChanged += (_, _) => OnDriveToggled();
                DrivePanel.Children.Add(box);
            }

            if (DrivePanel.Children.Count == 0)
            {
                DrivePanel.Children.Add(new TextBlock
                {
                    Text = "没有可用的固定磁盘",
                    FontSize = 12,
                    Opacity = 0.55
                });
            }
        }
        finally
        {
            _loadingDrives = false;
        }
    }

    /// <summary>勾选变化 → 存进设置 → 重建索引。</summary>
    private void OnDriveToggled()
    {
        if (_loadingDrives) return;

        try
        {
            var all = FileSearchService.AvailableDrives().ToList();
            var checkedRoots = DrivePanel.Children.OfType<CheckBox>()
                .Where(b => b.IsChecked == true)
                .Select(b => (b.Content as string ?? "").TrimEnd('\\'))
                .Where(s => s.Length > 0)
                .ToList();

            // 全选 = 空数组（以后插上 U 盘也自动包含）；只选部分才记具体磁盘
            App.Settings.Current.FileSearchDrives = checkedRoots.Count >= all.Count
                ? Array.Empty<string>()
                : checkedRoots.ToArray();
            App.Settings.Save();

            FileSearchService.Current.RebuildForScopeChange();
        }
        catch (Exception ex)
        {
            Core.AppLog.Catch("file-search", "更新索引范围", ex);
        }
    }

    private void OpenWindow_Click(object? sender, RoutedEventArgs e)
    {
        Views.FileSearchWindow.ShowWindow();
    }

    private void Rebuild_Click(object? sender, RoutedEventArgs e)
    {
        FileSearchService.Current.Rebuild();
    }
}
