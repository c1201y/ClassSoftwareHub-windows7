using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 软件详情页（原生）。版式照站点 DownloadDetailPage.vue：
/// 头部（图标 / 名称 / 标语 / 分类徽标）→ 应用介绍 → 详细信息（版本·体积·系统·官网·GitHub）
/// → 小提示 → 下载（应用商店卡 + 下载项：平台·备注·体积·校验值 + 下载 + GitHub 加速）。
/// 所有链接都交给系统（浏览器 / 下载工具）。
/// </summary>
public sealed partial class DetailPage : PageBase
{
    private string _storeUrl = "";
    private Button? _lastCopyButton;
    private DispatcherTimer? _copyTimer;
    private SoftwareApp? _app;

    public DetailPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            // 离开页面把「已复制」提示定时器停掉
            _copyTimer?.Stop();
            DetachApp();
        };
    }

    /// <summary>
    /// 退订图标状态通知。
    ///
    /// ⚠️ 必须在**离开页面时**退订。`_app` 来自 App.Content.Apps —— 那是进程级静态单例，
    /// 委托是"单例持有页面"，页面不持有单例；而宿主 Frame 的 CacheSize = 0、页面根本不缓存。
    /// 所以只靠 OnNavigatedTo 里"退订上一个"救不了：**每进一次详情页就永久泄漏一个 Page**，
    /// 翻得越多漏得越多（教学机内存小，必须堵）。
    ///
    /// OnNavigatedFrom 与 Unloaded 都挂一道：前者是导航时必定触发，后者兜住非导航的移除，
    /// 重复调用无害（`_app` 已置空就直接返回）。
    /// </summary>
    private void DetachApp()
    {
        if (_app is null) return;
        _app.PropertyChanged -= App_PropertyChanged;
        _app = null;
    }

    public override void OnNavigatedFrom()
    {
        DetachApp();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        // 换软件时先把上一个的图标通知退订，避免占位字形被旧软件的状态带歪
        if (_app is not null) _app.PropertyChanged -= App_PropertyChanged;

        var ui = App.Content.Ui;
        IntroTitle.Text = ui.T("detail.intro", "应用介绍");
        InfoTitle.Text = ui.T("detail.info", "详细信息");
        DownloadsTitle.Text = ui.T("detail.downloads", "下载");
        StoreTitle.Text = ui.T("detail.store-title", "在 Microsoft Store 中获取");
        StoreDesc.Text = ui.T("detail.store-desc", "由微软商店托管，安装后自动更新，无需手动下载安装包。");
        StoreButton.Content = ui.T("detail.store-button", "获取");
        StoreOnlyHint.Text = ui.T("detail.store-only", "该软件通过 Microsoft Store 分发，单击上方按钮打开商店页面即可获取");

        // 商店图标（2026-10-19，自上游 1.3）：内嵌资源解到缓存再绑，避免 unpackaged 下取不到
        if (StoreLogo.Source is null) StoreLogo.Source = LoadStoreLogo();
        StoreLogo.IsVisible = StoreLogo.Source is not null;

        var app = App.Content.FindById(parameter as string ?? "");
        if (app is null)
        {
            AppName.Text = ui.T("detail.not-found", "未找到该软件。");
            // ⚠️ Avalonia 没有 Visibility 枚举 → bool IsVisible。
            AppTagline.IsVisible = false;
            CategoryBadge.IsVisible = false;
            HeroMeta.IsVisible = false;
            IntroTitle.IsVisible = false;
            AppDescription.IsVisible = false;
            InfoTitle.IsVisible = false;
            DownloadsTitle.IsVisible = false;
            Rail.IsVisible = false;
            BodyGrid.ColumnDefinitions[1].Width = new GridLength(0);
            Hero.Opacity = 0;
            return;
        }

        // 找到软件了：把上一页可能留下的"未找到"隐藏态恢复回来
        Rail.IsVisible = true;
        BodyGrid.ColumnDefinitions[1].Width = new GridLength(300);

        // ── 头部 ──
        AppName.Text = app.Name;
        AppTagline.Text = app.Tagline;
        AppTagline.IsVisible = app.Tagline.Length > 0;

        var category = app.CategoryDisplay.Length > 0 ? app.CategoryDisplay : App.Content.CategoryName(app.Category);
        CategoryText.Text = category;
        CategoryBadge.IsVisible = category.Length > 0;

        // 占位字形先兜底（没图/加载中/失败都显示），图片加载成功后由 IconPlaceholder 通知收掉
        _app = app;
        app.PropertyChanged += App_PropertyChanged;
        ApplyIcon();

        // ── 应用介绍 ──
        AppDescription.Text = app.Description;
        AppDescription.IsVisible = app.Description.Length > 0;
        IntroTitle.IsVisible = AppDescription.IsVisible;

        // ── 小提示 ──
        if (app.Notice.Length > 0)
        {
            NoticeBar.Title = ui.T("detail.notice-title", "小提示");
            NoticeBar.Message = app.Notice;
            NoticeBar.IsOpen = true;
            NoticeBar.IsVisible = true;
        }
        else
        {
            // ⛔ 关着的 InfoBar 仍会占掉一排高度，必须显式收起
            NoticeBar.IsOpen = false;
            NoticeBar.IsVisible = false;
        }

        // ── 详细信息 ──
        InfoList.Children.Clear();
        AddInfoRow(ui.T("detail.version", "软件版本"), app.Version);
        AddInfoRow(ui.T("detail.size", "软件体积"), app.Size);
        AddInfoRow(ui.T("detail.system", "系统限制"), app.System);
        AddLinkRow(ui.T("detail.website", "官方网站"), app.Website);
        AddLinkRow(ui.T("detail.github", "GitHub"), app.Github);
        FinishInfoRows();   // 行与行之间补细分割线（末行不画）

        // ── 下载 ──
        // 商店项不进下载列表，交给上方那张商店卡（2026-10-18 对齐上游：判据是 DownloadLinkKind）
        _storeUrl = app.Store.Length > 0
            ? app.Store
            : app.Downloads.FirstOrDefault(d => d.Kind == DownloadKind.Store)?.Url ?? "";
        var others = app.Downloads.Where(d => d.Kind != DownloadKind.Store).ToList();

        StoreCard.IsVisible = _storeUrl.Length > 0;
        StoreOnlyHint.IsVisible = _storeUrl.Length > 0 && others.Count == 0;
        DownloadList.ItemsSource = others;

        // 微软商店本体是个特例：这台电脑要是没装商店，商店链接点开是没反应的 —— 提示一下怎么装回来
        var isStoreItself = Services.StoreRepair.IsStoreItself(app.Id, _storeUrl);
        if (isStoreItself && _storeUrl.Length > 0)
        {
            StoreOnlyHint.Text = "本机未安装 Microsoft Store 时，「打开」将无响应，此时可选择「一键恢复商店」。";
            StoreOnlyHint.IsVisible = true;
        }

        // ── 数据问题 ──
        var issues = App.Content.Issues
            .Where(i => i.File.Contains(app.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (issues.Count > 0)
        {
            IssueBar.Title = "该软件的数据存在提示";
            IssueBar.Message = string.Join("\n", issues.Select(i => i.File + "：" + i.Message));
            IssueBar.IsOpen = true;
        }

        // ── 顶栏副标题、推荐应用、主题色（2026-10-18 自上游 1.2.0 移植）──
        var meta = string.Join("　·　", new[] { app.VersionText, app.Size, app.System }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        HeroMeta.Text = meta;
        HeroMeta.IsVisible = meta.Length > 0;

        BuildRecommendations(app);
        _ = ApplyHeroColorAsync(app);
    }

    /// <summary>
    /// 右栏「推荐应用」：先取同分类的，不够就用其他软件补齐（Nick：搜不到就随便推荐）。
    /// ⛔ 标题不能叫「相似应用」—— 补齐的那部分只是别的软件，叫相似名不副实。
    /// ⛔ 用 Id 的稳定哈希排序而不是 Random —— 同一台机器每次进来顺序一致，不会看着"跳来跳去"。
    /// （2026-10-18 自上游 1.2.0 移植；顺带把 Core/StableRank 接上了）
    /// </summary>
    private void BuildRecommendations(SoftwareApp app)
    {
        var pool = App.Content.Apps
            .Where(a => !string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var same = pool
            .Where(a => string.Equals(a.Category, app.Category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => StableRank.Of(a.Id))
            .ToList();

        var picks = same.Take(6).ToList();

        if (picks.Count < 6)
        {
            picks.AddRange(pool
                .Except(picks)
                .OrderBy(a => StableRank.Of(a.Id + "|rest"))
                .Take(6 - picks.Count));
        }

        RecommendList.ItemsSource = picks;
        RecommendList.IsVisible = picks.Count > 0;
        RailEmpty.IsVisible = picks.Count == 0;
    }

    private void Recommend_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SoftwareApp target) return;

        // 详情页 → 详情页：走可视图树上的 ContentFrame（与搜索命中跳详情同一条路）
        var frame = this.FindAncestorOfType<Frame>() ?? App.MainWindow?.Shell.ContentFrame;
        frame?.Navigate(typeof(DetailPage), target.Id);
    }

    /// <summary>
    /// 把当前软件的图标刷到头部（图标是异步下载的，所以要能被重复调用）。
    /// ⚠️ 原版用 ImageBrush 一次性设上、由 BitmapImage 自己驱动 Image 刷新；
    ///    Avalonia 的 Bitmap 是纯数据对象，图标到位后必须重新赋值一次 Source。
    /// </summary>
    private void ApplyIcon()
    {
        IconImage.Source = _app?.IconImage;
        IconFallback.IsVisible = _app?.IconPlaceholder ?? true;
        ApplyAvatar();
        if (_app is { } app) _ = ApplyHeroColorAsync(app);
    }

    /// <summary>
    /// 顶部色带（2026-10-18 自上游 1.2.0 移植）：一律等**图标主色**，颜色没就绪之前色带是透明的（等于页面底色）。
    ///
    /// ⛔⛔ 上游上一版先用主题强调色顶上、几百毫秒后再换成图标色，被 Nick 点名"非常割裂"。
    /// 现在只有两种情况会立刻上色：① 列表页已经预热过（缓存命中，进页面就是最终色）；
    /// ② 取不到色（黑白图标 / SVG / 网络失败）⇒ 才退回强调色，这是兜底、不是过渡。
    /// 其余情况取到后再淡入，读起来像"色带浮现"，不像"颜色换了"。
    /// </summary>
    private async Task ApplyHeroColorAsync(SoftwareApp app)
    {
        if (Services.IconPalette.TryGetCached(app.Icon, out var ready))
        {
            PaintHero(app, ready, fade: false);
            return;
        }

        var picked = await Services.IconPalette.PickAsync(app.Icon);

        if (picked is { } color)
        {
            PaintHero(app, color, fade: true);
            return;
        }

        // 兜底：没有主色可用，直接上强调色（不做淡入，免得看着像还在加载）
        var fallback = (Services.ThemeBrush.Get(this, "AccentFillColorDefaultBrush") as SolidColorBrush)?.Color
                       ?? Color.FromArgb(255, 0, 95, 184);
        PaintHero(app, fallback, fade: false);
    }

    private void PaintHero(SoftwareApp app, Color color, bool fade)
    {
        // 期间可能已经导航到别的软件 —— 比 Id，别把 A 的颜色刷到 B 页面上
        if (_app is null || !string.Equals(_app.Id, app.Id, StringComparison.OrdinalIgnoreCase)) return;

        Hero.Background = Services.IconPalette.Gradient(color);

        if (!fade)
        {
            Hero.Opacity = 1;
            return;
        }

        // ⚠️ WinUI 的 Storyboard + DoubleAnimation → Avalonia 的 Animation（FillMode=Forward）。
        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(260),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 0d) },
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 1d) },
                },
            },
        };

        _ = anim.RunAsync(Hero);
    }

    /// <summary>
    /// 兜底头像的底色 + 首字（2026-10-02）。图标地址是第三方 CDN，教室里经常整片下不来，
    /// 这时页头就是一个彩色方块配应用首字，而不是过去那个灰扑扑的购物袋字形。
    /// </summary>
    private void ApplyAvatar()
    {
        if (_app is null) return;
        try
        {
            IconFallback.Background = _app.AvatarBrush;
            IconFallbackText.Text = _app.AvatarText;
        }
        catch
        {
        }
    }

    /// <summary>详细信息里的一行（左侧标签固定宽度，右侧值可选中复制）。</summary>
    private void AddInfoRow(string label, string value)
    {
        var row = NewInfoRow(label);
        // ⚠️ 原版 TextBlock.IsTextSelectionEnabled → Avalonia 用 SelectableTextBlock。
        var text = new SelectableTextBlock
        {
            Text = value.Length > 0 ? value : App.Content.Ui.T("detail.pending", "待补充"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),   // 原版 Grid.ColumnSpacing=12
            Opacity = value.Length > 0 ? 1.0 : 0.6,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        InfoList.Children.Add(row);
    }

    private void AddLinkRow(string label, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var row = NewInfoRow(label);
        // ⚠️ 原版 HyperlinkButton 是 WinUI 控件；Avalonia 也有同名控件（Avalonia.Controls.HyperlinkButton）。
        var link = new HyperlinkButton { Content = url, Padding = new Thickness(0), Tag = url, Margin = new Thickness(12, 0, 0, 0) };
        link.Click += (s, _) => { if (((HyperlinkButton)s!).Tag is string u) Open(u); };
        Grid.SetColumn(link, 1);
        row.Children.Add(link);
        InfoList.Children.Add(row);
    }

    /// <summary>
    /// 一行详细信息（2026-10-18 自上游 1.2.0 移植）。
    /// ⛔ 不再放进灰色卡片（Nick：灰底很丑）—— 现在靠**行间细分割线 + 上下留白**分组，
    /// 和商店里详情区的做法一致。分割线只画在行与行之间，末行不拖一条尾巴（见下面 FinishInfoRows）。
    /// </summary>
    private static Grid NewInfoRow(string label)
    {
        var row = new Grid { Margin = new Thickness(0, 11, 0, 11) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Opacity = 0.58,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    /// <summary>给信息行之间补分割线：行与行之间画一条，末行不画。</summary>
    private void FinishInfoRows()
    {
        var rows = InfoList.Children.OfType<Grid>().ToList();
        for (var i = 0; i < rows.Count - 1; i++)
        {
            // 把「行 + 分割线」包进一个纵向 StackPanel 里
            var index = InfoList.Children.IndexOf(rows[i]);
            var wrapper = new StackPanel();
            InfoList.Children.RemoveAt(index);
            wrapper.Children.Add(rows[i]);
            wrapper.Children.Add(new Border
            {
                Height = 1,
                Background = Services.ThemeBrush.Get(this, "DividerStrokeColorDefaultBrush") as IBrush
                             ?? new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0)),
            });
            InfoList.Children.Insert(index, wrapper);
        }
    }

    /// <summary>
    /// 微软商店图标（`Assets/StoreLogo.png`，内嵌资源 ⇒ 跟着 exe 走、离线可用）。
    /// ⚠️ WinUI 原版走 <c>Services.EmbeddedAssets.ExtractToCache</c> 解到缓存再用文件路径；
    ///    本移植版的 csproj 只把 <c>Assets\**\*.png</c> 收成 AvaloniaResource（不是 EmbeddedResource），
    ///    所以改成直接读 <c>avares://ClassSoftwareHub/Assets/StoreLogo.png</c>，效果一样、还少一次落盘。
    /// ⚠️ TODO(win7): 仓库根 Assets 里目前**没有** StoreLogo.png（上游 1.3 才加）——
    ///    资源缺失时返回 null、卡片自动收起图标，⛔ 别让整页崩掉。
    /// </summary>
    private static Avalonia.Media.Imaging.Bitmap? LoadStoreLogo()
    {
        try
        {
            var uri = new Uri("avares://ClassSoftwareHub/Assets/StoreLogo.png");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            return stream is null ? null : new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("detail", "商店图标加载失败（资源缺失？）：" + ex.Message);
            return null;
        }
    }

    private async void Store_Click(object? sender, RoutedEventArgs e)
    {
        if (_storeUrl.Length == 0) return;

        // 微软商店本体 → 特例（能顺带把商店装回来）
        if (Services.StoreRepair.IsStoreItself(_app?.Id, _storeUrl))
        {
            await ShowStoreSelfDialogAsync();
            return;
        }

        // 其余软件：转成商店协议 → 直接拉起「微软商店」应用（别再开浏览器了）
        App.MainWindow?.OpenStore(_storeUrl);
    }

    private void Download_Click(object? sender, RoutedEventArgs e)
    {
        // 「下载」= 原样直连 GitHub 官方，不做任何加速改写（与网页端两个按钮的分工一致）。
        // 想加速就点旁边那条「加速下载」—— 它才走自建节点 / 公益镜像。
        if (sender is Button b && b.Tag is string url && url.Length > 0)
            OpenDownload(url, b, Services.DownloadRoute.Official);
    }

    /// <summary>
    /// 下载按钮分流（2026-10-18 对齐上游 1.2.0：判据统一到 <see cref="DownloadKind"/> 四分类，
    /// 不再保留「只看后缀」的第二套实现）：
    ///   · 微软商店本体 → 特例（商店没装的话商店链接是死的，得能把商店装回来）
    ///   · 商店页 → 拉起「微软商店」应用
    ///   · 文件直链 → 原生下载
    ///   · 网盘 / 官网页面 → 开在浮层里，让人自己点
    /// </summary>
    private async void OpenDownload(string url, Button? source = null,
        Services.DownloadRoute route = Services.DownloadRoute.Setting)
    {
        if (Services.StoreRepair.IsStoreItself(_app?.Id, url))
        {
            await ShowStoreSelfDialogAsync();
            return;
        }

        var item = _app?.Downloads.FirstOrDefault(d => string.Equals(d.Url, url, StringComparison.OrdinalIgnoreCase));
        var kind = item?.Kind ?? DownloadLinkKind.Of(url);

        switch (kind)
        {
            case DownloadKind.Store:
                App.MainWindow?.OpenStore(url);
                return;

            case DownloadKind.File:
                App.MainWindow?.DownloadFile(url, source is null ? null : DownloadNameFor(source), route);
                return;

            case DownloadKind.Netdisk:
                App.MainWindow?.ShowWebSheet(url, "网盘下载");
                return;
        }

        // Page：链接是官网下载页 —— 交给网页浮层，让人自己在页面上点
        App.MainWindow?.ShowWebSheet(url, "官网下载");
    }

    /// <summary>
    /// 微软商店本体专用：一是打开商店；二是「没装 / 被精简掉了」时用 wsreset -i 装回来。
    /// ⚠️ 原版对话框带 XamlRoot；FluentAvalonia 的 ContentDialog 没有 XamlRoot，直接 ShowAsync() 即可。
    /// </summary>
    private async Task ShowStoreSelfDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Microsoft Store（微软商店）",
            Content = new TextBlock
            {
                Text = "若本机未安装 Microsoft Store，商店链接将无法打开：安装过程依赖商店自身。\n\n" +
                       "· 打开微软商店：跳转到「Microsoft Store」应用\n" +
                       "· 未安装或已损坏：调用系统自带方式重新安装（约 1 至 2 分钟，期间可能出现命令行窗口，请勿关闭）",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "打开微软商店",
            SecondaryButtonText = "一键恢复商店",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                App.MainWindow?.OpenStore(_storeUrl);
                break;

            case ContentDialogResult.Secondary:
                if (Services.StoreRepair.Reinstall())
                {
                    await new ContentDialog
                    {
                        Title = "已开始安装微软商店",
                        Content = new TextBlock
                        {
                            Text = "如出现命令行窗口，请等待其自动关闭（约 1 至 2 分钟）。\n" +
                                   "安装完成后，Microsoft Store 会出现在开始菜单中，返回此处再次单击「打开微软商店」即可。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        CloseButtonText = "确定",
                    }.ShowAsync();
                }
                else
                {
                    await new ContentDialog
                    {
                        Title = "无法启动安装",
                        Content = new TextBlock
                        {
                            Text = "可在开始菜单中搜索 Microsoft Store，或用浏览器访问 " +
                                   "https://apps.microsoft.com/detail/9wzdncrfjbmp 手动安装。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        CloseButtonText = "确定",
                    }.ShowAsync();
                }
                break;
        }
    }

    /// <summary>下载文件名：优先「软件名 + 版本」，实在没有就让 DownloadService 从 URL 猜。</summary>
    private string? DownloadNameFor(Button b)
    {
        if (_app is null) return null;

        var item = b.DataContext as DownloadItem;
        var platform = item?.Platform ?? "";

        // 用软件名当文件名，扩展名从 URL 里取 —— 这样下载下来是「微信 4.0.exe」而不是一串 hash
        var name = _app.Name;
        if (!string.IsNullOrWhiteSpace(platform)) name += " " + platform;
        if (!string.IsNullOrWhiteSpace(_app.Version)) name += " " + _app.Version;
        return name.Trim();
    }

    /// <summary>校验值：点一下把完整哈希复制走，2 秒后文案还原。</summary>
    private async void CopyHash_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hash || hash.Length == 0) return;

        try
        {
            // ⚠️ WinRT 的 Clipboard.SetContent(DataPackage) → Avalonia 的
            //    TopLevel.Clipboard.SetTextAsync（IClipboard）。
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(hash);
        }
        catch
        {
            return; // 剪贴板拿不到就什么都不做，完整值在 tooltip 里还能手选
        }

        b.Content = App.Content.Ui.T("detail.hash-copied", "已复制");
        _lastCopyButton = b;

        // ⚠️ WinUI DispatcherQueueTimer → Avalonia DispatcherTimer（Tick 里自己 Stop，
        //    等价原版 IsRepeating = false）。
        _copyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyTimer.Tick -= CopyTimer_Tick;
        _copyTimer.Tick += CopyTimer_Tick;
        _copyTimer.Start();
    }

    /// <summary>
    /// 「校验」：带着这个校验值跳到「编码 / 哈希工具」的**文件模式**，用户把刚下好的安装包
    /// 拖进去就自动比对 —— 省掉"自己打开工具 → 找到哈希 → 复制 → 粘贴 → 再选文件"那一串。
    /// 走 <c>viaList: false</c>：不铺工具列表，看完返回一次就回到这张详情页。
    /// </summary>
    private void HashCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string hash || hash.Length == 0) return;

        try
        {
            App.MainWindow?.Shell.NavigateToTool(
                typeof(Tools.EncodingToolPage),
                new Tools.HashCheckRequest(hash, _app?.Name ?? ""),
                viaList: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[detail] 跳哈希校验失败: " + ex.Message);
        }
    }

    private void CopyTimer_Tick(object? sender, EventArgs e)
    {
        _copyTimer?.Stop();
        if (_lastCopyButton?.DataContext is DownloadItem item)
            _lastCopyButton.Content = item.HashChipText;
        _lastCopyButton = null;
    }

    /// <summary>
    /// 「加速下载」：**不再让用户自己挑镜像**，点了直接开下 —— 走哪条路由程序定：
    ///   ① 自建加速（本站 Worker 代签 → 自建节点限时直链，最快最稳）；
    ///   ② 自建不可用 → 并行测速 4 条公益镜像，最快的先用、其余按速度兜底；
    ///   ③ 全都不行 → GitHub 官方直链收尾（绝不把下载堵死）。
    ///
    /// 具体的挑选在 <see cref="Services.GithubRoute.ResolveAcceleratedAsync"/> 与
    /// <see cref="Services.DownloadService"/> 里做（逐条候选依次重试），界面这边只负责
    /// 把「要加速」这个意图传下去 —— 所以这里没有浮层、没有清单。
    /// </summary>
    private void Mirror_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string url || url.Length == 0) return;
        OpenDownload(url, b, Services.DownloadRoute.Accelerated);
    }

    /// <summary>
    /// 打开链接：http(s) 一律走应用内网页浮层（不再甩到浏览器），
    /// 其它协议（ms-windows-store: 等）才交给系统。
    /// </summary>
    private void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            App.MainWindow?.ShowWebSheet(url);
            return;
        }

        // ⚠️ WinRT Launcher.LaunchUriAsync → Avalonia TopLevel.Launcher.LaunchUriAsync。
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is not null) _ = top.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch { /* 打不开就算了 */ }
    }

    // 图标加载状态变了（成功 → 收掉兜底头像；失败 → 继续顶着）
    private void App_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwareApp.IconPlaceholder) && _app is not null)
        {
            IconFallback.IsVisible = _app.IconPlaceholder;
            ApplyAvatar();
        }
        // ⚠️ Avalonia 的 Bitmap 不会自己刷新 Image，图标到位后要重新赋一次 Source。
        else if (e.PropertyName == nameof(SoftwareApp.IconImage) && _app is not null)
            IconImage.Source = _app.IconImage;
    }
}
