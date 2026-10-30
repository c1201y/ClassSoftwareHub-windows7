// TODO(win7): 上游本文件用 Microsoft.UI.Xaml 的 ContentDialog / StackPanel / TextBlock / ProgressBar、
//   Microsoft.UI.Text.FontWeights、Microsoft.UI.Xaml 的 Thickness / TextWrapping / TextTrimming，
//   以及 DispatcherQueue / App.MainWindow / Application.Current.Exit()。移植映射：
//     · ContentDialog（带 XamlRoot）        → FluentAvalonia.UI.Controls.ContentDialog（owner 走 ShowAsync(top)）
//     · Microsoft.UI.Xaml.Controls.*        → Avalonia.Controls.*（StackPanel / TextBlock / ProgressBar）
//     · Microsoft.UI.Text.FontWeights       → Avalonia.Media.FontWeight
//     · Microsoft.UI.Xaml.Thickness         → Avalonia.Thickness
//     · TextWrapping / TextTrimming         → Avalonia.Media 下同名
//     · DispatcherQueue.TryEnqueue          → Avalonia.Threading.Dispatcher.UIThread.Post
//     · Application.Current.Exit()          → 桌面生命周期 Shutdown()
//   ⚠️ 本文件依赖主控负责的 MainWindow 提供：public ExitApp()、NotifyUpdateReady(string)、
//      NotifyBackgroundDownloadFailed(string,string)（上游 MainWindow 已有，随 MainWindow 同步补齐）。
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>
/// 更新流程：**先问用户要不要更新**（绝不强制），用户点了「立即更新」才进入
/// 「下载 → 校验 → 静默安装 → 自动重启」。
/// 设置页的「检查更新」和启动时的自动检查都走这里。
///
/// ⚠️ 移植说明（WinUI → Avalonia）：
///    · 原版 <c>ContentDialog</c> 要 <c>XamlRoot</c> 才能挂载；FluentAvalonia 的 <c>ContentDialog</c>
///      **没有 XamlRoot**，<c>ShowAsync()</c> 自己找宿主窗口（也可显式传 <c>TopLevel</c>/<c>Window</c>）。
///      所以原来第一个 <c>XamlRoot xamlRoot</c> 参数换成可选的 <c>TopLevel? owner</c>（不传就走默认宿主）。
///    · <c>FontWeights.SemiBold</c> → <c>FontWeight.SemiBold</c>；<c>TextWrapping</c>/<c>TextTrimming</c>
///      在 <c>Avalonia.Media</c> 下同名；<c>Thickness</c> 在 <c>Avalonia</c> 下同名。
///    · <c>DispatcherQueue.TryEnqueue</c> → <c>Dispatcher.UIThread.Post</c>。
///    · <c>Application.Current.Exit()</c> → 桌面生命周期对象的 <c>Shutdown()</c>。
/// </summary>
public static class UpdateFlow
{
    /// <summary>用户对「发现新版本」弹窗的选择。</summary>
    public enum UpdateChoice
    {
        /// <summary>稍后（关闭 / Esc）。</summary>
        Later,
        /// <summary>立即更新：下载 → 校验 → 静默安装 → 自动重启。</summary>
        Now,
        /// <summary>后台下载：不弹进度窗，下载完发系统通知，装不装等用户点。</summary>
        Background,
    }

    /// <summary>
    /// 第一步：把「发现新版本」摆给用户看，让他自己决定。
    /// </summary>
    public static async Task<UpdateChoice> AskAsync(UpdateRelease release, TopLevel? owner = null)
    {
        if (release.Primary is not { } package) return UpdateChoice.Later;

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"新版本：{release.Tag}",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var detail = $"安装包：{package.SizeText}　·　通道：{UpdateChannels.ToDisplay(release.Channel)}";
        if (release.PublishedAt is { } when) detail += $"　·　{when.LocalDateTime:yyyy-MM-dd}";
        body.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        if (!string.IsNullOrWhiteSpace(release.Notes))
        {
            body.Children.Add(new TextBlock
            {
                Text = "本次更新内容：",
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 0),
            });
            body.Children.Add(new TextBlock
            {
                Text = release.Notes.Trim(),
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 8,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = "更新为自愿操作，不会自动安装。可「立即更新」现在就装，「后台下载」先下好稍后安装，或「稍后」暂不处理。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var dialog = new ContentDialog
        {
            Title = "发现新版本",
            Content = body,
            PrimaryButtonText = "立即更新",
            SecondaryButtonText = "后台下载",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };

        // ⚠️ 移植：原版靠 dialog.XamlRoot 挂载，直接 ShowAsync()；
        //    FluentAvalonia 没有 XamlRoot，未显式给 owner 时 ShowAsync() 自己找宿主窗口。
        var result = owner is null ? await dialog.ShowAsync() : await dialog.ShowAsync(owner);
        return result switch
        {
            ContentDialogResult.Primary => UpdateChoice.Now,
            ContentDialogResult.Secondary => UpdateChoice.Background,
            _ => UpdateChoice.Later,
        };
    }

    /// <summary>
    /// 第二步（用户已经同意）：下载 → 校验 → 静默安装 → 退出应用（装好会自动重新打开）。
    /// 返回 false = 失败（调用方自己提示）。成功的话应用会直接退出。
    /// </summary>
    public static async Task<bool> RunAsync(
        UpdateService service, UpdateRelease release, string title = "正在更新", TopLevel? owner = null)
    {
        if (release.Primary is not { } package) return false;

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var status = new TextBlock { Text = $"正在下载 {release.Tag}", TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock
        {
            Text = "下载完成后先进行 MD5 校验（防损坏 / 防替换），校验通过后方可安装。" +
               "安装过程中应用将自动关闭，安装完成后自动重新打开（约十几秒），此过程并非程序异常。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(bar);
        panel.Children.Add(status);
        panel.Children.Add(note);

        var allowClose = false;          // 收尾阶段（正在安装）才允许关窗
        var userCancelled = false;       // 用户主动点了「取消下载」
        var closedByUser = false;        // 弹窗已经被用户关掉了，后面别再 Hide

        // ⚠️ 这个 token 就是"卡死时的出口"。原来它根本没接线：
        // UpdateService 里的 HttpClient 是 Timeout.InfiniteTimeSpan（注释写着"靠 CancellationToken 控制"），
        // 但 DownloadAndVerifyAsync 调用时没传 ct —— 于是连接建立后传输停滞（半开连接、镜像挂起）时
        // ReadAsync 会永久阻塞，弹窗又关不掉，用户只能杀进程。
        using var cts = new CancellationTokenSource();

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "立即更新",
            CloseButtonText = "取消下载",
            DefaultButton = ContentDialogButton.Primary,
        };

        // 主按钮只是"更新进行中"的指示，点了不该关窗
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = true;

        dialog.Closing += (_, args) =>
        {
            if (allowClose) return;

            // 点「取消下载」/ Esc / 点窗外 —— 一律：允许马上关掉，并且真的把下载停掉。
            // 绝不 Cancel 关窗：宁可下载被取消，也不能把用户困在一个关不掉的弹窗里。
            closedByUser = true;
            userCancelled = true;
            try { cts.Cancel(); } catch { /* 已经取消过了 */ }
        };

        // ⚠️ 这里原来把 ShowAsync 的 Task 直接丢掉（`_ = ...`）：弹窗解析不到宿主、或已经有
        //    ContentDialog 在显示（RunAsync 被调两次）时会抛 InvalidOperationException ——
        //    异常被静默吞掉 → 弹窗根本没出现，dialog.Closing 也就从没挂上 →
        //    userCancelled 永远 false、cts 永远取消不了，下载在"没界面、停不掉"的状态下跑完。
        //    现在：同步抛的直接中止；异步失败的把异常观察掉并取消下载（宁可不下，也别闷头跑）。
        try
        {
            var showTask = owner is null ? dialog.ShowAsync() : dialog.ShowAsync(owner);
            _ = showTask.ContinueWith(
                t =>
                {
                    _ = t.Exception;
                    try { cts.Cancel(); } catch { /* 已取消 / 已释放 */ }
                },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        catch (Exception)
        {
            // 弹不出进度窗就不能往下走：用户既看不到进度，也按不到「取消下载」
            allowClose = true;
            return false;
        }

        // 空闲超时（不是总时长）：连续这么久没有任何进度就认定卡死。
        // 慢速下载不会被误杀 —— 每来一次进度就续期一次。
        const int IdleSeconds = 45;
        cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds));

        try
        {
            // 进度改成 DownloadProgress（2026-10-18 自上游 1.2.0 移植）：
            // 它带已收字节 / 总字节 / 速度，任务页的卡片与普通下载是同一套快照。
            var progress = new Progress<DownloadProgress>(p =>
            {
                bar.Value = p.Indeterminate ? 0 : p.Percent;
                status.Text = p.Indeterminate
                    ? $"正在下载 {release.Tag}　{p.SizeText}"
                    : $"正在下载 {release.Tag} {p.Percent:0}%　{p.SizeText}"
                      + (p.SpeedText.Length > 0 ? "  ·  " + p.SpeedText : "");
                try { cts.CancelAfter(TimeSpan.FromSeconds(IdleSeconds)); } catch { /* 已取消/已释放 */ }
            });

            var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);

            // 本地已有同名安装包（上次更新 / 回滚留下的）：先校验，过了就不重新下载 ——
            // 回滚旧版本时这一步能省一次上百 MB 的下载（这也是「本地保留最近 N 个安装包」的意义）。
            // 校验不过就删掉重下，绝不让坏包蒙混过关。
            var downloaded = await TryReuseLocalAsync(package, destination, t => status.Text = t, cts.Token)
                             ?? await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

            cts.CancelAfter(Timeout.Infinite);   // 下载完了，别再触发超时打断安装
            bar.Value = 100;

            // ⚠️⚠️ 安装前最后一道闸：**未校验的包绝不能执行**。
            // DownloadAndVerifyAsync 现在保证 Verified 一定为 true（否则抛 MissingChecksumException），
            // 这里再拦一次是双保险 —— 界面上白纸黑字承诺「校验通过后方可安装」（见上面 note 的原文），
            // 而旧实现只把这句话显示到状态栏、从不看这个标记。
            if (!downloaded.Verified)
            {
                allowClose = true;
                status.Text = "该发布未提供校验值，已拒绝安装（不运行未经完整性校验的安装包）。";
                await Task.Delay(2500);
                TryHide(dialog, closedByUser);
                return false;
            }

            status.Text = $"下载完成（{downloaded.VerifyNote}），正在安装：安装完成后应用将自动重新打开。";

            // ⚠️⚠️ RunInstaller 只是"安排"（它内部会先等约 3 秒才拉起安装程序），
            //    随后必须**立刻真的退出应用**：Inno 一启动就查 AppMutex，
            //    这时应用还在的话安装会被静默取消（详见 UpdateService.RunInstaller 注释）。
            //    顺序反了（先装后退 / 不退）就是 2026-10-01「1.2 升不到 1.3」的根因。
            UpdateService.RunInstaller(downloaded.FilePath);
            allowClose = true;
            await Task.Delay(400);       // 让 cmd 来得及把延迟启动安排下去，再走
            ExitAppNow();
            return true;
        }
        catch (OperationCanceledException)
        {
            allowClose = true;

            // 用户自己取消的：他已经知道了，不用再解释
            if (userCancelled) return false;

            status.Text = $"下载停滞（连续 {IdleSeconds} 秒无进度），已取消。\n" +
                          "可能为网络问题或下载源无响应，可稍后重试。";
            await Task.Delay(2500);
            TryHide(dialog, closedByUser);
            return false;
        }
        catch (Exception ex)
        {
            allowClose = true;
            status.Text = "更新失败：" + ex.Message;
            await Task.Delay(2500);
            TryHide(dialog, closedByUser);
            return false;
        }
    }

    private static void TryHide(ContentDialog dialog, bool alreadyClosed)
    {
        if (alreadyClosed) return;   // 用户已经关掉了，再 Hide 会抛
        try { dialog.Hide(); } catch { /* 已经关了就算了 */ }
    }

    // ── 后台下载 ─────────────────────────────────────────────────────

    private static int _bgBusy;      // 0 = 空闲，1 = 后台下载进行中（同一时间只允许一个）

    /// <summary>
    /// 后台下载：不弹进度窗，静默走「校验本地包 → 下载 → 校验」，
    /// 成功后记入存档待装标记并弹系统通知（带 现在安装 / 稍后安装 按钮）；
    /// 失败也报一声（托盘气泡兜底）。返回 false = 已经有一个在下了。
    /// </summary>
    public static bool StartBackgroundDownload(UpdateService service, UpdateRelease release)
    {
        if (release.Primary is not { } package) return false;
        if (Interlocked.Exchange(ref _bgBusy, 1) == 1) return false;

        _ = Task.Run(async () =>
        {
            // ── 更新下载也进「任务进行」页（2026-10-18 自上游 1.2.0 移植） ──
            // 任务页、导航徽标、完成通知这些观察者都靠 DownloadManager 的台账工作，
            // 所以这里登记一条 DownloadTask，把进度与结局回报进去，界面一行都不用改。
            DownloadTask? tracked = null;
            try
            {
                // ⚠️ 必须回 UI 线程登记：TrackUpdate 往 ObservableCollection 里 Insert，
                //    而这里整段跑在 Task.Run 的线程池线程上（进度/收尾早就用 RunOnUi 了，唯独漏了登记这一步）。
                tracked = RunOnUiGet(() => DownloadManager.Current.TrackUpdate(
                    $"更新 {release.Tag}", release.Tag,
                    "正在下载更新包。下载完成后可点「安装」立即安装（安装时会先退出本程序）。",
                    package.Url?.ToString() ?? ""));
            }
            catch
            {
                // 登记失败不该影响下载本身（只是任务页上看不见这一条）
            }

            try
            {
                var destination = Path.Combine(UpdateService.UpdatesDir, package.Name);
                using var cts = new CancellationTokenSource();
                if (tracked is not null)
                {
                    // 外部托管：卡片上的「取消」必须能掐到真正在跑的那次传输
                    tracked.AdoptCancellation(cts);
                }
                cts.CancelAfter(TimeSpan.FromSeconds(45));   // 空闲超时，同 RunAsync 的规矩
                var progress = new Progress<DownloadProgress>(p =>
                {
                    // ① 任务页卡片：与普通下载同一套快照（百分比 / 已收 / 速度）
                    // ⚠️ 必须回 UI 线程：Progress<T> 是在 Task.Run 里 new 的，没有同步上下文，
                    //    回调直接跑在线程池；而 ReportProgress 会改绑定到界面的任务对象/集合。
                    if (tracked is not null)
                        RunOnUi(() => DownloadManager.Current.ReportProgress(tracked, p));

                    // ② 续期空闲超时
                    try { cts.CancelAfter(TimeSpan.FromSeconds(45)); } catch { /* 已取消 */ }
                });

                var downloaded = await TryReuseLocalAsync(package, destination, null, cts.Token)
                                 ?? await service.DownloadAndVerifyAsync(package, destination, progress, cts.Token);

                // 与前台 RunAsync 同一道闸：**没验过的包不许进待装标记**（否则首页横幅会把
                // 一个从没校验过的安装包摆到用户面前，点「现在安装」就执行了）。
                if (!downloaded.Verified)
                    throw new InvalidOperationException("该发布未提供校验值，已拒绝下载安装包（" + downloaded.VerifyNote + "）。");

                // 记入存档：首页横幅与「稍后安装」都认它
                var settings = App.Settings;
                settings.Current.UpdatePendingPath = downloaded.FilePath;
                settings.Current.UpdatePendingTag = release.Tag;
                // 记下校验值：安装前要据此重新核验（见 VerifyPendingPackageAsync）
                settings.Current.UpdatePendingMd5 = downloaded.Md5;
                settings.Current.UpdatePendingSha256 = downloaded.Sha256;
                settings.Save();

                if (tracked is not null)
                {
                    var size = 0L;
                    try { size = new FileInfo(downloaded.FilePath).Length; } catch { }
                    RunOnUi(() => DownloadManager.Current.CompleteExternal(tracked, downloaded.FilePath, size));
                }

                RunOnUi(() => App.MainWindow?.NotifyUpdateReady(release.Tag));
            }
            catch (OperationCanceledException)
            {
                // 用户点的取消 vs 闲置超时：两件事在日志和界面上说法完全不同（见 DownloadTask.CanceledByUser）
                if (tracked is not null && tracked.CanceledByUser)
                    RunOnUi(() => DownloadManager.Current.CancelExternal(tracked));
                else
                {
                    if (tracked is not null)
                        RunOnUi(() => DownloadManager.Current.FailExternal(tracked, "下载停滞（连续 45 秒无进度）"));
                    RunOnUi(() => App.MainWindow?.NotifyBackgroundDownloadFailed(release.Tag, "下载停滞（连续 45 秒无进度）"));
                }
            }
            catch (Exception ex)
            {
                if (tracked is not null)
                    RunOnUi(() => DownloadManager.Current.FailExternal(tracked, ex.Message));
                RunOnUi(() => App.MainWindow?.NotifyBackgroundDownloadFailed(release.Tag, ex.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _bgBusy, 0);
            }
        });
        return true;
    }

    /// <summary>立即安装待装的更新（首页横幅按钮 / 通知「现在安装」都走这里）。须在 UI 线程调用。</summary>
    public static async Task InstallPendingNowAsync()
    {
        var path = App.Settings.Current.UpdatePendingPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        if (!await VerifyPendingPackageAsync(path)) return;

        if (!await TryStartInstallerAsync(path)) return;

        await Task.Delay(400);       // 让 cmd 来得及把延迟启动安排下去，再走
        ExitAppNow();
    }

    /// <summary>
    /// 「任务进行」页的更新包点「安装」（2026-10-18 自上游 1.2.0 移植）。
    ///
    /// 与 <see cref="InstallPendingNowAsync"/> 是同一条路，差别只在取哪条路径：
    /// 后者从设置里的"待装标记"拿，这里直接从**这一条任务的落盘路径**拿 ——
    /// 用户刚从任务页点「安装」时，待装标记可能还没写（后台下载刚回报完成）。
    ///
    /// ⚠️ 顺序必须一致：先拉起安装器、成功之后再退应用（Inno 一启动就查 AppMutex，
    ///    应用还在跑会被静默取消，用户端只表现为"版本没变"）。
    /// </summary>
    public static async Task<bool> InstallPendingFromTaskPageAsync(DownloadTask task)
    {
        var path = task.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        // 如果这条任务正好是"待装包"，同样要按下载时记下的校验值复核（见 VerifyPendingPackageAsync）
        var pendingPath = App.Settings.Current.UpdatePendingPath;
        if (string.Equals(pendingPath, path, StringComparison.OrdinalIgnoreCase)
            && !await VerifyPendingPackageAsync(path))
            return false;

        if (!await TryStartInstallerAsync(path)) return false;

        await Task.Delay(400);
        ExitAppNow();
        return true;
    }

    /// <summary>
    /// 安装前重新核验待装包。
    /// ⛔ 下载时校验过，不代表**现在**还是那个文件 —— 落盘之后可能被磁盘错误写坏、
    ///    被同名文件覆盖、被人替换。执行安装包是整条更新链路里破坏性最大的一步，
    ///    不能"文件还在"就直接运行。
    /// 没有记录校验值的待装标记来自旧版本 → 放行并记日志（否则会凭空作废用户已下好的包）。
    /// </summary>
    private static async Task<bool> VerifyPendingPackageAsync(string path)
    {
        var s = App.Settings.Current;
        var expectedMd5 = s.UpdatePendingMd5 ?? "";
        var expectedSha = s.UpdatePendingSha256 ?? "";

        if (expectedMd5.Length == 0 && expectedSha.Length == 0)
        {
            Core.AppLog.Info("update", $"待装包没有记录校验值（旧版本遗留），跳过安装前复核：{path}");
            return true;
        }

        try
        {
            var (actualMd5, actualSha) = await UpdateService.HashFileAsync(path, expectedSha.Length > 0, CancellationToken.None);

            var ok = expectedMd5.Length > 0
                ? string.Equals(expectedMd5, actualMd5, StringComparison.OrdinalIgnoreCase)
                : string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase);

            // MD5 对上但发布方还给了 SHA256 时，一并核对
            if (ok && expectedMd5.Length > 0 && expectedSha.Length > 0 && actualSha.Length > 0)
                ok = string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase);

            if (ok) return true;

            Core.AppLog.Warn("update",
                $"待装包复核不通过（期望 MD5={expectedMd5} / SHA256={expectedSha}，" +
                $"实际 MD5={actualMd5} / SHA256={actualSha}）：{path}");

            s.UpdatePendingPath = "";
            s.UpdatePendingTag = "";
            s.UpdatePendingMd5 = "";
            s.UpdatePendingSha256 = "";
            App.Settings.Save();

            try { File.Delete(path); }
            catch (Exception ex) { Core.AppLog.Warn("update", $"删除复核失败的安装包失败：{ex.Message}"); }

            await ShowMessageAsync("安装包校验失败",
                "安装包在下载完成后发生了变化（可能已损坏或被替换）。为安全起见已删除，请重新下载更新。");
            return false;
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("update", ex, "安装前复核待装包");
            await ShowMessageAsync("安装没有开始",
                "无法读取安装包完成校验：" + ex.Message + "\n\n应用没有退出，安装包也还在原处，可稍后重试。");
            return false;
        }
    }

    /// <summary>一个只有一个「知道了」按钮的提示框（失败路径用，异常不外抛）。</summary>
    private static async Task ShowMessageAsync(string title, string text)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了",
            };
            var owner = App.MainWindow;
            if (owner is null) await dialog.ShowAsync();
            else await dialog.ShowAsync(owner);
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("update", $"提示框显示失败（{title}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 拉起安装器；返回 false = **没拉起来，绝对不能退应用**。
    ///
    /// RunInstaller 现在会对装不了的包（扩展名不是 .exe/.msi）和"拉不起来"抛异常，
    /// 而这三处调用点都是「拉起 → 退应用」的顺序：不接住就变成
    /// **应用退了、什么都没装、用户还完全不知情**（正是报告里 HIGH-4/HIGH-5 的形态）。
    /// ⚠️ 这些方法的调用方里有 <c>async void</c> 事件处理器，异常绝不能漏出去。
    /// </summary>
    private static async Task<bool> TryStartInstallerAsync(string installerPath)
    {
        try
        {
            UpdateService.RunInstaller(installerPath);
            return true;
        }
        catch (Exception ex)
        {
            ScreenCapture.Log($"[update] 拉起安装程序失败（{installerPath}）：{ex.Message}");
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "安装没有开始",
                    Content = new TextBlock
                    {
                        Text = ex.Message + "\n\n应用没有退出，安装包也还在原处，可稍后重试。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CloseButtonText = "知道了",
                };
                var owner = App.MainWindow;
                if (owner is null) await dialog.ShowAsync();
                else await dialog.ShowAsync(owner);
            }
            catch
            {
                // 连提示都弹不出来：日志已经记下了，别再把异常抛给调用方
            }
            return false;
        }
    }

    /// <summary>
    /// 用本地已有的安装包覆盖安装（设置页「本地安装包 → 覆盖安装」）。
    /// 「本地保留最近 N 个安装包」的意义就在这儿：想重装 / 装回旧版时不必再下一遍。
    ///
    /// ⚠️⚠️ 顺序与在线更新完全一致：<see cref="UpdateService.RunInstaller"/> 只是"安排"
    ///    （它自己会先等约 3 秒），随后必须**立刻真的退出应用** —— Inno 一启动就查 AppMutex，
    ///    应用还在跑的话安装会被静默取消、用户端只表现为"版本没变"（2026-10-01 的根因）。
    /// </summary>
    public static async Task InstallLocalAsync(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath)) return;

        if (!await TryStartInstallerAsync(installerPath)) return;

        await Task.Delay(400);       // 让 cmd 来得及把延迟启动安排下去，再走
        ExitAppNow();
    }

    /// <summary>
    /// 真正把应用退掉，给随后的安装程序腾位置。
    ///
    /// ⚠️⚠️ 必须走 <see cref="MainWindow.ExitApp"/>（它会先置 <c>_exitRequested</c>）。
    ///    直接调 <c>Application.Current.Exit()</c> 会被 MainWindow 里 AppWindow.Closing 的托盘逻辑
    ///    拦成"用户点了 ×" → <c>SW_HIDE</c> 躲进托盘，**进程不退** → 安装程序查到 AppMutex 占用 →
    ///    静默模式自动取消 → 安装失败且无任何提示。这就是 2026-10-01「1.2 无法更新到 1.3」的根因。
    ///    ⚠️ 移植：Avalonia 侧同样是"必须走 MainWindow.ExitApp()"这条路，见上面注释。
    /// </summary>
    private static void ExitAppNow()
    {
        if (App.MainWindow is { } win)
        {
            win.ExitApp();
            return;
        }

        // 主窗口已经没了（理论上到不了这儿）：退而求其次
        // ⚠️ 移植：原版 Application.Current.Exit() → Avalonia 桌面生命周期 Shutdown()
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Environment.Exit(0);
        }
        catch { Environment.Exit(0); }
    }

    private static void RunOnUi(Action action)
    {
        // ⚠️ 移植：原版 App.MainWindow?.DispatcherQueue + TryEnqueue(() => action())，
        //    队列取不到（主窗没了）时直接跑。Avalonia 用 Dispatcher.UIThread.Post 投递到 UI 线程。
        if (App.MainWindow is null) { action(); return; }
        Dispatcher.UIThread.Post(action);
    }

    /// <summary>需要返回值的版本（如登记任务要拿回 DownloadTask）：在 UI 线程同步执行并取结果。</summary>
    private static T RunOnUiGet<T>(Func<T> func)
    {
        if (App.MainWindow is null || Dispatcher.UIThread.CheckAccess()) return func();
        return Dispatcher.UIThread.Invoke(func);
    }

    /// <summary>
    /// 本地同名安装包复用：MD5（发布方提供了就必过）→ SHA256（同）→ 都没有就**不复用**。
    /// 任何一项对不上：删掉本地文件、返回 null 走正常下载。返回 null 也涵盖「本地没有包」。
    /// </summary>
    private static async Task<DownloadedPackage?> TryReuseLocalAsync(
        UpdatePackage package, string path,
        Action<string>? status, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(package.Name) || !File.Exists(path)) return null;
            status?.Invoke("发现本地已有该版本的安装包，正在校验…");

            var expectedMd5 = await UpdateService.ResolveMd5Async(package, ct);
            var wantSha = (package.Sha256 ?? "").Length > 0;
            var (md5, sha) = await UpdateService.HashFileAsync(path, wantSha, ct);

            if (expectedMd5.Length > 0 && !string.Equals(expectedMd5, md5, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuiet(path);
                return null;
            }
            if (wantSha && !string.Equals(package.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuiet(path);
                return null;
            }
            if (expectedMd5.Length == 0 && !wantSha)
            {
                // 该发布一个校验值都没给：本地包再"看起来对"也无从校验 → 交给下载路径，
                // 那边会直接抛 MissingChecksumException（见 UpdateService.DownloadAndVerifyAsync），
                // 界面据此提示"该发布未提供校验值"，不会白下大文件。
                //
                // ⚠️ 这里**绝不能删本地包**（原来会拿 package.Size 比，对不上就删）：
                //    ① 接口缺 size 时 GitHubReleaseSource 给的是 0 → 一次"检查更新"就把用户
                //       留着回滚 / 离线覆盖安装的包删掉，再重下几百 MB；
                //    ② 既然压根不打算用它，删它一点好处都没有。
                return null;
            }
            return new DownloadedPackage(path, md5, sha, true, "本地安装包校验通过，未重新下载");
        }
        catch
        {
            // 本地包读不了 / 网络取校验值失败等：一律退回正常下载，不在复用这条路上添堵
            return null;
        }
    }

    private static void DeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就让下载流程覆盖它 */ }
    }

    /// <summary>
    /// 把「待装更新」补进任务列表（2026-10-22 自上游 1.3 移植）。
    /// <para>
    /// 起因：包是**上一次会话**下好的（<c>UpdatePendingPath</c> 存在设置里），重启后任务列表是空的 ——
    /// 用户在「任务进行」页看不到它，只能去首页横幅点安装。这里按需补一条已完成的更新任务卡片，
    /// 与「后台下载完成」那条完全同构（同一个 TrackUpdate + CompleteExternal 套路）。
    /// </para>
    /// <para>⚠️ 幂等：已存在同 tag / 同路径的更新任务就直接返回，别重复加卡片。</para>
    /// </summary>
    public static void EnsurePendingTaskVisible()
    {
        var s = App.Settings.Current;
        var path = s.UpdatePendingPath ?? "";
        if (path.Length == 0 || !File.Exists(path)) return;

        var tag = s.UpdatePendingTag ?? "";

        foreach (var t in DownloadManager.Current.Tasks)
            if (t.IsUpdate && (tag.Length == 0 ? t.Path == path : t.UpdateTag == tag)) return;

        var size = SizeOfFile(path);
        var task = DownloadManager.Current.TrackUpdate(
            $"ClassSoftwareHub {(tag.Length > 0 ? tag : "新版本")}", tag, UpdateTaskNote(), "");
        DownloadManager.Current.CompleteExternal(task, path, size);
    }

    /// <summary>更新任务卡片上的说明文案（与后台下载那条保持一致）。</summary>
    private static string UpdateTaskNote() => "应用更新包　·　可在此直接安装，无需手动查找安装包";

    /// <summary>文件大小（拿不到就 0，卡片上少显示一个数字而已）。</summary>
    private static long SizeOfFile(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
