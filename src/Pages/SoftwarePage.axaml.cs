using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>软件下载页：搜索 + 分类筛选 + 软件卡片墙（全原生，不加载任何网页）。</summary>
public sealed partial class SoftwarePage : PageBase
{
    private string _category = "";
    private string _keyword = "";
    private string _view = "tile";          // tile | grid
    private bool _loading;
    private readonly List<ToggleButton> _chips = new();

    public SoftwarePage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        _category = parameter as string ?? "";

        _view = App.Settings.Current.AppCardView == "grid" ? "grid" : "tile";
        _loading = true;
        // ⚠️ 原版是 ViewChoice.SelectedIndex；Avalonia 没有 RadioButtons 容器，直接勾对应的 RadioButton。
        if (_view == "grid") GridRadio.IsChecked = true;
        else TileRadio.IsChecked = true;
        _loading = false;
        UpdateView();

        BuildChips();
        Apply();
    }

    /// <summary>磁贴（3 列，带简介）/ 网格（5 列，紧凑）切换，选择会记进设置。</summary>
    private void ViewChoice_Checked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        _view = tag == "grid" ? "grid" : "tile";
        App.Settings.Current.AppCardView = _view;
        App.Settings.Save();
        UpdateView();
    }

    private void UpdateView()
    {
        var tile = _view != "grid";
        // ⚠️ Avalonia 没有 Visibility 枚举 → 用 bool IsVisible。
        TileGrid.IsVisible = tile;
        CompactGrid.IsVisible = !tile;
    }

    private void BuildChips()
    {
        Chips.Children.Clear();
        _chips.Clear();

        AddChip("全部", "");
        foreach (var c in App.Content.Categories)
            AddChip(c.Name, c.Key);
    }

    private void AddChip(string text, string key)
    {
        var chip = new ToggleButton
        {
            Content = key.Length == 0 ? $"{text} {App.Content.Apps.Count}" : $"{text} {App.Content.CountInCategory(key)}",
            Tag = key,
            IsChecked = key == _category,
        };
        chip.Click += (s, _) =>
        {
            _category = (string)((ToggleButton)s!).Tag!;
            foreach (var other in _chips)
                if (!ReferenceEquals(other, s)) other.IsChecked = false;
            ((ToggleButton)s!).IsChecked = true;
            Apply();
        };
        _chips.Add(chip);
        Chips.Children.Add(chip);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        // ⚠️ 原版 AutoSuggestBox 会带 Reason（只在 UserInput 时处理）；
        //    Avalonia 的 TextBox.TextChanged 没有 Reason，任何文本变化都等同用户输入。
        _keyword = SearchBox.Text ?? "";
        Apply();
    }

    private void Apply()
    {
        var list = App.Content.Apps
            .Where(a => _category.Length == 0 || a.Category == _category)
            .Where(a => a.Matches(_keyword))
            .ToList();

        TileGrid.ItemsSource = list;
        CompactGrid.ItemsSource = list;

        TitleText.Text = _category.Length == 0
            ? (_keyword.Length == 0 ? "软件下载" : $"搜索：{_keyword}")
            : App.Content.CategoryName(_category);

        UpdateEmptyState(list.Count);
    }

    /// <summary>
    /// 空清单时别只写一句"没有匹配的软件"——要分清两种情况：
    /// ① 搜索/分类筛掉了 ② 整体就没内容（内容包没同步下来，教学机断网最容易踩）
    /// </summary>
    private void UpdateEmptyState(int shown)
    {
        if (shown > 0)
        {
            EmptyPanel.IsVisible = false;
            return;
        }

        EmptyPanel.IsVisible = true;

        var hasFilter = _keyword.Length > 0 || _category.Length > 0;
        if (hasFilter)
        {
            EmptyTitle.Text = "未找到匹配的软件";
            EmptyText.Text = "请更换关键词，或单击「全部」查看所有软件。";
            EmptyText.IsVisible = true;
            EmptyDetail.Text = $"当前内容来源：{App.Content.SourceLabel}（共 {App.Content.Apps.Count} 个软件）";
            EmptyRetry.IsVisible = false;
            return;
        }

        EmptyTitle.Text = "软件清单为空";
        EmptyText.Text = "清单位于「内容包」中：安装包内置一份，联网后自动从站点更新。" +
                         "若始终为空，通常是内容包未同步成功（网络不可用或站点尚未发布）。";
        EmptyText.IsVisible = true;
        EmptyDetail.Text = $"当前内容来源：{App.Content.SourceLabel}" +
                           (App.Content.Issues.Count > 0 ? $"\n读取问题：{App.Content.Issues[0].Message}" : "");
        EmptyRetry.IsVisible = true;
        EmptyRetry.IsEnabled = true;
        EmptyRetry.Content = "重新同步内容包";
    }

    /// <summary>空状态里的「重新同步内容包」：拉一次远端内容包再重读（失败就照实说）。</summary>
    private async void EmptyRetry_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        EmptyRetry.IsEnabled = false;
        EmptyRetry.Content = "正在同步";
        EmptyTitle.Text = "正在同步内容包";
        EmptyText.Text = "正在从站点获取最新清单。";
        EmptyDetail.Text = "";

        try
        {
            var result = await Services.ContentUpdater.SyncAsync(
                new Progress<string>(text => EmptyDetail.Text = text));

            if (result.Updated) App.Content.Load();

            BuildChips();
            Apply();

            if (App.Content.Apps.Count == 0)
            {
                EmptyTitle.Text = "仍未获取到内容";
                EmptyText.Text = "站点无法访问，或内容包尚未发布。已安装版本内置的清单可在离线时使用；" +
                                 "如问题持续，请将本页截图提供给维护人员。";
                EmptyDetail.Text = result.Message;
            }
        }
        catch (Exception ex)
        {
            EmptyTitle.Text = "同步失败";
            EmptyText.Text = "网络不可用或站点暂时无法访问，请稍后重试。";
            EmptyDetail.Text = ex.Message;
        }
        finally
        {
            EmptyRetry.IsEnabled = true;
            EmptyRetry.Content = "重新同步内容包";
        }
    }

    /// <summary>
    /// 卡片单击 → 进软件详情页。
    /// ⚠️ 原版是 <c>Frame.Navigate</c>（WinUI Page.Frame 自带宿主 Frame）；
    ///    Avalonia 的页面是 UserControl，没有 Frame 属性 → 沿可视树向上找宿主 <see cref="Frame"/>。
    /// </summary>
    private void AppGrid_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is SoftwareApp app)
            this.FindAncestorOfType<Frame>()?.Navigate(typeof(DetailPage), app.Id);
    }

    // 卡片自己的悬停反馈由 XAML 里 Class="card" 的 :pointerover 样式给出（见页面顶部注释）。
}
