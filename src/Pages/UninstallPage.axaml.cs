using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>分类下拉里的一项。</summary>
public sealed class CategoryOption
{
    public CategoryOption(AppCategory category, string glyph, string label, int count)
    {
        Category = category;
        Glyph = glyph;
        Label = label;
        Count = count;
    }

    public AppCategory Category { get; }
    public string Glyph { get; }
    public string Label { get; }
    public int Count { get; }

    public string CountText => Count.ToString("N0");
}

/// <summary>列表里的一行（把 <see cref="InstalledApp"/> 翻译成界面要的样子）。</summary>
public sealed class UninstallRow
{
    private IImage? _icon;
    private bool _iconResolved;

    public UninstallRow(InstalledApp app)
    {
        App = app;
        Name = app.Name;
        Version = app.Version;
        CategoryText = AppCategoryLook.LabelOf(app.Category);

        // 副标题：发布者。都取不到就退一步 —— 商店包的位置是 C:\Program Files\WindowsApps\…（一长串），
        // 不如显示包标识来得可读。
        var bits = new List<string>();
        var publisher = FriendlyPublisher(app.Publisher);
        if (publisher.Length > 0) bits.Add(publisher);
        if (bits.Count == 0 && app.PackageFullName.Length > 0)
            bits.Add(app.PackageFullName.Split('_')[0]);
        if (bits.Count == 0 && app.Location.Length > 0) bits.Add(app.Location);
        Sub = string.Join(" · ", bits);

        CanUninstall = AppUninstaller.CanUninstall(app);
        ActionTip = AppUninstaller.IsSelf(app) ? "该条目为本程序自身，不提供卸载"
            : AppUninstaller.IsProtected(app) ? "该条目为 Windows 系统组件，不提供卸载"
            : CanUninstall ? $"卸载「{app.Name}」"
            : "该记录未提供卸载命令，需手动处理";
    }

    public InstalledApp App { get; }
    public string Name { get; }
    public string Version { get; }
    public string Sub { get; }
    public bool CanUninstall { get; }
    public string ActionTip { get; }
    public string CategoryText { get; }

    /// <summary>软件自己的图标。**惰性取**：第一次被界面问到才去掏。</summary>
    public IImage? Icon
    {
        get
        {
            if (!_iconResolved)
            {
                _iconResolved = true;
                _icon = AppIconService.Get(App.IconPath);
            }

            return _icon;
        }
    }

    /// <summary>掏不到图标时的占位字形 —— 按分类给。</summary>
    public string PlaceholderGlyph => AppCategoryLook.GlyphOf(App.Category);

    /// <summary>发布者友好化：注册表里偶尔直接塞的是证书主题（CN=Microsoft Corporation, O=…），取 CN= 后面那一截。</summary>
    private static string FriendlyPublisher(string raw)
    {
        if (raw.Length == 0) return "";
        if (!raw.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return raw;

        var s = raw[3..];
        var comma = s.IndexOf(',');
        return (comma > 0 ? s[..comma] : s).Trim();
    }
}

/// <summary>
/// 应用卸载页（2026-10-12 加，挂在「实验性功能」下；2026-10-18 自上游 1.2.0 移植到 Avalonia）。
///
/// 主题是「以软件下载起步的程序，也管卸载」—— 把本机装了什么列出来，能直接卸。
/// 扫描走 <see cref="InstalledApps"/>，卸载走 <see cref="AppUninstaller"/>。
///
/// ⚠️ 扫描一定要丢后台：要遍历三个注册表视图的几百个键。
/// ⛔ 系统组件**永远不露**操作入口（后端两道闸 + 界面不给入口）。
/// </summary>
public partial class UninstallPage : Platform.PageBase
{
    private List<UninstallRow> _all = new();
    private AppCategory _category = AppCategory.Desktop;
    private bool _ready;
    private bool _loading;
    private bool _busy;

    /// <summary>残留状态：当前摆出来的那些行。</summary>
    private readonly List<LeftoverRowVm> _leftoverRows = new();

    /// <summary>残留状态：这次卸的是哪个软件（清理记录里要写）。</summary>
    private string _leftoverAppName = "";

    public UninstallPage()
    {
        InitializeComponent();

        // 两侧留白 / 标题行上的滚轮也转给列表（见 Core/WheelRelay.cs），在挂树后接线。
        AttachedToVisualTree += (_, _) => _ = LoadAsync();
    }

    // ══════════════════════════════════════════════════════
    //  扫描
    // ══════════════════════════════════════════════════════

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;

        LoadingPanel.IsVisible = true;
        AppListPanel.Children.Clear();
        EmptyPanel.IsVisible = false;
        RescanButton.IsEnabled = false;
        CountLine.Text = "正在扫描…";

        try
        {
            var apps = await Task.Run(() =>
            {
                var list = new List<InstalledApp>();
                list.AddRange(InstalledApps.Enumerate());              // 「程序和功能」
                list.AddRange(InstalledApps.EnumerateStorePackages()); // 商店应用
                return list.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
            });

            _all = apps.Select(a => new UninstallRow(a)).ToList();
            _ready = true;

            FillCategories();   // 计数在这会儿才算得出来
            ApplyFilter();

            // 图标预热：把"读文件掏图标"那一半丢后台先跑掉。
            _ = Task.Run(() =>
            {
                foreach (var a in apps) AppIconService.Prewarm(a.IconPath);
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, "扫描已安装软件");
            _all.Clear();
            _ready = true;
            AppListPanel.Children.Clear();
            EmptyPanel.IsVisible = true;
            EmptyText.Text = "扫描失败，请点击「重新扫描」后重试。";
            CountLine.Text = "";
        }
        finally
        {
            LoadingPanel.IsVisible = false;
            RescanButton.IsEnabled = true;
            _loading = false;
        }
    }

    /// <summary>把四个分类灌进下拉（带各自计数）。</summary>
    private void FillCategories()
    {
        var wanted = _category;

        var options = AppCategoryLook.Order
            .Select(c => new CategoryOption(c, AppCategoryLook.GlyphOf(c), AppCategoryLook.LabelOf(c),
                                           _all.Count(r => r.App.Category == c)))
            .ToList();

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
            panel.Children.Add(new TextBlock
            {
                Text = option.CountText, FontSize = 12, Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
            });

            CategoryBox.Items.Add(new ComboBoxItem { Content = panel, Tag = option });
        }

        // 优先沿用用户当前选的那一类；重扫后那一类若空了，退到第一个有内容的；
        // 全空（本机什么都没扫到）就停在第一项，别把下拉搞成空白。
        var pick = options.FirstOrDefault(o => o.Category == wanted)
                   ?? options.FirstOrDefault(o => o.Count > 0)
                   ?? options[0];

        CategoryBox.SelectedIndex = options.IndexOf(pick);
        _category = pick.Category;
    }

    private void ApplyFilter()
    {
        if (!_ready) return;   // 构造期 TextChanged / SelectionChanged 也会进来，那时还没数据

        var key = SearchBox.Text?.Trim() ?? "";
        var catName = AppCategoryLook.LabelOf(_category);

        var shown = _all
            .Where(r => r.App.Category == _category)
            .Where(r => key.Length == 0 ||
                        r.Name.Contains(key, StringComparison.CurrentCultureIgnoreCase) ||
                        r.Sub.Contains(key, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        AppListPanel.Children.Clear();
        foreach (var row in shown)
            AppListPanel.Children.Add(BuildAppRow(row));

        if (_all.Count == 0)
        {
            CountLine.Text = "未扫描到已安装的软件";
        }
        else if (key.Length > 0)
        {
            CountLine.Text = $"共 {_all.Count} 个应用 · 在{catName}中筛出 {shown.Count} 个";
        }
        else if (_category == AppCategory.System)
        {
            // 只读的一类：把"为什么没有卸载键"写在计数这行，省得用户找
            CountLine.Text = $"共 {_all.Count} 个应用 · {catName} {shown.Count} 个 · 系统组件，不提供卸载";
        }
        else
        {
            CountLine.Text = $"共 {_all.Count} 个应用 · {catName} {shown.Count} 个";
        }

        var empty = shown.Count == 0;
        EmptyPanel.IsVisible = empty;

        if (empty)
        {
            EmptyText.Text = _all.Count == 0
                ? "没有扫描到已安装的软件。"
                : key.Length == 0
                    ? $"「{catName}」这一类目前没有内容。"
                    : $"「{catName}」里没有名称或发布者包含「{key}」的应用。";
        }
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void Category_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // ⚠️ 重建下拉内容时 SelectedItem 会先变 null 再变新项 —— 那一下忽略掉
        if (CategoryBox.SelectedItem is not ComboBoxItem item || item.Tag is not CategoryOption option) return;
        _category = option.Category;
        ApplyFilter();
    }

    private void Rescan_Click(object? sender, RoutedEventArgs e) => _ = LoadAsync();

    // ══════════════════════════════════════════════════════
    //  应用列表的行
    // ══════════════════════════════════════════════════════

    private Control BuildAppRow(UninstallRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标：能掏到就用软件自己的（多数人认图不认字），掏不到退成分类占位字形。
        var iconHost = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        var icon = row.Icon;
        if (icon is not null)
        {
            iconHost.Children.Add(new Image
            {
                Source = icon, Width = 32, Height = 32, Stretch = Stretch.Uniform,
            });
        }
        else
        {
            iconHost.Children.Add(new FontIcon
            {
                Glyph = row.PlaceholderGlyph, FontSize = 16, Opacity = 0.4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        grid.Children.Add(iconHost);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(12, 0, 0, 0);
        text.Children.Add(new TextBlock
        {
            Text = row.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        });
        var sub = new TextBlock
        {
            Text = row.Sub, FontSize = 12, Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        ToolTip.SetTip(sub, row.Sub);
        text.Children.Add(sub);
        grid.Children.Add(text);

        var version = new TextBlock
        {
            Text = row.Version, FontSize = 12, Opacity = 0.6,
            Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(version, 2);
        grid.Children.Add(version);

        // 行尾：一朵透明的 ⋯，点开才是「卸载」。⛔ 系统组件连这朵都不给。
        if (row.CanUninstall)
        {
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
            more.Click += (_, _) => ShowAppMenu(more, row);
            Grid.SetColumn(more, 3);
            grid.Children.Add(more);
        }

        return grid;
    }

    /// <summary>行尾「⋯」菜单：手建，用闭包直接捕获这一行。</summary>
    private void ShowAppMenu(Control anchor, UninstallRow row)
    {
        if (!row.CanUninstall) return;

        var uninstall = new MenuItem { Header = "卸载" };
        uninstall.Click += (_, _) => _ = UninstallAsync(row);

        var flyout = new MenuFlyout();
        flyout.Items.Add(uninstall);
        flyout.ShowAt(anchor);
    }

    // ══════════════════════════════════════════════════════
    //  卸载
    // ══════════════════════════════════════════════════════

    private async Task UninstallAsync(UninstallRow row)
    {
        if (_busy) return;

        var dialog = new ContentDialog
        {
            Title = $"卸载「{row.Name}」？",
            Content = BuildConfirmBody(row),
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            // ⚠️ 默认落在「取消」上：这是不可逆操作，别让一个回车就卸掉东西
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        RescanButton.IsEnabled = false;
        SearchBox.IsEnabled = false;
        CategoryBox.IsEnabled = false;
        CountLine.Text = $"正在卸载「{row.Name}」…";

        try
        {
            // 卸载 + 残留体检（前后两份浅层快照求差集，见 Services/AppUninstaller.UninstallAndScanAsync）
            var run = await AppUninstaller.UninstallAndScanAsync(row.App);

            // 卸载程序退出了不等于登记表就更新了（有些要等一两拍）→ 稍等再重扫
            await Task.Delay(1200);
            await LoadAsync();

            // ⚠️ 提示必须放在 LoadAsync **之后** —— 它会把 CountLine 重写成统计值
            if (!run.Outcome.Started)
            {
                CountLine.Text = run.Outcome.Message;
                await InfoAsync("无法启动卸载程序",
                    run.Outcome.Message
                    + "\n\n可以改用系统「设置 → 应用」来卸载；"
                    + "也可以确认该软件的卸载程序是否仍在其安装目录中。");
                return;
            }

            var stillThere = _all.Any(r =>
                string.Equals(r.Name, row.Name, StringComparison.CurrentCultureIgnoreCase));
            var stillThereNote =
                $"「{row.Name}」仍存在于已安装列表：卸载可能已取消、失败，或需重启后方可完成。"
                + "为避免误删其正在使用的数据，本次不执行残留清理。";

            if (!run.Scanned)
            {
                // 没做体检（超时 / 用户取消了 UAC 之后的回落）—— 如实说，别假装"没有残留"
                CountLine.Text = run.Outcome.Message;
                await InfoAsync("未能完成残留检查",
                    run.Outcome.Message + "\n\n本次没有检查残留，也没有移除任何东西。");
                return;
            }

            if (stillThere)
            {
                // ⛔⛔ 卸载记录**还在**（卸载被取消 / 失败 / 要重启才生效）⇒ 一律不进残留状态。
                //     这时候"名字对得上的痕迹"里有一大半是这个软件**正在用的活数据**，
                //     清掉就等于把一个还在装的软件拆坏。
                CountLine.Text = stillThereNote;
                await InfoAsync("卸载未完成", stillThereNote);
                return;
            }

            if (run.Leftovers.Count == 0)
            {
                // ⚠️ 措辞是「没有扫描到」，⛔ 不是「没有」。
                CountLine.Text = $"「{row.Name}」已卸载 · 没有扫描到残留";
                await InfoAsync("卸载完成",
                    $"「{row.Name}」已卸载，没有扫描到残留的文件或注册表项。\n\n"
                    + "如需一并查看它留在 AppData 里的缓存目录，可以到「AppData 管理」中查找。");
                return;
            }

            EnterLeftoverState(row.Name, run.Leftovers);
        }
        finally
        {
            _busy = false;
            RescanButton.IsEnabled = true;
            SearchBox.IsEnabled = true;
            CategoryBox.IsEnabled = true;
        }
    }

    /// <summary>一句话的提示框（四种不进入残留确认的结局都要有看得见的落点）。</summary>
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

    // ══════════════════════════════════════════════════════
    //  状态二：卸载残留确认
    // ══════════════════════════════════════════════════════

    private sealed class LeftoverRowVm
    {
        public LeftoverItem Item { get; }
        public CheckBox Check { get; }

        public LeftoverRowVm(LeftoverItem item, CheckBox check)
        {
            Item = item;
            Check = check;
        }
    }

    private void EnterLeftoverState(string appName, IReadOnlyList<LeftoverItem> items)
    {
        _leftoverAppName = appName;
        _leftoverRows.Clear();
        LeftoverListPanel.Children.Clear();

        foreach (var item in items)
        {
            var vm = BuildLeftoverRow(item);
            _leftoverRows.Add(vm);
            LeftoverListPanel.Children.Add(BuildLeftoverVisual(vm));
        }

        TitleText.Text = "卸载残留";
        SetRiskBar(leftover: true);

        // 顶栏换岗：列表那一组收起来，露出「返回列表」
        CategoryBox.IsVisible = false;
        SearchBox.IsVisible = false;
        RescanButton.IsVisible = false;
        LeftoverEntryButton.IsVisible = false;
        BackButton.IsVisible = true;

        var any = _leftoverRows.Count > 0;

        if (any)
        {
            var strong = items.Count(i => i.Evidence == LeftoverEvidence.Strong);
            var medium = items.Count(i => i.Evidence == LeftoverEvidence.Medium);
            var weak = items.Count(i => i.Evidence == LeftoverEvidence.Weak);

            CountLine.Text = $"「{appName}」已卸载 · 发现 {items.Count} 项残留";
            if (strong + medium > 0 || weak > 0)
                CountLine.Text += $"（确定 {strong} · 很可能 {medium} · 不确定 {weak}）";
        }
        else
        {
            // 主动进来：⛔ 别拼出"「」已卸载 · 发现 0 项残留"那种怪句子
            CountLine.Text = "当前没有待处理的残留项";
        }

        LeftoverEmpty.IsVisible = !any;
        LeftoverBar.IsVisible = any;

        ListHost.IsVisible = false;
        LeftoverHost.IsVisible = true;
        UpdateLeftoverBar();

        AppLog.Info("leftover", $"进入残留确认：{appName}，{items.Count} 项（默认勾选 {_leftoverRows.Count(r => r.Check.IsChecked == true)} 项）");
    }

    /// <summary>建一行的勾选框 + 行对象（勾选变化驱动底部统计）。</summary>
    private LeftoverRowVm BuildLeftoverRow(LeftoverItem item)
    {
        var check = new CheckBox
        {
            IsChecked = item.Selected,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        ToolTip.SetTip(check, "勾选这一项");
        check.IsCheckedChanged += (_, _) =>
        {
            item.Selected = check.IsChecked == true;
            UpdateLeftoverBar();
        };

        return new LeftoverRowVm(item, check);
    }

    private Control BuildLeftoverVisual(LeftoverRowVm vm)
    {
        var item = vm.Item;

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(vm.Check);

        var bits = new List<string> { item.KindText };
        if (item.SizeText.Length > 0) bits.Add(item.SizeText);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(12, 0, 0, 0);
        text.Children.Add(new TextBlock
        {
            Text = item.DisplayLeaf, FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        });
        var loc = new TextBlock
        {
            Text = item.DisplayParent, FontSize = 12, Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        ToolTip.SetTip(loc, item.DisplayPath);
        text.Children.Add(loc);
        grid.Children.Add(text);

        var meta = new TextBlock
        {
            Text = string.Join(" · ", bits), FontSize = 12, Opacity = 0.6,
            Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(meta, 2);
        grid.Children.Add(meta);

        // 证据徽标：三档各一色（走动态资源，主题切换时跟着变）
        var pillHost = new Grid();
        var reason = item.Reason.Length > 0 ? item.Reason : "无可用判定依据";
        pillHost.Children.Add(MakePill("确定", "SystemFillColorSuccessBrush", item.Evidence == LeftoverEvidence.Strong, reason));
        pillHost.Children.Add(MakePill("很可能", "SystemFillColorCautionBrush", item.Evidence == LeftoverEvidence.Medium, reason));
        pillHost.Children.Add(MakePill("不确定", "TextFillColorTertiaryBrush", item.Evidence == LeftoverEvidence.Weak, reason));
        Grid.SetColumn(pillHost, 3);
        grid.Children.Add(pillHost);

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
        more.Click += (_, _) => ShowLeftoverMenu(more, item);
        Grid.SetColumn(more, 4);
        grid.Children.Add(more);

        return grid;
    }

    private static Border MakePill(string label, string brushKey, bool visible, string reason)
    {
        var brush = ThemeBrush.Get(null, brushKey);
        var pill = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = visible,
            BorderBrush = brush,
            Child = new TextBlock { Text = label, FontSize = 11, Foreground = brush },
        };
        ToolTip.SetTip(pill, reason);
        return pill;
    }

    /// <summary>「残留与隔离区」：**主动进入**残留界面。</summary>
    private void LeftoverEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // ⚠️ 已经在残留态就别再切一次（会把它重置成空态，把用户正在勾的清单冲掉）
        if (LeftoverHost.IsVisible) return;

        EnterLeftoverState("", Array.Empty<LeftoverItem>());
    }

    /// <summary>「查看隔离区」：列出历次清理留下的备份记录。⛔ 只读。</summary>
    private async void Quarantine_Click(object? sender, RoutedEventArgs e)
    {
        var entries = QuarantineStore.Scan();
        var body = new StackPanel { Spacing = 10 };

        if (entries.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"隔离区里还没有记录。\n\n位置：{QuarantineStore.Root}",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = $"共 {entries.Count} 条清理记录。移除的内容均备份在这里，可随时取回。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });

            var host = new StackPanel { Spacing = 8 };
            foreach (var entry in entries) host.Children.Add(BuildQuarantineRow(entry));

            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = host,
            });
        }

        var dialog = new ContentDialog
        {
            Title = "隔离区",
            Content = body,
            CloseButtonText = "关闭",
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Exception("quarantine", ex, "打开隔离区对话框");
        }
    }

    /// <summary>隔离区列表的一行：左＝软件名与时间，右＝打开文件夹。</summary>
    private Control BuildQuarantineRow(QuarantineEntry entry)
    {
        var row = new StackPanel { Spacing = 2 };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        var open = new Button { Content = "打开文件夹" };
        open.Click += (_, _) =>
        {
            if (!QuarantineStore.OpenInExplorer(entry.Dir))
                _ = InfoAsync("没能打开", $"隔离区目录可能已被移走或删除：\n{entry.Dir}");
        };
        Grid.SetColumn(open, 1);
        open.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(open);

        row.Children.Add(head);
        row.Children.Add(new TextBlock
        {
            Text = $"{entry.TimeText}　·　{entry.SubText}",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        return row;
    }

    /// <summary>回到应用列表状态。⛔ 什么都**没删** —— 「不用了」和「返回列表」走的是同一条路。</summary>
    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ExitLeftoverState();
        _ = LoadAsync();
    }

    private void ExitLeftoverState()
    {
        _leftoverRows.Clear();
        _leftoverAppName = "";

        LeftoverListPanel.Children.Clear();
        LeftoverEmpty.IsVisible = false;
        LeftoverHost.IsVisible = false;
        ListHost.IsVisible = true;

        TitleText.Text = "应用卸载";
        SetRiskBar(leftover: false);
        CategoryBox.IsVisible = true;
        SearchBox.IsVisible = true;
        RescanButton.IsVisible = true;
        LeftoverEntryButton.IsVisible = true;
        BackButton.IsVisible = false;
    }

    /// <summary>顶部那条风险提示的两个文案。⛔ 只在两个状态**之间**换，不新增控件。</summary>
    private void SetRiskBar(bool leftover)
    {
        if (leftover)
        {
            RiskBar.Title = "仅移除已勾选的项目";
            RiskBar.Message = "标注为「很可能」「不确定」的项目默认不勾选：其判定依据仅为名称或位置相似，"
                            + "不排除属于其他软件，请逐项确认后再行处理。";
        }
        else
        {
            RiskBar.Title = "此操作将删除文件与注册表项";
            RiskBar.Message = "清理卸载残留会移除文件与注册表项。被移除的内容均先行备份至隔离区，可在「隔离区」中查看并取回。";
        }
    }

    /// <summary>底部那条：已选几项、大约多少体积；主按钮的字数也跟着变。</summary>
    private void UpdateLeftoverBar()
    {
        var chosen = _leftoverRows.Where(r => r.Check.IsChecked == true).ToList();
        var size = chosen.Where(r => r.Item.SizeBytes > 0).Sum(r => r.Item.SizeBytes);

        CleanButton.Content = chosen.Count == 0 ? "清理选中" : $"清理选中 ({chosen.Count})";
        CleanButton.IsEnabled = chosen.Count > 0;

        var weakNote = chosen.Count(r => r.Item.Evidence == LeftoverEvidence.Weak);
        var text = chosen.Count == 0
            ? "未勾选任何项目"
            : $"已选 {chosen.Count} 项" + (size > 0 ? $" · 约 {HumanSize(size)}" : "");

        // 不确定的那些要额外提醒一句 —— 它们是"名字对不上"的，勾了就是用户自己的判断
        if (weakNote > 0) text += $"（其中 {weakNote} 项为「不确定」）";

        LeftoverSummary.Text = text;
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    /// <summary>执行清理。⚠️ 真正的删除在后台线程上跑。</summary>
    private async void Clean_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var chosen = _leftoverRows.Where(r => r.Check.IsChecked == true).Select(r => r.Item).ToList();
        if (chosen.Count == 0) return;

        var size = chosen.Where(i => i.SizeBytes > 0).Sum(i => i.SizeBytes);

        var confirm = new ContentDialog
        {
            Title = $"确认清理 {chosen.Count} 项残留？",
            Content = BuildCleanConfirmBody(chosen.Count, size),
            PrimaryButtonText = "清理",
            CloseButtonText = "取消",
            // 不可逆操作，默认落在取消上
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        CleanButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        CleanButton.Content = "正在清理…";
        LeftoverSummary.Text = "正在清理（需要管理员权限的项目将再次弹出系统授权提示）…";

        CleanupReport report;
        try
        {
            report = await Task.Run(() => LeftoverCleaner.CleanAsync(chosen, _leftoverAppName));
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, "清理卸载残留");
            report = null!;
        }
        finally
        {
            _busy = false;
            BackButton.IsEnabled = true;
        }

        if (report is null)
        {
            LeftoverSummary.Text = "清理过程中出错，未执行任何移除。";
            UpdateLeftoverBar();
            return;
        }

        // 汇报：清完了什么、还剩什么、后悔药在哪
        var dialog = new ContentDialog
        {
            Title = "清理完成",
            Content = BuildReportBody(report),
            PrimaryButtonText = report.QuarantineDir.Length > 0 ? "打开隔离区" : "",
            CloseButtonText = "关闭",
            DefaultButton = report.QuarantineDir.Length > 0
                ? ContentDialogButton.Primary
                : ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && report.QuarantineDir.Length > 0)
            OpenInExplorer(report.QuarantineDir);

        ExitLeftoverState();
        await LoadAsync();
    }

    /// <summary>行尾「⋯」：给残留行用的（打开所在文件夹 / 复制路径）。</summary>
    private void ShowLeftoverMenu(Control anchor, LeftoverItem item)
    {
        var flyout = new MenuFlyout();

        // 文件/文件夹才有"打开所在位置"；注册表项没有可靠的"跳过去"命令行参数。
        var openable = item.Kind == FootprintKind.Path
                       && (File.Exists(item.Path) || Directory.Exists(item.Path));

        if (openable)
        {
            var open = new MenuItem { Header = "打开所在文件夹" };
            open.Click += (_, _) => RevealInExplorer(item.Path);
            flyout.Items.Add(open);
        }

        var copy = new MenuItem { Header = "复制完整路径" };
        copy.Click += (_, _) => CopyText(item.DisplayPath);
        flyout.Items.Add(copy);

        flyout.ShowAt(anchor);
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, $"打开文件夹（{path}）");
        }
    }

    /// <summary>在资源管理器里**选中**这个文件/文件夹（不是打开它）。</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            var args = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = args,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, $"定位到文件夹（{path}）");
        }
    }

    private void CopyText(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            _ = clipboard?.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            AppLog.Exception("uninstall", ex, "复制路径到剪贴板");
        }
    }

    // ══════════════════════════════════════════════════════
    //  说明 / 确认框正文
    // ══════════════════════════════════════════════════════

    private async void About_Click(object? sender, RoutedEventArgs e)
    {
        var inLeftover = LeftoverHost.IsVisible;

        var dialog = new ContentDialog
        {
            Title = inLeftover ? "残留判定说明" : "扫描范围与注意事项",
            Content = inLeftover ? BuildLeftoverAboutBody() : BuildAboutBody(),
            CloseButtonText = "关闭",
        };

        await dialog.ShowAsync();
    }

    /// <summary>残留状态下的说明：讲清判据与边界，别让人以为这是"灵媒"。</summary>
    private static Control BuildLeftoverAboutBody()
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

        Item("判定方法",
             "卸载开始前，先记录一批常见位置的第一级内容（用户数据目录、程序目录、开始菜单、"
             + "注册表中的软件项、开机自启项、文件关联等）；卸载程序退出后再记录一次，"
             + "取两次记录的差集作为本次卸载新增的内容。该判据无需预先准备任何材料。");

        Item("证据等级的含义",
             "「确定」：名称与软件名一致，或位置位于该软件的安装目录之内。"
             + "「很可能」：名称与程序文件名一致。"
             + "「不确定」：仅符合「卸载过程中新增」，而名称不符。"
             + "仅「确定」一级默认勾选，其余需自行确认。");

        Item("「不确定」档的成因",
             "痕迹嵌套于两级目录之下时（如「厂商名\\软件名」），浅层扫描只能取到外层目录，名称自然不符；"
             + "卸载程序顺带写入的其他内容同样归入此档。");

        Item("清理前的处理",
             "先行备份，再行移除：注册表项导出为标准 .reg 文件（双击即可导回），文件原样移入隔离区目录。"
             + "两者均存放于本程序数据目录的 quarantine 下，不参与自动清理，可在「隔离区」中查看并取回。");

        Item("保护范围",
             "Windows 目录、程序目录下的公共组件（Common Files、WindowsApps、系统组件目录）、"
             + "Microsoft 与组策略的注册表区域、文件关联的系统键（CLSID、TypeLib 等），"
             + "以及本程序自身的数据目录。该名单为代码内固定常量，界面勾选不改变其判定结果。");

        return panel;
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

        Item("扫描范围",
             "Windows「程序和功能」的安装记录（64 位、32 位、当前用户三处），以及本账户注册的 Microsoft Store 应用。"
             + "免安装（绿色版）软件无安装记录，不会出现在列表中。");

        Item("卸载方式",
             "调用软件自带的卸载程序；需要管理员权限时将弹出系统授权提示。"
             + "本程序仅负责发起调用，不删除文件、不修改注册表。");

        Item("分类",
             "桌面应用：传统安装包。运行库：VC++、.NET 等供其他软件依赖的组件，卸载前请确认无程序正在使用。"
             + "微软应用商店：本账户注册的商店应用。系统组件：Windows 自带的部件。");

        Item("系统组件的限制",
             "该类包含「文件资源管理器」「开始」「设置」「讲述人」等 Windows 自身部件，移除后对应系统功能将直接失效。"
             + "因此该类仅作查看：界面不提供卸载入口，后端亦拒绝执行。");

        Item("可能的失败",
             "部分软件会拒绝卸载或要求重启，此类限制来自软件自身。"
             + "卸载前请先退出该软件，并保存正在编辑的内容。");

        return panel;
    }

    /// <summary>清理确认框的正文：说清"会发生什么、后悔药在哪"。</summary>
    private static Control BuildCleanConfirmBody(int count, long size)
    {
        var panel = new StackPanel { Spacing = 8 };

        void Line(string text, double opacity)
            => panel.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, Opacity = opacity, TextWrapping = TextWrapping.Wrap,
            });

        Line($"将移除 {count} 项残留条目" + (size > 0 ? $"，合计约 {HumanSize(size)}" : "") + "。", 0.9);

        Line("移除前先行备份：注册表项导出为标准 .reg 文件，文件原样移入隔离区，"
             + "两者均位于本程序数据目录的 quarantine 下，可在「隔离区」中查看并取回。", 0.7);

        Line("仅移除已勾选的位置，不涉及任何系统目录与系统注册表项。"
             + "该限制为代码内固定常量，界面勾选不改变其判定结果。", 0.7);

        Line("清理操作不可撤销（备份仅供还原）。请先确认目标软件确已卸载完成。", 0.7);

        return panel;
    }

    /// <summary>清理结果：实际删掉什么、还剩什么、备份在哪。</summary>
    private static Control BuildReportBody(CleanupReport report)
    {
        var panel = new StackPanel { Spacing = 8 };

        void Line(string text, double opacity)
            => panel.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, Opacity = opacity, TextWrapping = TextWrapping.Wrap,
            });

        Line($"已移除 {report.RemovedTotal} 项（共提交 {report.Results.Count} 项）。", 0.9);

        var notBackedUp = report.Count(CleanStatus.RemovedNoBackup);
        if (notBackedUp > 0)
            Line($"其中 {notBackedUp} 项因体积超过备份上限（2 GB）未留存备份，已直接删除。", 0.7);

        var failed = report.Count(CleanStatus.Failed);
        if (failed > 0)
            Line($"{failed} 项移除失败，通常因被运行中的程序占用，或需重启后重试。"
                 + "相关条目仍列于本页，可稍后重新处理。", 0.7);

        var blocked = report.Count(CleanStatus.Blocked);
        if (blocked > 0)
            Line($"{blocked} 项位于受保护位置，已拒绝执行。", 0.7);

        // ⛔ 这一条必须写：报告讲的是"实际结果"，不是"我们打算做的事"
        var stillThere = report.Results.Count(r => r.Item.Selected && r.Item.StillThere);
        if (stillThere > 0)
            Line($"复扫结果：仍有 {stillThere} 项存在于原位置。", 0.7);

        if (report.QuarantineDir.Length > 0)
        {
            Line("备份位置：" + report.QuarantineDir, 0.7);
            Line("· registry-backup.reg —— 双击可导入注册表项", 0.6);
            Line("· files\\ —— 被移出的文件原样保存，剪切回原位置即可", 0.6);
        }
        else
        {
            Line("本轮隔离区未能建立，因此未留存备份。", 0.8);
        }

        return panel;
    }

    /// <summary>确认框正文：把"要动的是哪个软件"摆清楚，再说清接下来会发生什么。</summary>
    private static Control BuildConfirmBody(UninstallRow row)
    {
        var panel = new StackPanel { Spacing = 8 };

        void Line(string text, double opacity)
            => panel.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, Opacity = opacity, TextWrapping = TextWrapping.Wrap,
            });

        panel.Children.Add(new TextBlock
        {
            Text = row.Name, FontSize = 15, TextWrapping = TextWrapping.Wrap,
        });

        if (row.Version.Length > 0) Line($"版本 {row.Version}", 0.75);
        if (row.Sub.Length > 0) Line(row.Sub, 0.6);
        Line(row.CategoryText, 0.6);

        Line(row.App.IsStore
            ? "将按 Microsoft Store 应用方式移除（等同于系统「设置 → 应用」中的卸载操作）。"
            : "将调用该软件自带的卸载程序。过程中可能弹出其自身界面及系统管理员授权提示。", 0.75);

        // 运行库不是"用户主动装的应用"，卸掉多半会连累别的软件 —— 这里必须多一句
        if (row.App.Category == AppCategory.Runtime)
            Line("⚠ 该项目属于运行库：其他软件可能依赖它，移除后将导致相关软件启动失败。请确认无程序依赖后再继续。", 0.85);

        Line("卸载不可撤销，请先退出该软件并保存正在编辑的内容。", 0.75);

        return panel;
    }
}
