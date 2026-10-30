using System;
using System.Collections.Generic;
using System.IO;
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

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 磁盘空间可视化：squarified treemap（见 <see cref="TreemapLayout"/>）+ 逐层下钻。
/// （2026-10-18 自上游 1.2.0 移植，WinUI → Avalonia。）
///
/// <para>
/// ⛔ 扫描一律在后台线程跑，并且**可取消** —— 全盘几十万文件，扫到一半又不想等了是很正常的操作。
/// ⛔ 树图上的方块最多画 90 个，其余并成「其他」：再多既点不中也刷不动。
/// </para>
/// </summary>
public partial class DiskMapPage : Platform.PageBase
{
    /// <summary>一次最多画多少块（超过就并成「其他」）。</summary>
    private const int MaxTiles = 90;

    private CancellationTokenSource? _cts;
    private DiskScanResult? _result;
    private string _currentPath = "";
    private bool _initialized;

    public DiskMapPage()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            if (_initialized) return;

            // ⚠️ 顺序很重要：FillDrives 里会设置 SelectedIndex，若先把 _initialized 置真，
            //    SelectionChanged 会再起一次扫描，紧接着下面又起一次 —— 两次扫描互相取消，
            //    前一次的 catch/finally 还会把后一次的"正在统计"状态冲掉。
            FillDrives();
            _initialized = true;
        };

        // 离开页面就把扫描停掉，别让后台线程继续啃硬盘
        DetachedFromVisualTree += (_, _) => _cts?.Cancel();
    }

    // ══════════════════════════════════════════════════════
    //  启动
    // ══════════════════════════════════════════════════════

    private void FillDrives()
    {
        var drives = DiskUsage.FixedDrives();
        if (drives.Count == 0)
        {
            StatusText.Text = "没有可扫描的磁盘。";
            return;
        }

        foreach (var drive in drives)
        {
            var total = drive.TotalSize > 0 ? DiskUsage.FormatBytes(drive.TotalSize) : "未知";
            var free = drive.AvailableFreeSpace > 0 ? DiskUsage.FormatBytes(drive.AvailableFreeSpace) : "未知";

            DriveBox.Items.Add(new ComboBoxItem
            {
                Content = $"{drive.Name.TrimEnd('\\')}　可用 {free} / 共 {total}",
                Tag = drive.RootDirectory.FullName,
            });
        }

        // 默认从**当前用户的目录**开始：一上来就扫整个 C:\ 要等很久，
        // 而绝大多数人想找的就是自己目录里的大文件
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeRoot = Path.GetPathRoot(home) ?? "";
        var index = 0;

        for (var i = 0; i < DriveBox.Items.Count; i++)
        {
            if (DriveBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, homeRoot, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        DriveBox.SelectedIndex = index;

        var start = Directory.Exists(home) ? home : drives[0].RootDirectory.FullName;
        _ = ScanAsync(start);
    }

    private void DriveBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (DriveBox.SelectedItem is not ComboBoxItem item || item.Tag is not string root) return;

        _ = ScanAsync(root);
    }

    // ══════════════════════════════════════════════════════
    //  扫描
    // ══════════════════════════════════════════════════════

    private async Task ScanAsync(string path)
    {
        // ⚠️ 上一轮的 CTS 只 Cancel 不 Dispose：此刻它可能还挂在某个 await 上，
        //    立刻 Dispose 会让取消回调抛 ObjectDisposedException；由它自己的 finally 释放。
        _cts?.Cancel();

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        // 被新一轮扫描接替后，这一轮就只是"退出"，不许再碰任何 UI ——
        // 否则它的 catch/finally 会把新一轮的转圈和按钮状态复位掉（审计 P2 #29）。
        bool IsCurrent() => ReferenceEquals(_cts, cts);

        _currentPath = path;
        UpdateCrumb();

        BusyRing.IsActive = true;
        ScanButton.Content = "取消";
        StatusText.Text = "正在统计…（大目录可能要十几秒）";
        EmptyText.IsVisible = false;
        MapCanvas.Children.Clear();
        TopList.Children.Clear();

        try
        {
            var result = await DiskUsage.ScanAsync(path, token, (dir, bytes, files) =>
            {
                // 进度来自后台线程 ⇒ 回 UI 线程再改文字
                if (!Dispatcher.UIThread.CheckAccess())
                {
                    Dispatcher.UIThread.Post(() => ShowProgress(dir, bytes, files));
                    return;
                }

                ShowProgress(dir, bytes, files);
            });

            if (token.IsCancellationRequested || !IsCurrent()) return;

            _result = result;
            Render(result);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent()) StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;

            AppLog.Exception("diskmap", ex, "扫描目录占用");
            StatusText.Text = "读取这个位置失败：" + ex.Message;
            EmptyText.Text = "读取失败：可能没有访问权限。\r\n可以选择别的目录，或用管理员身份运行。";
            EmptyText.IsVisible = true;
        }
        finally
        {
            if (IsCurrent())
            {
                BusyRing.IsActive = false;
                ScanButton.Content = "重新扫描";
                _cts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>扫描中的进度：当前正在数哪个目录、已经数到多少。</summary>
    private void ShowProgress(string dir, long bytes, int files)
    {
        BusyRing.IsActive = true;
        StatusText.Text = $"正在统计 {DiskUsage.FormatBytes(bytes)}（{files:N0} 个文件）　{Shorten(dir)}";
    }

    private static string Shorten(string path)
    {
        const int max = 64;
        if (path.Length <= max) return path;
        return "…" + path[^max..];
    }

    private void Scan_Click(object? sender, RoutedEventArgs e)
    {
        // 扫描中再点一次 = 取消
        if (_cts is { IsCancellationRequested: false } && BusyRing.IsActive)
        {
            _cts.Cancel();
            StatusText.Text = "正在停止…";
            return;
        }

        if (_currentPath.Length > 0) _ = ScanAsync(_currentPath);
    }

    private async void Pick_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            var picked = await FilePickerCompat.PickFolderAsync(top, "选择要统计的文件夹");
            if (picked.Files.Count == 0) return;

            _ = ScanAsync(picked.Files[0]);
        }
        catch (Exception ex)
        {
            AppLog.Exception("diskmap", ex, "选择文件夹");
            StatusText.Text = "无法打开文件夹选择器。";
        }
    }

    // ══════════════════════════════════════════════════════
    //  渲染
    // ══════════════════════════════════════════════════════

    private void Render(DiskScanResult result)
    {
        var totalText = DiskUsage.FormatBytes(result.TotalBytes);
        var skipped = result.SkippedDirectories > 0
            ? $"，{result.SkippedDirectories} 个目录无权限已跳过"
            : "";

        StatusText.Text = $"{totalText}　·　{result.FileCount:N0} 个文件　·　{result.Elapsed.TotalSeconds:0.0} 秒{skipped}";

        if (result.Children.Count == 0)
        {
            EmptyText.Text = "这个位置没有可统计的内容（空目录，或全部没有访问权限）。";
            EmptyText.IsVisible = true;
            MapCanvas.Children.Clear();
            return;
        }

        EmptyText.IsVisible = false;
        RenderTopList(result);
        DrawTreemap(result);
    }

    private void DrawTreemap(DiskScanResult result)
    {
        MapCanvas.Children.Clear();

        var width = MapCanvas.Bounds.Width;
        var height = MapCanvas.Bounds.Height;
        if (width < 20 || height < 20) return;

        // 超过 MaxTiles 就并成「其他」：再多既点不中也刷不动
        var items = result.Children.ToList();
        var others = new List<DiskEntry>();

        if (items.Count > MaxTiles)
        {
            others = items.Skip(MaxTiles - 1).ToList();
            items = items.Take(MaxTiles - 1).ToList();
        }

        var weights = items.Select(i => (double)i.Bytes).ToList();
        if (others.Count > 0)
            weights.Add(others.Sum(o => (double)o.Bytes));

        var tiles = TreemapLayout.Squarify(weights, 0, 0, width - 4, height - 4);

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (tile.Width < 3 || tile.Height < 3) continue;

            var isOther = i == items.Count;
            var entry = isOther ? null : items[i];

            var border = BuildTile(tile, entry, isOther, others);
            MapCanvas.Children.Add(border);
        }
    }

    private Border BuildTile(TreemapLayout.Tile tile, DiskEntry? entry, bool isOther, List<DiskEntry> others)
    {
        var color = TileColor(Math.Abs((entry?.FullPath ?? "其他").GetHashCode()) % 360);

        var border = new Border
        {
            Width = Math.Max(2, tile.Width - 2),
            Height = Math.Max(2, tile.Height - 2),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(color),
        };

        Canvas.SetLeft(border, tile.X + 1);
        Canvas.SetTop(border, tile.Y + 1);

        var name = isOther ? $"其他 {others.Count} 项" : entry!.Name;
        var bytes = isOther ? others.Sum(o => o.Bytes) : entry!.Bytes;

        // 方块太小就只上色，不塞文字（塞了也是被裁一半的残字）
        if (tile.Width > 62 && tile.Height > 30)
        {
            var panel = new StackPanel { Margin = new Thickness(7, 5, 7, 5), Spacing = 1 };

            panel.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });

            if (tile.Height > 46)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = DiskUsage.FormatBytes(bytes),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                });
            }

            border.Child = panel;
        }

        ToolTip.SetTip(border, isOther
            ? $"其余 {others.Count} 项，合计 {DiskUsage.FormatBytes(bytes)}"
            : $"{entry!.FullPath}\r\n{DiskUsage.FormatBytes(bytes)}　{entry.FileCount:N0} 个文件" +
              (entry.IsDirectory ? "\r\n单击进入下一层" : ""));

        if (entry is { IsDirectory: true })
        {
            border.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                _ = ScanAsync(entry.FullPath);
            };

            border.PointerEntered += (_, _) => border.Opacity = 0.82;
            border.PointerExited += (_, _) => border.Opacity = 1;
        }

        return border;
    }

    /// <summary>按色相给方块上色：同一目录下颜色各不相同，深浅统一，深色主题上也压得住。</summary>
    private static Color TileColor(int hue)
    {
        var h = hue % 360;
        const double s = 0.42;
        const double v = 0.62;

        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromArgb(0xE6,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private void RenderTopList(DiskScanResult result)
    {
        TopList.Children.Clear();
        if (result.TotalBytes <= 0) return;

        foreach (var entry in result.Children.Take(5))
        {
            var percent = entry.Bytes * 100.0 / result.TotalBytes;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.85,
            });

            var right = new TextBlock
            {
                Text = $"{DiskUsage.FormatBytes(entry.Bytes)}　{percent:0.0}%",
                FontSize = 12,
                Opacity = 0.55,
            };
            Grid.SetColumn(right, 1);
            right.Margin = new Thickness(12, 0, 0, 0);
            row.Children.Add(right);

            TopList.Children.Add(row);
        }
    }

    // ══════════════════════════════════════════════════════
    //  面包屑
    // ══════════════════════════════════════════════════════

    private void UpdateCrumb()
    {
        CrumbBar.Children.Clear();

        if (_currentPath.Length == 0) return;

        var root = Path.GetPathRoot(_currentPath) ?? _currentPath;
        var rest = _currentPath.Length > root.Length ? _currentPath[root.Length..] : "";
        var parts = rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var accumulated = root;
        CrumbBar.Children.Add(CrumbButton(root.TrimEnd('\\'), root, parts.Length == 0));

        foreach (var part in parts)
        {
            accumulated = Path.Combine(accumulated, part);
            CrumbBar.Children.Add(Chevron());
            CrumbBar.Children.Add(CrumbButton(part, accumulated, part == parts[^1]));
        }
    }

    private Button CrumbButton(string text, string path, bool isCurrent)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
            },
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Tag = path,
        };

        if (!isCurrent)
        {
            button.Click += (sender, args) =>
            {
                if ((sender as Button)?.Tag is string target) _ = ScanAsync(target);
            };
        }

        return button;
    }

    private static TextBlock Chevron() => new()
    {
        Text = "›",
        FontSize = 12.5,
        Opacity = 0.4,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void MapCanvas_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // 窗口尺寸变了要重画：树图是按像素铺的，不重排就会留白或溢出
        if (_result is not null && !BusyRing.IsActive) DrawTreemap(_result);
    }
}
