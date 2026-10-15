using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 教室喊话页（收发一体）：
///   · 左栏：接收开关 + 轮询状态 + 本机配对码 + 展示设置（间隔 / 安静 / 朗读）；
///   · 右栏：发送区 + 已配对设备（互填配对码即配对，勾选即定向，可多选批量）。
/// 配对语义对齐 UU 远程 —— 一台电脑一个 6 位配对码（<see cref="Services.AnnounceService.MyPairCode"/>），
/// 名单存 <see cref="AnnounceDevice"/>（Id = 对方配对码）。轮询循环本体在 <see cref="Services.AnnounceService"/>。
/// </summary>
public sealed partial class AnnouncePage : PageBase
{
    /// <summary>状态行刷新计时器（1s 一刷；页面离开即停，不留后台空转）。</summary>
    private readonly DispatcherTimer _statusTimer;

    /// <summary>构造期正在填初值：XAML 挂的事件此刻回调会把半成品写进设置（本仓库约定）。</summary>
    private bool _loading = true;

    private bool _sending;
    private bool _busyDevices;

    /// <summary>最近一次「刷新」拿到的各设备在线时间（code → 最近轮询毫秒时刻；仅会话内暂存，不落盘）。</summary>
    private readonly System.Collections.Generic.Dictionary<string, long> _lastSeen = new();

    /// <summary>在线判定阈值：略大于一个轮询间隔（当前最大档 60s）即视为离线。</summary>
    private const long OnlineWindowMs = 90_000;

    public AnnouncePage()
    {
        InitializeComponent();

        var s = App.Settings.Current;

        EnableToggle.IsChecked = s.AnnounceEnabled;
        QuietToggle.IsChecked = s.AnnounceQuietMode;
        SpeakToggle.IsChecked = s.AnnounceSpeakText;
        IntervalCombo.SelectedIndex = s.AnnounceIntervalSec switch { 15 => 0, 60 => 2, _ => 1 };
        PairCodeText.Text = Services.AnnounceService.MyPairCode;
        TtsHint.IsOpen = !Services.Tts.ChineseVoiceAvailable;

        // 迁移清理：早期方案名单里存的是 16~64 位设备号 hex，与配对码（6 位数字）语义不兼容，直接清掉
        var stale = s.AnnounceDevices.Where(d => !Services.AnnounceService.IsValidPairCode(d.Id)).ToList();
        foreach (var d in stale) s.AnnounceDevices.Remove(d);
        if (stale.Count > 0) App.Settings.Save();

        _loading = false;

        RenderDeviceList();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        RefreshStatus();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        _statusTimer.Start();
        RefreshStatus();
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _statusTimer.Stop();
    }

    // ════════════════════════ 接收侧 ════════════════════════

    private void EnableToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AnnounceEnabled = EnableToggle.IsChecked == true;
        App.Settings.Save();

        if (App.Settings.Current.AnnounceEnabled) Services.AnnounceService.Start();
        else Services.AnnounceService.Stop();

        RefreshStatus();
    }

    private void Interval_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (IntervalCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var sec)) return;

        App.Settings.Current.AnnounceIntervalSec = sec;
        App.Settings.Save();
        Services.AnnounceService.RefreshSettings();   // 未运行则拉起；运行中下一轮自然生效
    }

    private void Quiet_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AnnounceQuietMode = QuietToggle.IsChecked == true;
        App.Settings.Save();
    }

    private void Speak_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Current.AnnounceSpeakText = SpeakToggle.IsChecked == true;
        App.Settings.Save();
    }

    /// <summary>轮询状态行：开关没开 / 正常节奏（附上次检查时刻）/ 退避中。</summary>
    private void RefreshStatus()
    {
        if (EnableToggle.IsChecked != true)
        {
            StatusText.Text = "已停止，打开开关后开始接收。";
            return;
        }

        if (!Services.AnnounceService.IsRunning)
        {
            StatusText.Text = "正在启动……";
            return;
        }

        var last = Services.AnnounceService.LastPollAt;
        var lastPart = last is null
            ? "尚未连上服务器"
            : $"上次检查 {TimeAgo(last.Value)}";
        var back = Services.AnnounceService.BackoffSec;
        StatusText.Text = back > 0
            ? $"{lastPart}；连接不稳定，{back} 秒后重试。"
            : $"{lastPart}。";
    }

    private static string TimeAgo(DateTime t)
    {
        var sec = (int)(DateTime.Now - t).TotalSeconds;
        if (sec < 5) return "刚刚";
        if (sec < 60) return $"{sec} 秒前";
        var min = sec / 60;
        return min < 60 ? $"{min} 分钟前" : $"{min / 60} 小时前";
    }

    // ════════════════════════ 发送侧 ════════════════════════

    private void Content_Changed(object? sender, TextChangedEventArgs e)
    {
        var len = ContentBox.Text?.Length ?? 0;
        CountText.Text = $"{len} / 100";
        SendButton.IsEnabled = len > 0 && len <= 100 && !_sending;
    }

    // ════════════════════════ 已配对设备 ════════════════════════
    // 名单存 AppSettings.AnnounceDevices（Id = 对方配对码，勾选状态一起持久化）。
    // 添加 = 输对方 6 位码；刷新 = 逐码查询服务端，更新机器名与在线状态（仅会话内显示）。
    // 发送时 targets = 勾选设备的配对码，逐码顺序发送（服务端限流按码计，互不影响）。

    private async void AddDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (_busyDevices) return;
        var code = (NewCodeBox.Text ?? "").Trim();
        if (!Services.AnnounceService.IsValidPairCode(code))
        {
            ShowResult(false, "配对码格式不对：应为 6 位数字。");
            return;
        }

        var devices = App.Settings.Current.AnnounceDevices;
        if (devices.Any(d => d.Id == code))
        {
            ShowResult(false, "该设备已在名单中。");
            return;
        }

        _busyDevices = true;
        AddDevice.IsEnabled = false;

        var info = await Services.AnnounceService.QueryDeviceAsync(code);

        _busyDevices = false;
        AddDevice.IsEnabled = true;

        if (info is null)
        {
            ShowResult(false, "查询失败：网络不可达，请稍后重试。");
            return;
        }

        devices.Add(new AnnounceDevice { Id = code, Name = info.Name, Selected = true });
        App.Settings.Save();
        if (info.LastSeenMs > 0) _lastSeen[code] = info.LastSeenMs;

        NewCodeBox.Text = "";
        RenderDeviceList();
        ShowResult(true, info.LastSeenMs > 0
            ? (string.IsNullOrWhiteSpace(info.Name) ? "已添加。" : $"已添加：{info.Name}。")
            : "已添加。该设备尚未开启「接收喊话」，开启后刷新即可显示机器名。");
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var d in App.Settings.Current.AnnounceDevices) d.Selected = false;
        App.Settings.Save();
        RenderDeviceList();
    }

    private async void RefreshDevices_Click(object? sender, RoutedEventArgs e)
    {
        if (_busyDevices) return;
        var devices = App.Settings.Current.AnnounceDevices;
        if (devices.Count == 0) return;

        _busyDevices = true;
        RefreshDevices.IsEnabled = false;

        foreach (var d in devices)
        {
            var info = await Services.AnnounceService.QueryDeviceAsync(d.Id);
            if (info is null) continue;                     // 该码查询失败，保留原状
            if (info.LastSeenMs > 0) _lastSeen[d.Id] = info.LastSeenMs;
            if (!string.IsNullOrWhiteSpace(info.Name) && info.Name != d.Name) d.Name = info.Name;
        }
        App.Settings.Save();

        _busyDevices = false;
        RefreshDevices.IsEnabled = true;
        RenderDeviceList();
    }

    /// <summary>重铺设备名单（勾选框 + 机器名/配对码 + 在线状态 + 删除键）。</summary>
    private void RenderDeviceList()
    {
        var devices = App.Settings.Current.AnnounceDevices;
        DeviceList.Items.Clear();
        DeviceEmptyHint.IsVisible = devices.Count == 0;

        foreach (var d in devices)
        {
            var dev = d;   // 闭包捕获：每行锁自己的实体
            var row = new Grid { Margin = new Avalonia.Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var check = new CheckBox
            {
                IsChecked = dev.Selected,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MinWidth = 0,
            };
            check.IsCheckedChanged += (_, _) =>
            {
                dev.Selected = check.IsChecked == true;
                App.Settings.Save();
            };
            Grid.SetColumn(check, 0);

            var label = new TextBlock
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(8, 0, 8, 0),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = string.IsNullOrWhiteSpace(dev.Name)
                    ? dev.Id
                    : $"{dev.Name}　（{dev.Id}）",
            };
            Grid.SetColumn(label, 1);

            var status = new TextBlock
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 8, 0),
                FontSize = 11.5,
                Opacity = 0.7,
                Text = OnlineText(dev.Id),
            };
            Grid.SetColumn(status, 2);

            var del = new Button { Content = "删除", MinWidth = 52, Padding = new Avalonia.Thickness(8, 2, 8, 2) };
            del.Click += (_, _) =>
            {
                App.Settings.Current.AnnounceDevices.Remove(dev);
                _lastSeen.Remove(dev.Id);
                App.Settings.Save();
                RenderDeviceList();
            };
            Grid.SetColumn(del, 2);

            row.Children.Add(check);
            row.Children.Add(label);
            row.Children.Add(status);
            row.Children.Add(del);
            DeviceList.Items.Add(row);
        }
    }

    /// <summary>在线状态文案：从未查询到 → 空；查询过按最近轮询时刻判在线 / 离线。</summary>
    private string OnlineText(string code)
    {
        if (!_lastSeen.TryGetValue(code, out var ts) || ts <= 0) return "";
        var online = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts < OnlineWindowMs;
        return online ? "在线" : "离线";
    }

    private async void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (_sending) return;
        var text = ContentBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            ShowResult(false, "请先输入喊话内容。");
            return;
        }

        var from = FromBox.Text ?? "";
        var duration = 30;
        if (DurationCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && int.TryParse(tag, out var d))
            duration = d;

        // 发送目标 = 勾选设备的配对码；一台没勾不发（一码一机，无广播语义）
        var targets = App.Settings.Current.AnnounceDevices
            .Where(x => x.Selected)
            .Select(x => x.Id)
            .ToList();
        if (targets.Count == 0)
        {
            ShowResult(false, "请先在「已配对设备」中勾选要发送的设备。");
            return;
        }

        _sending = true;
        SendButton.IsEnabled = false;
        SendResult.IsOpen = false;

        var result = await Services.AnnounceService.SendAsync(text, from, duration, targets);

        _sending = false;
        SendButton.IsEnabled = (ContentBox.Text?.Length ?? 0) > 0;

        if (result.Ok)
        {
            var part = result.Failed > 0 ? $"，{result.Failed} 台失败" : "";
            ShowResult(true, result.Devices > 0
                ? $"已发送，{result.Devices} 台设备将在一个轮询间隔内收到{part}。"
                : $"已发送，但目标设备当前离线{part} —— 设备开启「接收喊话」后才能收到。");
        }
        else
            ShowResult(false, result.Error switch
            {
                "no_target" => "请先勾选要发送的设备。",
                "rate_limited" => "发送过快：同一配对码 10 秒内限发一条。",
                "no_device" => "目标设备离线：须开启「接收喊话」并保持联网。",
                "unreachable" => "网络不可达：请检查本机网络。",
                _ => $"发送失败（{result.Error}），请稍后重试。",
            });
    }

    private void ShowResult(bool ok, string message)
    {
        SendResult.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        SendResult.Title = ok ? "发送成功" : "发送失败";
        SendResult.Message = message;
        SendResult.IsOpen = true;
    }
}
