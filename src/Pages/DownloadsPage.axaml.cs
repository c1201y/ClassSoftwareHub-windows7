using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 「任务进行」：正在下载 / 已下载完的任务一览。
///
/// 这一页解决的是「下载时只能盯着进度条」—— 下载本身跑在
/// <see cref="DownloadManager"/> 里，就算把首页的下载弹窗收掉，任务也一直在这儿，
/// 随时能看进度、取消、或打开下好的文件（左侧导航上的数字徽标也是它）。
/// </summary>
public sealed partial class DownloadsPage : PageBase
{
    private bool _hooked;

    public DownloadsPage()
    {
        InitializeComponent();
        TaskList.ItemsSource = DownloadManager.Current.Tasks;

        // 页面用一次就丢（Frame 的 CacheSize=0），所以离开时必须把订阅摘掉，
        // 否则 DownloadManager 这个进程级单例会把已经没人要的页面一直攥着。
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        DownloadManager.Current.Changed += Refresh;
        Refresh();
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        DownloadManager.Current.Changed -= Refresh;
    }

    /// <summary>刷顶部汇总行 + 空状态 + 底部按钮的可用性（每条任务自己的卡片靠绑定自己更新）。</summary>
    private void Refresh()
    {
        var all = DownloadManager.Current.Tasks;
        var active = DownloadManager.Current.ActiveCount;
        var finished = all.Count - active;

        SummaryText.Text = all.Count == 0
            ? "下载任务不占用界面，可随时切换至其他页面；下载完成后显示系统通知。"
            : active > 0
                ? $"正在下载 {active} 个" + (finished > 0 ? $"　·　已完成 {finished} 个" : "")
                : $"当前无进行中的下载任务　·　已完成 {finished} 个";

        // ⚠️ Avalonia 没有 Visibility 枚举 → bool IsVisible。
        EmptyPanel.IsVisible = all.Count == 0;
        ClearButton.IsEnabled = finished > 0;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } task) DownloadManager.Current.Cancel(task.Id);
    }

    private void Retry_Click(object? sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } task) DownloadManager.Current.Retry(task.Id);
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } task) DownloadManager.Current.Remove(task.Id);
    }

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { Path: { Length: > 0 } path }) App.MainWindow?.OpenFile(path);
    }

    private void Reveal_Click(object? sender, RoutedEventArgs e)
    {
        // 解压过就去产物目录，否则回文件本身（见 DownloadTask.PreferredRevealPath）
        if (Tagged(sender) is { PreferredRevealPath: { Length: > 0 } path }) App.MainWindow?.RevealFile(path);
    }

    // ── 「安装 / 解压」：下完之后再走一步（2026-10-18 自上游 1.2.0 移植） ──────────
    //
    // ⛔ 更新包和普通下载走**两条路**：
    //    · 更新包（IsUpdate）→ UpdateFlow.InstallPendingNowAsync（先退应用、后起安装程序），
    //      直接双击会被 Inno 的 AppMutex 拦下；
    //    · 普通下载 → PackageRunner（.zip 走 BCL、其余交给系统组件），装完/解完按设置删源包。

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { } task) return;

        try
        {
            // 更新包的安装由更新流程托管（含"退出应用 → 起安装程序"）。
            var started = await Services.Updating.UpdateFlow.InstallPendingFromTaskPageAsync(task);
            if (!started)
                await InfoAsync("未能启动安装", "安装包可能已被移动或删除。可回「设置 → 关于」重新检查更新。");
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("downloads", ex, "启动更新包安装");
            await InfoAsync("未能启动安装", ex.Message);
        }
    }

    private async void PackageAction_Click(object? sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { Path: { Length: > 0 } path } task) return;

        var action = task.Action;
        if (action == PackageAction.None) return;

        if (action == PackageAction.Install)
        {
            RunInstaller(task, path);
            return;
        }

        await ExtractAsync(task, path);
    }

    /// <summary>安装包：直接交给 shell 启动（与双击等价）。</summary>
    private void RunInstaller(DownloadTask task, string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            task.SetWork(false, "已启动安装程序。");
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("downloads", ex, "启动安装包");
            _ = InfoAsync("没能启动安装程序", ex.Message);
        }
    }

    /// <summary>压缩包：后台解压（可看进度文本），完事按设置自动删源包 / 打开产物目录。</summary>
    private async Task ExtractAsync(DownloadTask task, string path)
    {
        task.SetWork(true, "正在解压…");
        try
        {
            var progress = new Progress<double>(p => task.SetWork(true, $"正在解压… {p * 100:0}%"));
            var result = await PackageRunner.ExtractAsync(path, progress);

            task.ExtractedDir = result.Directory;
            task.SetWork(false, "已解压到：" + result.Directory);

            var settings = App.Settings.Current;
            if (settings.AutoDeletePackage)
            {
                try { File.Delete(path); } catch { /* 被占用就算了 */ }
            }

            if (settings.AutoOpenExtractFolder)
                App.MainWindow?.RevealFile(result.Directory);
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("downloads", ex, "解压压缩包");
            task.SetWork(false, "解压失败：" + ex.Message);
        }
    }

    private void ExtractFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { ExtractedDir: { Length: > 0 } dir }) App.MainWindow?.RevealFile(dir);
    }

    private async Task InfoAsync(string title, string body)
    {
        var dialog = new FluentAvalonia.UI.Controls.ContentDialog
        {
            Title = title,
            Content = new Avalonia.Controls.TextBlock
            {
                Text = body, FontSize = 13, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e) => DownloadService.OpenDownloadsFolder();

    private void ClearFinished_Click(object? sender, RoutedEventArgs e) => DownloadManager.Current.ClearFinished();

    // ⚠️ 原版 sender 收窄成 FrameworkElement；Avalonia 对应 Control。
    private static DownloadTask? Tagged(object? sender) =>
        (sender as Control)?.Tag as DownloadTask;
}
