using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services.Docs;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 文档库列表页（需求 15）。
///
/// <para>
/// 这一页只读**清单**（标题 / 作者 / 简介 / 标签），正文要等点开某篇时才下载 ——
/// 见 <see cref="DocLibrary"/> 的两层缓存说明。
/// </para>
/// <para>
/// 搜索框只在清单里查（标题、简介、标签、作者）；应用全局搜索同样只查这些，不碰正文。
/// </para>
/// <para>
/// 移植说明：原版是 <c>Page</c> + <c>OnNavigatedTo(NavigationEventArgs)</c>；
/// Avalonia 侧统一用 <see cref="PageBase"/> 的 <c>OnNavigatedTo(object?)</c>。
/// 原版 <c>ListView.IsItemClickEnabled/ItemClick</c> 没有对应物 ⇒ 改用
/// <c>ListBox.SelectionChanged</c>，跳转后立刻把选中复位（否则回到本页再点同一篇不触发）。
/// </para>
/// </summary>
public sealed partial class DocLibraryPage : PageBase
{
    private bool _syncing;

    /// <summary>正在程序性复位选中：此时的 SelectionChanged 不是用户点的。</summary>
    private bool _resettingSelection;

    public DocLibraryPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);

        ApplyFilter();
        _ = SyncAsync(force: false);
    }

    private async Task SyncAsync(bool force)
    {
        if (_syncing) return;
        _syncing = true;
        RefreshButton.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            var result = await DocLibrary.SyncAsync(force, progress);

            StatusText.Text = result.Ok
                ? $"{result.Message}　·　共 {DocLibrary.Articles.Count} 篇"
                : result.Message;

            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppLog.Exception("docs", ex, "同步文档库");
            StatusText.Text = "文档库同步失败，正在显示本机已缓存的清单。";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _syncing = false;
        }
    }

    private void ApplyFilter()
    {
        var query = (SearchBox.Text ?? "").Trim();
        var all = DocLibrary.Articles;

        IReadOnlyList<DocArticle> rows = all;
        if (query.Length > 0)
        {
            var parts = query
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            rows = all.Where(article => parts.All(part =>
                SearchText(article).Contains(part, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        ArticleList.ItemsSource = rows;

        if (rows.Count == 0)
        {
            EmptyText.Text = all.Count == 0
                ? "文档库还没有文章。投稿通过后，教程会出现在这里。"
                : "没有找到匹配的文章。换个关键词试试。";
            EmptyPanel.IsVisible = true;
            ArticleList.IsVisible = false;
        }
        else
        {
            EmptyPanel.IsVisible = false;
            ArticleList.IsVisible = true;
        }
    }

    private static string SearchText(DocArticle article) =>
        article.Title + "\n" + article.Summary + "\n" + article.Author + "\n"
        + string.Join(" ", article.Tags);

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void Refresh_Click(object? sender, RoutedEventArgs e) => _ = SyncAsync(force: true);

    private void Submit_Click(object? sender, RoutedEventArgs e)
        => App.MainWindow?.OpenExternal(DocLibrary.SubmitUrl());

    /// <summary>
    /// 点一行 → 打开正文。原版走 <c>App.MainWindow.Shell.NavigateToDocArticle(id)</c>；
    /// 导航高亮那一步属于 ShellPage 的接线（见交付说明的「待接线」），这里先用仓库里
    /// 通行的 "就近 Frame" 写法（与 SoftwarePage / MachineCheckPage 打开详情页同一条路）。
    /// </summary>
    private void ArticleList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_resettingSelection) return;
        if (ArticleList.SelectedItem is not DocArticle article) return;

        _resettingSelection = true;
        try
        {
            ArticleList.SelectedIndex = -1;
        }
        finally
        {
            _resettingSelection = false;
        }

        var frame = this.FindAncestorOfType<Frame>() ?? App.MainWindow?.Shell.ContentFrame;
        frame?.Navigate(typeof(DocArticlePage), article.Id);
    }
}
