using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Controls;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 列表里的一行。两种来源共用一个类型：
///  - 体检行：一级目录，**永远不可清理**（只读体检）；
///  - 清理行：命中白名单的缓存目录。
///
/// ⚠️ 体积是**后台一个个填进来的**，行控件里直接改文字（本站风格：模板事件不跨绑定）。
/// </summary>
public sealed class AppDataRow : INotifyPropertyChanged
{
    private long _bytes = -1;      // -1 = 还没统计
    private int _files;
    private bool _busy;
    private string _status = "";

    private AppDataRow(string name, string sub, string glyph, bool cleanable,
        AppDataEntry? entry, AppDataKind kind, string measuredPath)
    {
        Name = name;
        Sub = sub;
        Glyph = glyph;
        Cleanable = cleanable;
        Entry = entry;
        Kind = kind;
        MeasuredPath = measuredPath;
    }

    /// <summary>体检行：一级目录，只读。</summary>
    public static AppDataRow FromFolder(AppDataFolder f) => new(
        f.Name,
        AppDataLook.ZoneLabelOf(f.Zone) + " · 一级目录（只读体检，不提供清理）",
        "\uE8B7",
        cleanable: false, entry: null, AppDataKind.Temp, f.FullPath);

    /// <summary>清理行：白名单命中的缓存目录。</summary>
    public static AppDataRow FromEntry(AppDataEntry e) => new(
        e.RelPath, e.Note, AppDataLook.GlyphOf(e.Kind),
        cleanable: true, e, e.Kind, e.FullPath);

    public string Name { get; }
    public string Sub { get; }
    public string Glyph { get; }
    public bool Cleanable { get; }
    public AppDataEntry? Entry { get; }
    public AppDataKind Kind { get; }

    /// <summary>统计体积时用的路径（概览行是一级目录本身，清理行是那个缓存目录）。</summary>
    public string MeasuredPath { get; }

    public long Bytes
    {
        get => _bytes;
        set
        {
            if (_bytes == value) return;
            _bytes = value;
            Raise(nameof(SizeText));
            Raise(nameof(CanClean));
            Raise(nameof(ActionTip));
        }
    }

    public int Files
    {
        get => _files;
        set
        {
            if (_files == value) return;
            _files = value;
            Raise(nameof(CountText));
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Raise(nameof(CanClean));
            Raise(nameof(CountText));
            Raise(nameof(ActionTip));
        }
    }

    /// <summary>清理完之后的一句状态（"已清空" / "已清空（跳过 N 项）"），压过文件数显示。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(CountText));
        }
    }

    public string SizeText => _bytes < 0 ? "统计中…" : AppLog.Size(_bytes);

    public string CountText =>
        _busy ? "清理中…"
        : _status.Length > 0 ? _status
        : _files > 0 ? _files.ToString("N0") + " 个文件"
        : "";

    /// <summary>⛔ 体积没统计完不给点：确认弹窗要写清"将释放多少"，没数字就没法交代。</summary>
    public bool CanClean => Cleanable && !_busy && _bytes >= 0;

    public string ActionTip =>
        !Cleanable ? ""
        : _busy ? "正在清理"
        : _bytes < 0 ? "等体积统计完再清理"
        : $"清理「{Name}」（永久删除，删掉找不回来）";

    private bool _checked;

    /// <summary>这一行有没有被勾上（勾选状态存在行自己身上，页面只读它）。</summary>
    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Raise(nameof(IsChecked));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// AppData 管理（2026-10-18 自上游 1.2.0 移植到 Avalonia）。
///
/// 三层边界的定义全在 <see cref="AppDataScan"/> 的类注释里，
/// 这一层只负责：**把体积一个个问出来、按分类摆好、把删除动作交给服务层**。
///
/// 扫描是**两段式**的：
///  阶段 A：只读目录结构 —— 秒出，列表立刻有内容，体积先显示"统计中…"；
///  阶段 B：后台逐个递归统计体积 —— 不冻 UI、可取消，填一个刷一个。
/// </summary>
public partial class AppDataPage : Platform.PageBase
{
    /// <summary>下拉里的一项。</summary>
    private sealed class CategoryOption
    {
        public CategoryOption(bool isOverview, AppDataZone zone, AppDataKind kind, string glyph, string label)
        {
            IsOverview = isOverview;
            Zone = zone;
            Kind = kind;
            Glyph = glyph;
            Label = label;
        }

        public bool IsOverview { get; }
        public AppDataZone Zone { get; }
        public AppDataKind Kind { get; }
        public string Glyph { get; }
        public string Label { get; }

        /// <summary>下拉项里那个计数 TextBlock 的引用 —— 统计完要**就地**改它（重建下拉会把选中项弄丢）。</summary>
        public TextBlock? CountBlock { get; set; }
    }

    /// <summary>一行的可视件（行控件 + 需要随时改的文字/勾选框）。</summary>
    private sealed class RowVisual
    {
        public AppDataRow Row = null!;
        public TextBlock SizeText = null!;
        public TextBlock CountText = null!;
        public CheckBox? Check;
        public Button? More;
    }

    private readonly List<AppDataRow> _foldersLocal = new();
    private readonly List<AppDataRow> _foldersRoaming = new();
    private readonly List<AppDataRow> _clean = new();

    /// <summary>行对象 → 可视件（统计时逐行刷新）。</summary>
    private readonly Dictionary<AppDataRow, RowVisual> _visuals = new();

    /// <summary>已经挂过 PropertyChanged 的行（行对象跨分类共用，卡片却会反复重建）。</summary>
    private readonly HashSet<AppDataRow> _subscribed = new();

    private CategoryOption? _category;
    private CancellationTokenSource? _cts;

    private bool _ready;        // 目录结构到位了没
    private bool _measuring;    // 正在统计体积
    private bool _busy;         // 正在跑一次清理

    public AppDataPage()
    {
        InitializeComponent();

        // 构造期控件还没进可视化树，扫描放 Attached（与其它页一致）
        AttachedToVisualTree += (_, _) =>
        {
            if (!_ready && _cts is null) _ = ScanAsync();
        };

        // 离开页面就掐掉后台统计
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
            AppLog.Exception("appdata", ex, "取消 AppData 统计");
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

        _ready = false;
        _measuring = true;

        LoadingPanel.IsVisible = true;
        EmptyPanel.IsVisible = false;
        CleanBar.IsVisible = false;
        EntryListPanel.Children.Clear();
        _visuals.Clear();
        CountLine.Text = "正在读取目录结构…";

        // ── 阶段 A：只读目录结构（秒出）──
        List<AppDataFolder> folders;
        List<AppDataEntry> clean;

        try
        {
            (folders, clean) = await Task.Run(() =>
            {
                var f = AppDataScan.TopFolders().ToList();
                var c = AppDataScan.FindCleanable().ToList();
                return (f, c);
            }, cts.Token);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            AppLog.Fail("appdata", ex, "读取 AppData 目录结构");
            _measuring = false;
            ShowEmpty("读取目录结构失败，详情见日志。");
            return;
        }

        if (cts.IsCancellationRequested) return;

        _foldersLocal.Clear();
        _foldersLocal.AddRange(folders.Where(f => f.Zone == AppDataZone.Local).Select(AppDataRow.FromFolder));
        _foldersRoaming.Clear();
        _foldersRoaming.AddRange(folders.Where(f => f.Zone == AppDataZone.Roaming).Select(AppDataRow.FromFolder));
        _clean.Clear();
        _clean.AddRange(clean.Select(AppDataRow.FromEntry));

        _ready = true;
        FillCategories();
        ShowCategory();
        LoadingPanel.IsVisible = false;

        AppLog.Info("appdata",
            $"目录结构读取完成：本地 {_foldersLocal.Count} 个一级目录、漫游 {_foldersRoaming.Count} 个、可清理 {_clean.Count} 项");

        // ── 阶段 B：后台逐个统计体积 ──
        var queue = new List<AppDataRow>(_foldersLocal.Count + _foldersRoaming.Count + _clean.Count);
        queue.AddRange(_foldersLocal);
        queue.AddRange(_foldersRoaming);
        queue.AddRange(_clean);

        await MeasureAllAsync(queue, cts);

        if (cts.IsCancellationRequested) return;

        _measuring = false;
        ShowCategory();
    }

    private async Task MeasureAllAsync(List<AppDataRow> queue, CancellationTokenSource cts)
    {
        foreach (var row in queue)
        {
            if (cts.IsCancellationRequested) return;

            var path = row.MeasuredPath;
            (long Bytes, int Files) measured;

            try
            {
                measured = await Task.Run(() => AppDataScan.Measure(path, cts.Token), cts.Token);
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                AppLog.Exception("appdata", ex, "统计体积：" + path);
                continue;
            }

            if (cts.IsCancellationRequested) return;

            row.Bytes = measured.Bytes;
            row.Files = measured.Files;
            RefreshRowVisual(row);
            RefreshCounts();

            // 体积一到位这一行才「能清」，所以动作条也要跟着重算。
            UpdateActionBar();
        }

        if (cts.IsCancellationRequested) return;

        // 统计完按体积重排一次 —— "谁在占地方"这个问题的答案本来就要按体积看
        SortBySize(_foldersLocal);
        SortBySize(_foldersRoaming);
        SortBySize(_clean);

        var biggest = _foldersLocal.FirstOrDefault();
        if (biggest is not null && biggest.Bytes > 0)
            AppLog.Info("appdata",
                $"体积统计完成；本地最大的一个一级目录是「{biggest.Name}」（{AppLog.Size(biggest.Bytes)}）");
    }

    private static void SortBySize(List<AppDataRow> rows)
    {
        rows.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
    }

    // ── 分类 ──────────────────────────────────────────────────────────

    private void FillCategories()
    {
        var wanted = _category;

        var options = new List<CategoryOption>
        {
            new(true, AppDataZone.Local, AppDataKind.Temp, "\uE8B7", "概览 · 本地"),
            new(true, AppDataZone.Roaming, AppDataKind.Temp, "\uE8B7", "概览 · 漫游"),
        };

        foreach (var kind in AppDataLook.Order)
            options.Add(new(false, AppDataZone.Local, kind, AppDataLook.GlyphOf(kind),
                "可清理 · " + AppDataLook.LabelOf(kind)));

        CategoryBox.Items.Clear();
        foreach (var option in options)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new FontIcon
            {
                Glyph = option.Glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = option.Label, VerticalAlignment = VerticalAlignment.Center,
            });

            var count = new TextBlock
            {
                FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center,
            };
            option.CountBlock = count;
            panel.Children.Add(count);

            CategoryBox.Items.Add(new ComboBoxItem { Content = panel, Tag = option });
        }

        var pick = options.FirstOrDefault(o => wanted is not null && o.Label == wanted.Label) ?? options[0];
        _category = pick;
        CategoryBox.SelectedIndex = options.IndexOf(pick);

        RefreshCounts();
    }

    /// <summary>刷新下拉里每一项的计数（统计中会变），顺带更新当前分类的计数文案。</summary>
    private void RefreshCounts()
    {
        foreach (var option in CategoryBox.Items.OfType<ComboBoxItem>()
                     .Select(i => i.Tag).OfType<CategoryOption>())
        {
            if (option.CountBlock is not null)
                option.CountBlock.Text = RowsOf(option).Count.ToString("N0");
        }

        UpdateCountText();
    }

    private void UpdateCountText()
    {
        if (!_ready || _category is null) return;

        var rows = RowsOf(_category);
        var label = _category.Label;
        var done = rows.Count(r => r.Bytes >= 0);

        if (_measuring && done < rows.Count)
        {
            CountLine.Text = $"{label} · {rows.Count} 项，正在统计体积（{done}/{rows.Count}）";
            return;
        }

        CountLine.Text = rows.Count == 0
            ? $"{label} · 没有东西"
            : $"{label} · {rows.Count} 项 · 合计 {AppLog.Size(rows.Sum(r => Math.Max(0, r.Bytes)))}";
    }

    private List<AppDataRow> RowsOf(CategoryOption option) =>
        option.IsOverview
            ? (option.Zone == AppDataZone.Local ? _foldersLocal : _foldersRoaming)
            : _clean.Where(r => r.Kind == option.Kind).ToList();

    private void Category_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is ComboBoxItem { Tag: CategoryOption option })
        {
            // 切分类就把勾清空：跨分类保留勾选只会让人记不清自己到底勾了哪一类。
            ClearAllChecks();

            _category = option;
            ShowCategory();
        }
    }

    /// <summary>把当前分类的行摆上去。</summary>
    private void ShowCategory()
    {
        if (!_ready || _category is null) return;

        var rows = RowsOf(_category);
        var any = rows.Count > 0;

        EntryListPanel.Children.Clear();
        _visuals.Clear();

        foreach (var row in rows)
            EntryListPanel.Children.Add(BuildRowVisual(row));

        EmptyPanel.IsVisible = !any;
        if (!any)
            EmptyText.Text = _category.IsOverview
                ? "这个根下没有可读的目录。"
                : $"「{_category.Label}」下没有找到可清理的缓存目录。";

        LoadingPanel.IsVisible = false;
        UpdateCountText();
        UpdateActionBar();
    }

    /// <summary>阶段 A 失败时的落地状态：收起转圈、给一句人话。</summary>
    private void ShowEmpty(string text)
    {
        _ready = false;
        LoadingPanel.IsVisible = false;
        EntryListPanel.Children.Clear();
        CleanBar.IsVisible = false;
        EmptyText.Text = text;
        EmptyPanel.IsVisible = true;
        CountLine.Text = "";
    }

    // ── 行控件 ────────────────────────────────────────────────────────

    private Control BuildRowVisual(AppDataRow row)
    {
        var grid = new Grid { MinHeight = 34, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 复选框只在可清理分类下出现（只读体检那一层没有删除入口）。
        CheckBox? check = null;
        if (row.Cleanable)
        {
            check = new CheckBox
            {
                IsChecked = row.IsChecked,
                MinWidth = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(check, "勾选这一项");
            check.IsCheckedChanged += (_, _) =>
            {
                row.IsChecked = check.IsChecked == true;
                if (!_suppressCheckEvent) UpdateActionBar();
            };
            grid.Children.Add(check);
        }

        var glyph = new FontIcon
        {
            Glyph = row.Glyph, FontSize = 15, Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(glyph, 1);
        grid.Children.Add(glyph);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 2);
        text.Children.Add(new TextBlock
        {
            Text = row.Name, FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        });
        var sub = new TextBlock
        {
            Text = row.Sub, FontSize = 12, Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        ToolTip.SetTip(sub, row.Sub);
        text.Children.Add(sub);
        grid.Children.Add(text);

        var stats = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var sizeText = new TextBlock
        {
            Text = row.SizeText, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right,
        };
        var countText = new TextBlock
        {
            Text = row.CountText, FontSize = 12, Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        stats.Children.Add(sizeText);
        stats.Children.Add(countText);
        Grid.SetColumn(stats, 3);
        grid.Children.Add(stats);

        // 行尾：一朵透明的「⋯」。菜单里有「打开文件夹」，可清理的行多一项「清理」。
        var more = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE712", FontSize = 14 },
        };
        ToolTip.SetTip(more, "更多操作");
        more.Click += (_, _) => ShowRowMenu(more, row);
        Grid.SetColumn(more, 4);
        grid.Children.Add(more);

        var visual = new RowVisual
        {
            Row = row, SizeText = sizeText, CountText = countText, Check = check, More = more,
        };
        _visuals[row] = visual;

        // 行对象状态变化时同步文字（体积统计完 / 清理中）。
        // ⚠️ 行对象在各分类之间是共用的，而卡片每次切换分类都会重建：不做标记的话，
        //    来回切几次就会给同一行挂上几十个一模一样的处理器（只增不减，审计 P2 #33）。
        if (_subscribed.Add(row))
        {
            row.PropertyChanged += (_, e) =>
            {
                if (!_visuals.TryGetValue(row, out var v)) return;
                if (e.PropertyName == nameof(AppDataRow.SizeText)) v.SizeText.Text = row.SizeText;
                if (e.PropertyName == nameof(AppDataRow.CountText)) v.CountText.Text = row.CountText;
            };
        }

        return grid;
    }

    private void RefreshRowVisual(AppDataRow row)
    {
        if (!_visuals.TryGetValue(row, out var v)) return;
        v.SizeText.Text = row.SizeText;
        v.CountText.Text = row.CountText;
    }

    // ── 批量选择（底部动作条） ──────────────────────────────────────────

    /// <summary>批量改写勾选时把逐行的刷新压住：几百项全选时不至于刷几百次动作条。</summary>
    private bool _suppressCheckEvent;

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_category is null || _category.IsOverview) return;

        // 只勾**能清**的：体积还在统计的那些不进选择区。
        _suppressCheckEvent = true;
        try
        {
            foreach (var row in RowsOf(_category))
            {
                row.IsChecked = row.CanClean;
                if (_visuals.TryGetValue(row, out var v) && v.Check is not null)
                    v.Check.IsChecked = row.IsChecked;
            }
        }
        finally
        {
            _suppressCheckEvent = false;
        }

        UpdateActionBar();
    }

    private void ClearSel_Click(object? sender, RoutedEventArgs e) => ClearAllChecks();

    /// <summary>
    /// 把所有行上的勾清掉。
    /// ⛔ 清的是**全部行**，不是当前分类那一屏 —— 切分类与清理收尾都要用它。
    /// </summary>
    private void ClearAllChecks()
    {
        _suppressCheckEvent = true;
        try
        {
            foreach (var row in _foldersLocal.Concat(_foldersRoaming).Concat(_clean))
            {
                row.IsChecked = false;
                if (_visuals.TryGetValue(row, out var v) && v.Check is not null)
                    v.Check.IsChecked = false;
            }
        }
        finally
        {
            _suppressCheckEvent = false;
        }

        UpdateActionBar();
    }

    private async void CleanSelected_Click(object? sender, RoutedEventArgs e)
    {
        var chosen = ChosenRows();
        if (chosen.Count == 0) return;
        await CleanAsync(chosen);
    }

    /// <summary>当前勾选里**真能清**的那些（还在统计体积、正在清理的不算）。</summary>
    private List<AppDataRow> ChosenRows() =>
        _category is null
            ? new List<AppDataRow>()
            : RowsOf(_category).Where(r => r.IsChecked && r.CanClean).ToList();

    /// <summary>刷新底部动作条。⛔ 只读体检分类、空分类、没准备好时整条藏起来。</summary>
    private void UpdateActionBar()
    {
        var show = _ready
                   && _category is { IsOverview: false }
                   && RowsOf(_category).Count > 0;

        CleanBar.IsVisible = show;
        if (!show) return;

        var chosen = ChosenRows();
        var size = chosen.Sum(r => Math.Max(0, r.Bytes));

        SelectAllButton.IsEnabled = !_busy;
        ClearSelButton.IsEnabled = !_busy && chosen.Count > 0;
        CleanSelectedButton.Content = chosen.Count > 0 ? $"清理选中 ({chosen.Count})" : "清理选中";
        CleanSelectedButton.IsEnabled = !_busy && chosen.Count > 0;

        CleanBarSummary.Text = chosen.Count == 0
            ? "勾选左侧复选框可一次处理多项。"
            : $"已选 {chosen.Count} 项" + (size > 0 ? $" · 合计约 {AppLog.Size(size)}" : "");
    }

    // ── 行尾菜单 ────────────────────────────────────────────────────────

    /// <summary>
    /// 行尾「⋯」—— 弹出这一行的操作。
    /// 顺序固定：**三项只读动作在前**，分隔线之后才是「清理」。
    /// </summary>
    private void ShowRowMenu(Control anchor, AppDataRow row)
    {
        var flyout = new MenuFlyout();

        var copy = new MenuItem { Header = "复制完整路径" };
        copy.Click += (_, _) => _ = CopyPathAsync(row);
        flyout.Items.Add(copy);

        // 「打开文件夹」对所有行都开放 —— 只读体检行尤其需要。
        var open = new MenuItem { Header = "打开文件夹" };
        open.Click += (_, _) => _ = OpenFolderAsync(row);
        flyout.Items.Add(open);

        // 「搜索」：查的是**完整路径**（不附加任何后缀）。
        var search = new MenuItem { Header = "在网上搜索完整路径" };
        search.Click += (_, _) => _ = SearchAsync(row);
        flyout.Items.Add(search);

        // 「清理」只有白名单命中的行才有（⛔ 只读项永远不出这一项）
        if (row.CanClean && row.Entry is not null)
        {
            flyout.Items.Add(new Separator());

            var clean = new MenuItem { Header = "清理" };
            clean.Click += (_, _) => _ = CleanAsync(new[] { row });
            flyout.Items.Add(clean);
        }

        flyout.ShowAt(anchor);
    }

    // ── 打开所在目录 / 复制路径 / 搜索（三个只读动作） ──────────────────

    private async Task OpenFolderAsync(AppDataRow row)
    {
        if (AppDataScan.OpenInExplorer(row.MeasuredPath)) return;

        await InfoAsync("没能打开", $"「{row.Name}」这个目录可能已经不在了，点「重新扫描」刷新一下。");
    }

    private async Task CopyPathAsync(AppDataRow row)
    {
        if (CopyText(row.MeasuredPath)) return;

        await InfoAsync("没能复制", "写入剪贴板失败，详情见日志。");
    }

    private async Task SearchAsync(AppDataRow row)
    {
        if (SearchOnline(row.MeasuredPath)) return;

        await InfoAsync("没能打开浏览器",
            "本机可能没有可用的默认浏览器。可以手动复制路径后在搜索框里查：" + row.MeasuredPath);
    }

    /// <summary>复制到剪贴板。</summary>
    private bool CopyText(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return false;
            _ = clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("appdata", ex, "复制路径：" + text);
            return false;
        }
    }

    /// <summary>
    /// 用**系统默认浏览器**去 Bing 搜这个目录。
    /// ⛔⛔ 查询词就是**完整路径本身，一个字都不加**（Nick 2026-10-15 定案）。
    /// </summary>
    private static bool SearchOnline(string fullPath)
    {
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(fullPath);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("appdata", ex, "打开浏览器搜索：" + fullPath);
            return false;
        }
    }

    // ── 清理 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 清理一批目录。
    /// ⛔ 行菜单里的「清理」和底部动作条的「清理选中」**走的是同一个方法**。
    /// </summary>
    private async Task CleanAsync(IReadOnlyList<AppDataRow> rows)
    {
        if (_busy) return;

        var work = rows.Where(r => r.CanClean && r.Entry is not null).ToList();
        if (work.Count == 0) return;

        // 占用检测：这批涉及哪几类就问哪几类，别按行重复问
        var running = work.Select(r => r.Entry!.Kind)
                          .Distinct()
                          .SelectMany(AppDataScan.RunningApps)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        var total = work.Sum(r => Math.Max(0, r.Bytes));
        var fileCount = work.Sum(r => Math.Max(0, r.Files));

        var dialog = new ContentDialog
        {
            Title = work.Count == 1 ? $"清理「{work[0].Name}」？" : $"清理选中的 {work.Count} 个目录？",
            Content = BuildConfirmBody(work, total, fileCount, running),
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            // ⚠️ 默认落在「取消」：删的是别人的数据，别让一个回车就动手
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        RescanButton.IsEnabled = false;
        CategoryBox.IsEnabled = false;
        UpdateActionBar();                       // 让三颗按钮一起变灰

        var okCount = 0;
        var failCount = 0;
        var skipped = 0;
        var freed = 0L;
        var notes = new List<string>();

        foreach (var row in work)
        {
            row.Busy = true;
            var before = Math.Max(0, row.Bytes);

            try
            {
                // ⚠️ **串行**跑：这是秒级动作，几条并行没有意义。
                var entry = row.Entry!;
                var outcome = await Task.Run(() => AppDataScan.Clean(entry, CancellationToken.None));

                // 清完重新量一次，把新体积如实显示出来
                var (bytes, files) = await Task.Run(() => AppDataScan.Measure(row.MeasuredPath, CancellationToken.None));
                row.Bytes = bytes;
                row.Files = files;

                if (outcome.Ok)
                {
                    okCount++;
                    skipped += outcome.Skipped;
                    freed += before - bytes;
                    row.Status = outcome.Skipped > 0 ? $"已清空（跳过 {outcome.Skipped} 项）" : "已清空";
                }
                else
                {
                    failCount++;
                    row.Status = "";
                    notes.Add($"「{row.Name}」：{outcome.Message}");
                }
            }
            catch (Exception ex)
            {
                failCount++;
                row.Status = "";
                AppLog.Fail("appdata", ex, "清理：" + row.Name);
                notes.Add($"「{row.Name}」：遇到错误，详情见日志。");
            }
            finally
            {
                row.Busy = false;
            }
        }

        _busy = false;
        RescanButton.IsEnabled = true;
        CategoryBox.IsEnabled = true;

        // ⛔ 走 ShowCategory 而不是就地改：清完体积变小，计数行、下拉里的数字、动作条都要跟着重算。
        ClearAllChecks();
        ShowCategory();
        RefreshCounts();

        await InfoAsync(failCount == 0 ? "清理完成" : "部分完成",
            BuildResultBody(okCount, failCount, freed, skipped, notes));
    }

    private static Control BuildConfirmBody(
        IReadOnlyList<AppDataRow> rows, long total, int fileCount, IReadOnlyList<string> running)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = rows.Count == 1
                ? $"将清空这个目录的内容（{AppLog.Size(total)}，{fileCount:N0} 个文件），目录本身保留："
                : $"将清空以下 {rows.Count} 个目录的内容（合计 {AppLog.Size(total)}，{fileCount:N0} 个文件），"
                  + "目录本身保留：",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });

        // 清单单独给一块小字底 —— 确认框的意义就是让人看清到底动的是哪儿。
        var lines = rows.Count == 1
            ? rows[0].MeasuredPath
            : string.Join("\n", rows.Take(10).Select(r => $"{r.Name}    {AppLog.Size(Math.Max(0, r.Bytes))}"))
              + (rows.Count > 10 ? $"\n…… 还有 {rows.Count - 10} 个" : "");

        var pathBlock = new TextBlock
        {
            Text = lines, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
        };
        var pathBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Background = ThemeBrush.Get(null, "CardBackgroundFillColorSecondaryBrush"),
            Child = new ScrollViewer
            {
                MaxHeight = 190,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = pathBlock,
            },
        };
        panel.Children.Add(pathBorder);

        // ⚠️ 这条**必须**是醒目的警示色，不能是灰小字：永久删除，没有回收站兜底。
        panel.Children.Add(new TextBlock
        {
            Text = "\uE7BA  内容是永久删除、不进回收站，删掉就找不回来了。\n"
                 + "以上都是缓存文件，程序下次要用会自己重新生成。不会碰配置和账号数据，目录本身也会保留。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush.Get(null, "SystemFillColorCautionBrush"),
        });

        if (running.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"检测到 {string.Join("、", running)} 正在运行 —— 先关掉它再清理会更彻底，"
                     + "否则被占用的文件会被跳过。",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush.Get(null, "SystemFillColorCautionBrush"),
            });
        }

        return panel;
    }

    /// <summary>
    /// 清理结果的收尾说明。
    /// ⚠️ 讲的必须是**实际结果**，不是"我们打算做的事"。
    /// </summary>
    private static string BuildResultBody(
        int okCount, int failCount, long freed, int skipped, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();

        sb.Append(okCount == 1
            ? $"已清理 1 个目录，释放约 {AppLog.Size(freed)}。"
            : $"已清理 {okCount} 个目录，合计释放约 {AppLog.Size(freed)}。");

        if (skipped > 0)
            sb.Append($"\n有 {skipped} 项正被程序占用，已跳过 —— 关掉相关程序后可以再清一次。");

        if (failCount > 0)
        {
            sb.Append($"\n有 {failCount} 个目录没能清掉：");
            foreach (var note in notes.Take(5)) sb.Append("\n· " + note);
            if (notes.Count > 5) sb.Append($"\n· …… 还有 {notes.Count - 5} 条");
        }

        sb.Append("\n这些是缓存文件，程序下次要用会自己重新生成。");
        return sb.ToString();
    }

    private async Task InfoAsync(string title, string body)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }

    // ── 说明 ──────────────────────────────────────────────────────────

    private async void About_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "AppData 管理：能做什么、不能做什么",
            Content = BuildAboutBody(),
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
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

        Item("只读体检",
             $"扫的是 {AppDataScan.LocalRoot} 和 {AppDataScan.RoamingRoot} 下面的一级目录，"
             + "只列体积排行。这一层没有清理入口 —— 它的用处是回答「到底是谁在占地方」。");

        Item("可清理的只有这几类",
             "系统临时文件、浏览器缓存、商店应用缓存、开发工具缓存、图形着色器缓存。"
             + "判据是「删了应用自己会重建，最坏也只是下次访问重新下载」。"
             + "够不上这个判据的目录，一律不会出现在清理列表里。");

        Item("删除是永久的",
             "清理不进回收站，是直接把文件删掉 —— 所以按下去之前请看清路径。"
             + "之所以敢这么删：这一层清的全是缓存，程序下次要用会自己重新生成。"
             + "目录本身会保留（是清空内容，不是删掉目录）。");

        Item("绝不碰的东西",
             "一级目录永远不会被整目录删除，所以有些目录看着挺大也不会出现在清理列表里 —— 这是故意的。"
             + "典型例子：Programs 是程序安装本体，Tencent / QQ 里是聊天记录，"
             + "*\\Settings 和 *.db 是配置与账号数据。这些东西删掉不可恢复，不属于缓存，不提供清理入口。");

        Item("为什么有些清不掉",
             "正被程序占用的文件删不动，会被跳过，并且如实告诉你跳过了几项。"
             + "关掉相关程序（比如浏览器）再清理，能清得更干净。");

        Item("批量选择",
             "「可清理」分类下每一行都带复选框，底部动作条可以一次处理多项。"
             + "「全选」只勾选体积已经统计完的项 —— 还在统计的暂时不能清，按钮上的数字里也就不含它们。"
             + "切换分类会清空勾选：跨分类保留勾选，只会让人记不清自己到底勾了哪一类。");

        Item("复制路径与搜索",
             "行尾「⋯」里可以复制完整路径，也可以调起系统默认浏览器在 Bing 上检索它。"
             + "检索词就是这一项的完整路径原样，不附加任何后缀 —— 由整条路径反查具体软件，"
             + "比只拿一段目录名去搜准得多。"
             + "两者都只读，不修改本机任何内容，任何一行都可以用。");

        Item("体积是怎么算的",
             "逐个文件累加得到，读不到的个别文件会跳过，所以数字可能略微偏小；"
             + "正被独占的文件统计不到，属于正常现象。");

        return panel;
    }
}
