using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services.Updating;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 3 步：检查更新（自上游 dv1.2.0-insider1.8 移植）。进页面自动查一次，结果摆给用户；
/// 「立即更新」走主界面同一套 <see cref="UpdateFlow.RunAsync"/>，什么都不点直接「下一步」＝ 跳过。
/// </summary>
public partial class OobeUpdatePage : UserControl
{
    private readonly UpdateService _service = UpdateService.CreateDefault();

    private CancellationTokenSource? _cts;
    private UpdateRelease? _release;
    private bool _checked;

    public OobeUpdatePage()
    {
        InitializeComponent();

        // 页面是缓存的：来回翻只查第一次，不重复打网络
        Loaded += (_, _) =>
        {
            if (_checked) return;
            _checked = true;
            _ = CheckAsync();
        };
        Unloaded += (_, _) => CancelPending();
    }

    private void CancelPending()
    {
        try { _cts?.Cancel(); } catch { /* 已经取消 / 释放过 */ }
    }

    private async void Retry_Click(object? sender, RoutedEventArgs e)
    {
        try { await CheckAsync(); }
        catch (Exception ex) { AppLog.Catch("oobe", "重试检查更新", ex); }
    }

    private async Task CheckAsync()
    {
        CancelPending();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _release = null;
        CheckingPanel.IsVisible = true;
        ResultPanel.IsVisible = false;
        RetryButton.IsVisible = false;

        try
        {
            var channel = UpdateChannels.Parse(App.Settings.Current.UpdateChannel);
            var result = await _service.CheckAsync(channel, ShellConfig.ShellVersion, ct);
            if (ct.IsCancellationRequested) return;
            Show(result);
        }
        catch (OperationCanceledException)
        {
            // 用户翻走了 / 重新检查打断了上一次 —— 什么都不用做
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            AppLog.Catch("oobe", "初次配置阶段检查更新", ex, LogLevel.Debug);
            ShowFailure();
        }
    }

    private void Show(UpdateCheckResult result)
    {
        CheckingPanel.IsVisible = false;
        ResultPanel.IsVisible = true;
        RetryButton.IsVisible = true;
        StatusText.IsVisible = false;

        if (result is { HasUpdate: true, Release: { } release })
        {
            _release = release;

            ResultIcon.Glyph = "\uE896";                 // 下载
            ResultTitle.Text = $"发现新版本 {release.Tag}";

            var detail = $"当前版本 {ShellConfig.ShellVersion}";
            if (release.Primary is { } package && package.SizeText.Length > 0)
                detail += $"　·　安装包 {package.SizeText}";
            if (release.PublishedAt is { } when) detail += $"　·　{when.LocalDateTime:yyyy-MM-dd}";
            ResultDetail.Text = detail;

            ActionRow.IsVisible = true;
            UpdateNowButton.IsEnabled = true;
            return;
        }

        ResultIcon.Glyph = "\uE73E";                     // 对勾
        ResultTitle.Text = "已是最新版本";
        ResultDetail.Text = result.Message.Length > 0
            ? $"当前 {ShellConfig.ShellVersion}。{result.Message}"
            : $"当前 {ShellConfig.ShellVersion}，没有需要更新的内容。";
        ActionRow.IsVisible = false;
    }

    private void ShowFailure()
    {
        CheckingPanel.IsVisible = false;
        ResultPanel.IsVisible = true;
        ResultIcon.Glyph = "\uE7BA";                     // 警告
        ResultTitle.Text = "无法检查更新";
        ResultDetail.Text = "无法连接更新服务。";
        ActionRow.IsVisible = false;
        StatusText.IsVisible = false;
        RetryButton.IsVisible = true;
    }

    private async void UpdateNow_Click(object? sender, RoutedEventArgs e)
    {
        if (_release is null) return;

        UpdateNowButton.IsEnabled = false;
        StatusText.Text = "正在下载并安装。安装期间应用将自动关闭，完成后自动重新打开。";
        StatusText.IsVisible = true;

        var ok = false;
        try
        {
            ok = await UpdateFlow.RunAsync(_service, _release, "正在更新", TopLevel.GetTopLevel(this));
        }
        catch (Exception ex)
        {
            AppLog.Exception("oobe", ex, "初次配置里启动更新失败");
        }

        if (!ok)
        {
            UpdateNowButton.IsEnabled = true;
            StatusText.Text = "更新未完成，可在「设置」中重试。";
        }
    }

    private void Skip_Click(object? sender, RoutedEventArgs e) => OobeSession.Current?.Next();
}
