using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Views;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 教室喊话页（对齐上游 dv1.2.0-insider1.5：双分页 + 局域网通道）。
/// <para>
/// 接收页：接收开关 / 本机标识 / 本机配对码（含预览）/ 局域网通道 / 放行防火墙 /
/// 「谁保存了本机配对码」（可删除、可清空）/ 显示设置（间隔 / 安静 / 朗读 / 出场 / 提示音）。
/// 发送页：局域网设备列表 + 手动填码（可保存）+ 已保存的电脑（勾选即发）+ 内容 / 署名 / 时长 / 发送。
/// </para>
/// <para>
/// 发送链路双通道：发现得到的设备局域网直达（<see cref="AnnounceLanService.TrySendAsync"/>），
/// 不在同一网段的自动回落服务器（<see cref="AnnounceService.SendAsync"/>）。
/// </para>
/// <para>Win7 版平台差异：SelectorBar → RadioButton 分页、文件选择走 FilePickerCompat、对话框为 FA ContentDialog。</para>
/// </summary>
public sealed partial class AnnouncePage : PageBase
{
    /// <summary>状态行刷新计时器（1 秒一刷；离开页面即停）。</summary>
    private readonly DispatcherTimer _statusTimer;

    /// <summary>构造期正在填初值：此刻控件事件回调会把半成品写进设置。</summary>
    private bool _loading = true;

    private bool _sending;

    public AnnouncePage()
    {
        InitializeComponent();

        var s = App.Settings.Current;

        EnableToggle.IsChecked = s.AnnounceEnabled;
        QuietToggle.IsChecked = s.AnnounceQuietMode;
        SpeakToggle.IsChecked = s.AnnounceSpeakText;
        IntervalCombo.SelectedIndex = s.AnnounceIntervalSec switch { 15 => 0, 60 => 2, _ => 1 };
        DurationCombo.SelectedIndex = 1;   // 30 秒，与服务端默认一致
        DefaultViewCombo.SelectedIndex = IsSendDefault(s.AnnounceDefaultView) ? 1 : 0;
        PairCodeText.Text = AnnounceService.MyPairCode;
        LabelBox.Text = s.AnnounceLabel;
        LanToggle.IsChecked = s.AnnounceLanEnabled;
        AnimationCombo.SelectedIndex = (s.AnnounceAnimation ?? "typewriter").ToLowerInvariant() switch
        {
            "slide" => 1,
            "pop" => 2,
            "none" => 3,
            _ => 0,
        };
        TtsHint.IsOpen = !Tts.ChineseVoiceAvailable;

        _loading = false;

        SelectTab(IsSendDefault(s.AnnounceDefaultView) ? "send" : "receive");

        RenderSaved();
        RenderBinders();
        SyncSoundName();
        _ = RefreshServerBindersAsync();   // 服务端登记（未实现时静默跳过）
        UpdateStatus();
        _ = RefreshFirewallAsync();        // 查询要起一个子进程，不能卡构造
        RefreshPeerList();

        // 两个后台列表（发现 / 登记）都在非 UI 线程上变，必须回 UI 线程再刷
        AnnounceLanService.PeersChanged += OnPeersChanged;
        AnnounceLanService.BindersChanged += OnBindersChanged;
        AnnounceLanService.SavedPeersChanged += OnSavedPeersChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        _statusTimer.Start();
        UpdateStatus();
        RefreshPeerList();
        _ = RefreshFirewallAsync(force: true);   // 从这里进来时重新查一次（用户可能刚在外面改过防火墙）
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _statusTimer.Stop();
        // 页面离开后旧实例留着订阅会回调已离开的控件
        AnnounceLanService.PeersChanged -= OnPeersChanged;
        AnnounceLanService.BindersChanged -= OnBindersChanged;
        AnnounceLanService.SavedPeersChanged -= OnSavedPeersChanged;
    }

    private void OnPeersChanged() => Dispatcher.UIThread.Post(RefreshPeerList);

    private void OnBindersChanged() => Dispatcher.UIThread.Post(RenderBinders);

    /// <summary>对方在它的接收端把本机删了 → 本机这边也要把「已保存的电脑」那条去掉。</summary>
    private void OnSavedPeersChanged() => Dispatcher.UIThread.Post(() =>
    {
        RenderSaved();
        ShowResult(true, "对方已删除本机的登记，对应的「已保存的电脑」已移除。");
    });

    // ════════════════════════ 分页 ════════════════════════

    private static bool IsSendDefault(string? value) =>
        string.Equals(value, "send", StringComparison.OrdinalIgnoreCase);

    private void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyTab();
    }

    private void ApplyTab()
    {
        var send = TabSend.IsChecked == true;
        ReceiveView.IsVisible = !send;
        SendView.IsVisible = send;
    }

    private void SelectTab(string tag)
    {
        (tag == "send" ? TabSend : TabReceive).IsChecked = true;
        ApplyTab();   // 选中同一项时 Checked 不再触发，这里补一次
    }

    private void DefaultView_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DefaultViewCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        App.Settings.Current.AnnounceDefaultView = tag;
        App.Settings.Save();
        SelectTab(tag);   // 选了就立刻切过去，不然像没生效
    }

    // ════════════════════════ 接收 ════════════════════════

    private void EnableToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.AnnounceEnabled = EnableToggle.IsChecked == true;
        App.Settings.Save();

        if (EnableToggle.IsChecked == true) AnnounceService.Start();
        else AnnounceService.Stop();

        UpdateStatus();
    }

    private void Label_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        var value = (LabelBox.Text ?? "").Trim();
        if (value == App.Settings.Current.AnnounceLabel) return;

        App.Settings.Current.AnnounceLabel = value;
        App.Settings.Save();
        // 广播下一拍（≤5 秒）就会带上新标识，发送端列表随之更新
    }

    private async void Lan_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        App.Settings.Current.AnnounceLanEnabled = LanToggle.IsChecked == true;
        App.Settings.Save();

        if (LanToggle.IsChecked == true)
        {
            AnnounceLanService.Start();

            if (!AnnounceLanService.IsRunning && AnnounceLanService.LastError is { Length: > 0 } error)
                ShowResult(false, $"局域网通道启动失败：{error}");

            await EnsureFirewallAsync(prompt: true);   // 内部已刷新卡片，不必再查一次
        }
        else
        {
            AnnounceLanService.Stop();
            await RefreshFirewallAsync();
        }

        RefreshPeerList();
    }

    private void Interval_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (IntervalCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var sec)) return;

        App.Settings.Current.AnnounceIntervalSec = sec;
        App.Settings.Save();
        AnnounceService.RefreshSettings();
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

    private async void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(PairCodeText.Text ?? "");
        }
        catch (Exception ex)
        {
            // 剪贴板被别的程序占住是常态，失败不打扰用户（码就在屏幕上）
            AppLog.Warn("announce", "复制配对码失败：" + ex.Message);
        }
    }

    /// <summary>状态行只说人话：能不能收到。不露退避秒数等实现细节。</summary>
    private void UpdateStatus()
    {
        if (EnableToggle.IsChecked != true)
        {
            StatusText.Text = App.Settings.Current.AnnounceLanEnabled
                ? "服务器通道未开启；同网段的电脑仍可通过局域网直接发来。"
                : "未开启，打开后开始接收消息。";
            return;
        }

        if (!AnnounceService.IsRunning)
        {
            StatusText.Text = "正在启动……";
            return;
        }

        var back = AnnounceService.BackoffSec;
        if (back > 0)
        {
            StatusText.Text = $"连不上服务器，{back} 秒后自动重试。"
                + (App.Settings.Current.AnnounceLanEnabled ? "局域网直达不受影响。" : "");
            return;
        }

        StatusText.Text = AnnounceService.LastPollAt is null ? "已开启，正在连接服务器……" : "已连接，正在等待消息。";
    }

    // ════════════════════════ 局域网通道 ════════════════════════

    /// <summary>最近一次查到的防火墙状态（卡片文案按它显示）。</summary>
    private FirewallState _firewall = FirewallState.Unknown;

    /// <summary>
    /// 查一次防火墙并刷新卡片。
    /// <para>
    /// ⚠️ 查询会起一个子进程（约 0.5~2 秒），一律异步 + 结果缓存 60 秒
    /// （见 <see cref="FirewallGateway.CheckAsync"/>）—— 不能在构造/切换事件里同步跑。
    /// </para>
    /// </summary>
    private async Task RefreshFirewallAsync(bool force = false)
    {
        if (LanToggle.IsChecked != true)
        {
            _firewall = FirewallState.Unknown;
            FirewallCard.IsVisible = false;
            return;
        }

        _firewall = await FirewallGateway.CheckAsync(force);

        var need = _firewall is FirewallState.PortRulesMissing or FirewallState.ProgramBlocked;
        FirewallDesc.Text = FirewallGateway.Describe(_firewall);
        FirewallCard.IsVisible = need;
    }

    /// <summary>
    /// 打开局域网通道时顺手检查一次；确认被拦才提示修复（提示之后才提权，不偷偷弹 UAC）。
    /// 规则完好时这里什么都不做 —— 装好一次之后，以后开机不会再有 UAC。
    /// </summary>
    private async Task EnsureFirewallAsync(bool prompt)
    {
        await RefreshFirewallAsync(force: true);
        if (!prompt) return;
        if (_firewall is not (FirewallState.PortRulesMissing or FirewallState.ProgramBlocked)) return;

        var dialog = new ContentDialog
        {
            Title = "修复防火墙",
            Content = FirewallGateway.Describe(_firewall) + "\n"
                      + "修复内容：为 TCP 47821（收件）与 UDP 47820（发现）添加覆盖「任何网络」的入站规则，"
                      + "并移除本程序的入站拦截规则。\n"
                      + "下一步会弹出一次管理员确认，完成后不再询问。",
            PrimaryButtonText = "修复",
            CloseButtonText = "暂不修复",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ApplyFirewallRepairAsync();
    }

    /// <summary>执行修复（一次 UAC）并按结果刷新界面。</summary>
    private async Task ApplyFirewallRepairAsync()
    {
        FirewallButton.IsEnabled = false;
        try
        {
            var state = await Task.Run(FirewallGateway.Repair);
            _firewall = state;

            switch (state)
            {
                case FirewallState.Ok:
                    ShowResult(true, "防火墙已修复：同网段的电脑现在可以发进来。");
                    break;
                case FirewallState.Unknown:
                    ShowResult(false, "没有拿到防火墙状态。若刚才取消了管理员确认，重新点「修复」即可。");
                    break;
                default:
                    ShowResult(false, "仍然被拦截：" + FirewallGateway.Describe(state));
                    break;
            }
        }
        finally
        {
            FirewallButton.IsEnabled = true;
            FirewallDesc.Text = FirewallGateway.Describe(_firewall);
            FirewallCard.IsVisible = LanToggle.IsChecked == true
                                     && _firewall is FirewallState.PortRulesMissing or FirewallState.ProgramBlocked;
            RefreshPeerList();
        }
    }

    private async void Firewall_Click(object? sender, RoutedEventArgs e) => await ApplyFirewallRepairAsync();

    private void RefreshPeers_Click(object? sender, RoutedEventArgs e) => RefreshPeerList();

    /// <summary>
    /// 重铺局域网设备列表。勾选状态按配对码保留 —— 列表会随心跳重刷，不能把用户勾好的刷掉。
    /// </summary>
    private void RefreshPeerList()
    {
        if (!App.Settings.Current.AnnounceLanEnabled)
        {
            // 发送端也要开着局域网通道才能发现对方（发现依赖的是本机的广播与监听，不是「只发送」）。
            // 开关在「接收」页，不在这一页 —— 所以必须指明去哪打开，否则用户会以为这一栏坏了。
            PeerHint.Text = "局域网通道未开启。请到「接收」页打开「局域网通道」，同一网段的电脑才会出现在这里。";
            PeerList.IsVisible = false;
            PeerList.ItemsSource = null;
            return;
        }

        var selected = (PeerList.ItemsSource as List<LanPeerItem>)
            ?.Where(p => p.IsSelected)
            .Select(p => p.PairCode)
            .ToHashSet() ?? new HashSet<string>();

        var items = AnnounceLanService.Peers
            .Select(p => new LanPeerItem(p) { IsSelected = selected.Contains(p.PairCode) })
            .ToList();

        PeerList.ItemsSource = items;

        if (items.Count > 0)
        {
            PeerHint.Text = $"已发现 {items.Count} 台。勾选要发送的电脑，也可以直接填配对码。";
            PeerList.IsVisible = true;
            return;
        }

        PeerList.IsVisible = false;
        PeerHint.Text = AnnounceLanService.IsRunning
            ? "当前网段内没有找到开启局域网通道的电脑；可以直接填配对码走服务器。"
            : "局域网通道未能启动"
              + (string.IsNullOrEmpty(AnnounceLanService.LastError) ? "" : $"（{AnnounceLanService.LastError}）")
              + "；可以改用配对码走服务器。";
    }

    // ════════════════════════ 谁保存了本机配对码 ════════════════════════

    private void RenderBinders()
    {
        var list = App.Settings.Current.AnnounceBinders
            .OrderByDescending(b => b.SeenTicks)
            .Select(b => new BinderItem(b))
            .ToList();

        BinderList.ItemsSource = list;
        BinderList.IsVisible = list.Count > 0;
        BinderEmpty.IsVisible = list.Count == 0;
        ClearBindersButton.IsEnabled = list.Count > 0;
    }

    /// <summary>
    /// 拉一次服务端登记名单，与本地局域网记录合并展示。
    /// 服务端这三个端点还没实现时返回 null，什么都不动 —— 保留本地已有的局域网记录，界面也不提示错误。
    /// </summary>
    private async Task RefreshServerBindersAsync()
    {
        var server = await AnnounceService.ListBindersAsync();
        if (server is null) return;   // 服务端不支持 / 不可达：保持现状

        var list = App.Settings.Current.AnnounceBinders;
        list.RemoveAll(b => string.Equals(b.Via, "server", StringComparison.Ordinal));

        foreach (var b in server)
        {
            // 同一台如果局域网也登记过，以服务端记录为准（它能跨网、也能远程删）
            list.RemoveAll(x => string.Equals(x.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            list.Add(new AnnounceBinder
            {
                Code = AnnounceService.MyPairCode,
                Name = b.Name,
                Label = b.Label,
                SeenTicks = b.Ts,
                Via = "server",
                SenderId = b.Id,
            });
        }

        while (list.Count > 50) list.RemoveAt(0);
        App.Settings.Save();
        RenderBinders();
    }

    private async void DeleteBinder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;

        var list = App.Settings.Current.AnnounceBinders;
        var target = list.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        list.Remove(target);
        App.Settings.Save();
        RenderBinders();

        if (string.Equals(target.Via, "server", StringComparison.Ordinal))
        {
            var ok = await AnnounceService.UnbindAsync(target.SenderId);
            ShowResult(ok,
                ok ? "已从服务端移除该登记。" : "已从本机移除；服务端暂时不可用，对方仍会保留这个码。");
            return;
        }

        _ = NotifyUnbindAsync(new[] { target.Ip });
    }

    private async void ClearBinders_Click(object? sender, RoutedEventArgs e)
    {
        var list = App.Settings.Current.AnnounceBinders;
        if (list.Count == 0) return;

        var serverCount = list.Count(b => string.Equals(b.Via, "server", StringComparison.Ordinal));
        var ips = list.Where(b => !string.Equals(b.Via, "server", StringComparison.Ordinal))
            .Select(b => b.Ip)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct()
            .ToList();

        list.Clear();
        App.Settings.Save();
        RenderBinders();

        if (serverCount > 0)
        {
            var ok = await AnnounceService.UnbindAsync(null);   // 服务端支持不带 senderId = 清空
            if (!ok) ShowResult(false, "服务端暂时不可用：跨网络的登记没有清掉。");
        }

        if (ips.Count > 0) _ = NotifyUnbindAsync(ips);
    }

    /// <summary>
    /// 删除登记的同时通知对方把「已保存的电脑」里那条也删掉（单边删除两边对不上）。
    /// 通知不到就记进「待通知」，等对方下次广播心跳时补发。
    /// </summary>
    private async Task NotifyUnbindAsync(IEnumerable<string> ips)
    {
        var code = AnnounceService.MyPairCode;
        var failed = 0;
        var total = 0;

        foreach (var ip in ips.Distinct())
        {
            total++;
            if (await AnnounceLanService.TryUnbindAsync(ip, code)) continue;

            QueuePendingUnbind(ip, code);
            failed++;
        }

        if (total == 0) return;

        ShowResult(true, failed == 0
            ? "已移除，并已通知对方删除对应记录。"
            : $"已从本机移除；{failed} 台当前不在线，等它们再上线时自动通知。");
    }

    private void QueuePendingUnbind(string ip, string code)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;

        var pending = App.Settings.Current.AnnouncePendingUnbinds;
        if (pending.Any(p => p.Ip == ip && p.Code == code)) return;

        pending.Add(new PendingUnbind
        {
            Ip = ip,
            Code = code,
            CreatedTicks = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        while (pending.Count > 50) pending.RemoveAt(0);
        App.Settings.Save();
    }

    // ════════════════════════ 已保存的电脑 ════════════════════════

    private void RenderSaved()
    {
        var saved = App.Settings.Current.AnnounceSavedPeers;

        var selected = (SavedList.ItemsSource as List<SavedPeerItem>)
            ?.Where(p => p.IsSelected)
            .Select(p => p.Code)
            .ToHashSet() ?? new HashSet<string>();

        var items = saved
            .OrderByDescending(p => p.SavedTicks)
            .Select(p => new SavedPeerItem(p) { IsSelected = selected.Contains(p.Code) })
            .ToList();

        SavedList.ItemsSource = items;
        SavedList.IsVisible = items.Count > 0;
        SavedHint.Text = items.Count > 0 ? "勾选要发送的电脑" : "填好配对码后点「保存」，下次直接勾选";
    }

    private void SavePeer_Click(object? sender, RoutedEventArgs e) => SavePeer(TargetCodeBox.Text, notify: true);

    /// <summary>
    /// 存一个配对码到「已保存的电脑」。
    /// 如果这台电脑此刻就在同一局域网里（发现得到），顺手通知它登记一下 —— 它的接收页就能看到「谁保存了我的码」。
    /// </summary>
    private async void SavePeer(string? rawCode, bool notify)
    {
        var code = (rawCode ?? "").Trim();
        if (!AnnounceService.IsValidPairCode(code))
        {
            ShowResult(false, "配对码应为 6 位数字。");
            return;
        }

        var list = App.Settings.Current.AnnounceSavedPeers;
        if (list.Any(p => p.Code == code))
        {
            ShowResult(true, "这个配对码已经在「已保存的电脑」里。");
            return;
        }

        var peer = AnnounceLanService.Peers.FirstOrDefault(p => p.PairCode == code);
        list.Insert(0, new AnnounceSavedPeer
        {
            Code = code,
            Name = peer?.Display ?? "",
            SavedTicks = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        App.Settings.Save();
        RenderSaved();

        if (notify)
        {
            var label = App.Settings.Current.AnnounceLabel ?? "";

            if (peer is not null)
            {
                await AnnounceLanService.TryBindAsync(peer, Environment.MachineName, label);
                ShowResult(true, $"已保存 {peer.Display}，并已通知对方登记。");
            }
            else
            {
                // 不在同一局域网：走服务端登记（服务端未实现时自动降级，不报错）
                var ok = await AnnounceService.BindAsync(code, Environment.MachineName, label);
                ShowResult(true, ok
                    ? "已保存，并已在服务端登记 —— 对方跨网络也能看到。"
                    : "已保存。对方不在同一局域网，且服务端登记暂时不可用，对方看不到这条登记。");
            }
        }
        else
        {
            ShowResult(true, "已保存。");
        }
    }

    private void DeleteSaved_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code }) return;

        App.Settings.Current.AnnounceSavedPeers.RemoveAll(p => p.Code == code);
        App.Settings.Save();
        RenderSaved();
    }

    // ════════════════════════ 出场方式与提示音 ════════════════════════

    private void Animation_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AnimationCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        App.Settings.Current.AnnounceAnimation = tag;
        App.Settings.Save();
    }

    private void SyncSoundName() => SoundNameText.Text = AnnounceSound.DisplayName;

    private async void PickSound_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            var filters = AnnounceSound.Extensions
                .Select(ext => "*" + ext)
                .ToList();
            var fileTypes = new List<FilePickerFileType>
            {
                new("音频文件") { Patterns = filters },
            };

            // Win7 走 IFileDialog 兜底（FilePickerCompat 统一分发）
            var result = await FilePickerCompat.PickFilesAsync(top, "选择提示音", false, fileTypes);
            if (result.Files.Count == 0) return;

            AnnounceSound.CustomPath = result.Files[0];
            SyncSoundName();
            AnnounceSound.Play();   // 挑完立刻响一下试听
        }
        catch (Exception ex)
        {
            AppLog.Info("announce", "选择提示音失败：" + ex.Message);
        }
    }

    private void TestSound_Click(object? sender, RoutedEventArgs e) => AnnounceSound.Play();

    private void ResetSound_Click(object? sender, RoutedEventArgs e)
    {
        AnnounceSound.CustomPath = "";
        SyncSoundName();
        AnnounceSound.Play();
    }

    // ════════════════════════ 本机预览（不联网） ════════════════════════

    private void PreviewShort_Click(object? sender, RoutedEventArgs e)
        => Preview("请李明同学到办公室拿东西", "教务处", 15);

    private void PreviewLong_Click(object? sender, RoutedEventArgs e)
        => Preview("通知：明天上午第二节课后，请各班班长到教务处领取本学期期中考试的答题卡与草稿纸，"
                   + "并按班级人数核对清楚后签字确认；如有缺页或印刷不清请当场说明，逾期不再补发。",
                   "教务处", 20);

    private void PreviewNoFrom_Click(object? sender, RoutedEventArgs e)
        => Preview("请把教室多媒体柜的钥匙交到门卫室", "", 20);

    private void Preview(string text, string from, int durationSec)
    {
        AppLog.Info("announce", "本机预览喊话窗（不联网）");
        AnnounceWindow.Preview(new AnnounceMessage
        {
            Id = "preview-" + Guid.NewGuid().ToString("N")[..6],
            Text = text,
            From = from,
            Duration = durationSec,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, quiet: QuietToggle.IsChecked == true, speak: SpeakToggle.IsChecked == true);
    }

    // ════════════════════════ 发送 ════════════════════════

    private void Content_Changed(object? sender, TextChangedEventArgs e)
    {
        var len = ContentBox.Text?.Length ?? 0;
        CountText.Text = $"{len} / 100";
        SendButton.IsEnabled = len > 0 && len <= 100 && !_sending;
    }

    private async void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (_sending) return;

        var text = ContentBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            ShowResult(false, "请先输入要展示的内容。");
            return;
        }

        var manualCode = (TargetCodeBox.Text ?? "").Trim();
        if (manualCode.Length > 0 && !AnnounceService.IsValidPairCode(manualCode))
        {
            ShowResult(false, "配对码应为 6 位数字。");
            return;
        }

        // 目标 = 勾选的局域网设备 ∪ 勾选的已保存设备 ∪ 手填的码（按码去重）
        var targets = new List<string>();
        targets.AddRange((PeerList.ItemsSource as List<LanPeerItem>)?.Where(p => p.IsSelected)
            .Select(p => p.PairCode) ?? Enumerable.Empty<string>());
        targets.AddRange((SavedList.ItemsSource as List<SavedPeerItem>)?.Where(p => p.IsSelected)
            .Select(p => p.Code) ?? Enumerable.Empty<string>());
        if (manualCode.Length > 0) targets.Add(manualCode);
        targets = targets.Distinct().ToList();

        if (targets.Count == 0)
        {
            ShowResult(false, "请勾选要发送的电脑，或填一个配对码。");
            return;
        }

        var duration = 30;
        if (DurationCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && int.TryParse(tag, out var d))
            duration = d;

        _sending = true;
        SendButton.IsEnabled = false;
        SendResult.IsOpen = false;

        var from = FromBox.Text ?? "";
        var msg = new AnnounceMessage
        {
            Id = Guid.NewGuid().ToString("N")[..12],   // 两条通道共用，接收端据此去重
            Text = text,
            From = from,
            Duration = duration,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var lanOk = 0;
        var serverOk = 0;
        var failed = 0;
        var revoked = new List<string>();   // 对方已在它的接收端把我们删了

        foreach (var code in targets)
        {
            // 发现得到的优先直连；发现不到的（已保存但不在同一网段）走服务器
            var peer = AnnounceLanService.Peers.FirstOrDefault(p => p.PairCode == code);
            if (peer is not null && await AnnounceLanService.TrySendAsync(peer, msg))
            {
                lanOk++;
                continue;
            }

            var r = await AnnounceService.SendAsync(text, from, duration, new[] { code });
            if (r.Ok) serverOk++;
            else if (string.Equals(r.Error, "revoked", StringComparison.OrdinalIgnoreCase)) revoked.Add(code);
            else failed++;
        }

        // 服务端说「对方已删除你的登记」：把本地那条也删掉，免得一直留着发不出去
        if (revoked.Count > 0)
        {
            App.Settings.Current.AnnounceSavedPeers.RemoveAll(p => revoked.Contains(p.Code));
            App.Settings.Save();
            RenderSaved();
        }

        _sending = false;
        SendButton.IsEnabled = (ContentBox.Text?.Length ?? 0) > 0;

        // 手填的码发成功了就顺手存起来，下次直接勾选（已存在的不会重复加）
        if (manualCode.Length > 0 && lanOk + serverOk > 0) SavePeer(manualCode, notify: false);

        var parts = new List<string>();
        if (lanOk > 0) parts.Add($"局域网直达 {lanOk} 台");
        if (serverOk > 0) parts.Add($"服务器中转 {serverOk} 台");
        if (failed > 0) parts.Add($"失败 {failed} 台");
        if (revoked.Count > 0) parts.Add($"{revoked.Count} 台已删除本机的登记（已从「已保存的电脑」移除）");

        if (failed == 0)
        {
            ContentBox.Text = "";   // 内容清空、署名保留（连续喊话常见）
            ShowResult(true, "已发送：" + string.Join("，", parts) + "。");
        }
        else
        {
            ShowResult(lanOk + serverOk > 0, "已发送：" + string.Join("，", parts) + "。");
        }
    }

    private void ShowResult(bool ok, string message)
    {
        SendResult.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        SendResult.Title = ok ? "已完成" : "未完成";
        SendResult.Message = message;
        SendResult.IsOpen = true;
    }
}
