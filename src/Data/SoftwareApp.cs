using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ClassSoftwareHub.Desktop.Data;

/// <summary>一条下载项（对应 app JSON 里 downloads[] 的元素）。</summary>
public sealed class DownloadItem
{
    public string Platform { get; set; } = "";
    public string Note { get; set; } = "";
    public string Size { get; set; } = "";
    public string Url { get; set; } = "";
    public string Hash { get; set; } = "";

    /// <summary>平台旁边的小字（备注 / 体积），都没填就返回空串。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Note)) parts.Add(Note);
            if (!string.IsNullOrWhiteSpace(Size)) parts.Add(Size);
            return string.Join(" · ", parts);
        }
    }

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasHash => !string.IsNullOrWhiteSpace(Hash);

    /// <summary>校验值只显示首尾几位（完整值在按钮上点击复制）。</summary>
    public string HashShort => Hash.Length > 16 ? Hash[..8] + "…" + Hash[^6..] : Hash;

    private static readonly Dictionary<int, string> HashAlgorithms = new()
    {
        [32] = "MD5",
        [40] = "SHA-1",
        [56] = "SHA-224",
        [64] = "SHA-256",
        [96] = "SHA-384",
        [128] = "SHA-512",
    };

    /// <summary>按长度推断算法名（站点同款规则）。</summary>
    public string HashAlgorithmName =>
        HashAlgorithms.TryGetValue(Hash.Length, out var name) ? name : "校验值";

    /// <summary>校验值那一行按钮上的字：算法 + 首尾 + 复制。</summary>
    public string HashChipText => HasHash ? $"{HashAlgorithmName} {HashShort}   复制" : "";

    // ⚠️ Avalonia 没有 Visibility 枚举，控件显隐一律用 bool 绑 IsVisible —— 故下面两个由
    //    Microsoft.UI.Xaml.Visibility 改为 bool（值 Visible/Collapsed → true/false）。
    public bool HashVisibility => HasHash;

    /// <summary>GitHub 链接才给「加速下载」入口。</summary>
    public bool MirrorVisibility => Core.GithubMirror.IsMirrorableUrl(Url);
}

/// <summary>一个软件（字段与站点 data/index.ts 的 PICK 白名单一致）。</summary>
public sealed class SoftwareApp : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Category { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Size { get; set; } = "";
    public string System { get; set; } = "";
    public string Website { get; set; } = "";
    public string Github { get; set; } = "";
    public string Notice { get; set; } = "";
    public string Store { get; set; } = "";
    public List<DownloadItem> Downloads { get; set; } = new();

    /// <summary>卡片副标题：有版本号显示版本，否则显示一句话简介。</summary>
    public string CardSubtitle => Version.Length > 0 ? "v" + Version : Tagline;

    /// <summary>分类显示名（载入内容后由 ContentStore 填，界面当徽标用）。</summary>
    public string CategoryDisplay { get; set; } = "";

    /// <summary>
    /// 卡片上的版本小字：数字开头才补个 v（数据里既有「3.5.2」也有「v0.9.0」「跟随官网」
    /// 「上次更新日期 2026/8/7」这类写法，不能无脑加前缀，否则会出「v上次更新日期」）。
    /// </summary>
    public string VersionText =>
        Version.Length == 0 ? "" : char.IsDigit(Version[0]) ? "v" + Version : Version;

    /// <summary>磁贴卡的一行小字：分类 · 版本 · 体积 · 系统（有哪个写哪个）。</summary>
    public string MetaText => Join(CategoryDisplay, VersionText, Size, System);

    /// <summary>紧凑卡的一行小字：版本 · 体积。</summary>
    public string CompactMeta => Join(VersionText, Size);

    private static string Join(params string[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    // ⚠️ 原版用 Microsoft.UI.Xaml.Media.ImageSource（WinUI 的抽象图像源）；
    //    Avalonia 的对应抽象类型是 Avalonia.Media.IImage（Bitmap 同时实现 IImage 与 IImageBrushSource，
    //    故界面既可绑 Image.Source 也可绑 ImageBrush.Source）。
    private IImage? _icon;
    private bool _iconResolved;
    private bool _iconLoaded;

    /// <summary>
    /// 下载图标用的共享 HttpClient（原版由 BitmapImage 内部持有连接，这里自己留一份）。
    ///
    /// ⚠️ 必须自己设超时（2026-10-02）：默认是 **100 秒**，而图标地址全是第三方 CDN
    ///    （google.cn / github / bing / 各家小站），教室里大半连不上 —— 83 个图标齐齐挂在
    ///    "正在连接"上，界面上就是一片永远不出来的占位。8 秒足够，过期就认输走头像兜底。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>同时最多下几个图标。一口气放几十个请求出去，教室那点带宽全被图标吃掉了。</summary>
    private static readonly SemaphoreSlim IconGate = new(6, 6);

    /// <summary>图标本地缓存目录（下一个存一个，以后启动直接读本地，不再碰网络）。</summary>
    private static string IconCacheDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassSoftwareHub", "icons");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>
    /// 占位字形（购物袋）的显隐：**没图 / 正在下载 / 下载失败都显示**，只有图片真的加载成功才收掉。
    /// ⚠️ 必须由属性 + 通知驱动（而不是光靠 Image 元素事件）：列表控件会回收容器，
    /// 复用到一个"没有图标"的软件时，之前被藏掉的字形不会自己回来 → 卡片就空一块。
    /// ⚠️ Avalonia 没有 Visibility 枚举，这里返回 bool 绑 IsVisible。
    /// </summary>
    public bool IconPlaceholder => !_iconLoaded;

    /// <summary>
    /// 图标没能加载出来时的兜底头像：**应用名首字 + 一个稳定的底色**。
    ///
    /// ⚠️ 为什么要有它（2026-10-02 实机反馈「下载应用界面应用没有图标」）：
    ///    icon 字段是**网上下载**的图片（见 <see cref="IconImage"/>），而地址全是第三方 CDN ——
    ///    google.cn / github / bing / 各家小站。教室里这些域名大半连不上，图标就一张都出不来；
    ///    原来的兜底只是个 35% 透明的灰购物袋字形，83 张卡片连成一片灰，
    ///    看着就是"这个应用没图标"，还完全认不出是谁。
    ///    换成彩色方块 + 首字之后：网再差，界面也是彩色的、每个应用一眼认得出。
    /// </summary>
    public string AvatarText
    {
        get
        {
            var n = (Name ?? "").Trim();
            return n.Length == 0 ? "?" : n[0].ToString();
        }
    }

    private IBrush? _avatar;

    /// <summary>头像底色：按 id 稳定取色（同一个应用每次都是同一个颜色，不会刷一次变一个）。</summary>
    public IBrush AvatarBrush => _avatar ??= MakeAvatarBrush(Id.Length > 0 ? Id : Name);

    private static readonly Color[] AvatarPalette =
    {
        Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x10, 0x7C, 0x41),
        Color.FromRgb(0xC2, 0x39, 0x34), Color.FromRgb(0x88, 0x17, 0x98),
        Color.FromRgb(0x00, 0x5B, 0x70), Color.FromRgb(0xCA, 0x50, 0x10),
        Color.FromRgb(0x4A, 0x54, 0x5E), Color.FromRgb(0x0F, 0x6C, 0xBD),
    };

    private static IBrush MakeAvatarBrush(string key)
    {
        var hash = 0;
        foreach (var c in key) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return new SolidColorBrush(AvatarPalette[hash % AvatarPalette.Length]);
    }

    /// <summary>卡片图标（http 地址就下载显示；空的话界面用占位字形兜底）。</summary>
    public IImage? IconImage
    {
        get
        {
            if (_iconResolved) return _icon;
            _iconResolved = true;
            try
            {
                if (Icon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(Icon);
                    // SVG 图标（chrome 等）：原版用 SvgImageSource 解（矢量，缩放不糊）。
                    // ⚠️ Avalonia 核心不含 SVG 解码器（需第三方包 Avalonia.Svg.Skia）——本移植版不引新包，
                    //    于是与"解不出来就一直等着"等价：占位字形自然留在那儿。
                    // TODO(win7): 日后若引入 Avalonia.Svg.Skia，这里换成 SvgImageSource 的等价物。
                    if (!uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        // ⚠️ 必须丢到线程池：LoadIconAsync 的**第一个 await 在很后面**，
                        //    前面"内置图标 / 本地缓存"两条路径是 File.OpenRead + Bitmap.DecodeToWidth
                        //    **同步**跑完的。而本 getter 是绑定在 UI 线程上调的，
                        //    直接 `_ = LoadIconAsync(uri)` 就等于把整页几十个图标的解码
                        //    全压在 UI 线程上 —— Win7 教室机首屏/刷新明显卡顿。
                        //    （方法内部的 UI 更新本来就是 Dispatcher.UIThread.Post，换线程不影响。）
                        _ = Task.Run(() => LoadIconAsync(uri));
                    }
                }
            }
            catch { /* 图标地址坏了就当没有 */ }
            return _icon;
        }
    }

    /// <summary>
    /// 后台下载 + 限宽解码图标，完成后回 UI 线程通知。**本地缓存优先**。
    /// 对应原版 <c>new BitmapImage(uri)</c>（内部异步下载）+ <c>DecodePixelWidth = 128</c>：
    /// 卡片上图标最大也就 72px（详情页），按原图解码（动不动 256/512）纯属浪费 ——
    /// 教学机 8G 内存，一页 50 多个图标就是几十 MB。这里限宽解码（等比，高度自动）。
    ///
    /// ⚠️ 2026-10-02 实机改（原话「下载应用界面应用没有图标」）：
    ///   ① **本地缓存**：下成功一次就存到 <c>%LOCALAPPDATA%\ClassSoftwareHub\icons</c>，
    ///      以后启动直接读盘。图标地址是 google.cn / github / bing / 各家小站这类第三方 CDN，
    ///      教室里大半连不上 —— 没有缓存的话每次启动都要重新赌一次网络。
    ///   ② **8 秒超时 + 只重试一次**：HttpClient 默认 100 秒，几十个请求一起挂着，
    ///      界面就是一片永远不出来的占位。
    ///   ③ **并发上限 6**：别让图标把教室那点带宽全吃光。
    ///   ④ **失败留日志**（icons.log）：下次再遇到"没图标"能直接看出是网络还是数据的问题。
    ///   失败后界面上是**彩色首字头像**（<see cref="AvatarText"/> / <see cref="AvatarBrush"/>），
    ///   不是过去那个灰扑扑的购物袋字形。
    /// </summary>
    private async Task LoadIconAsync(Uri uri)
    {
        // ⓪ **随安装包内置的图标**（2026-10-02 实机反馈「图标加载太慢，本地化」）：
        //    构建期把内容包里所有应用的图标抓下来、压到 128px、放进安装目录 ContentIcons\。
        //    命中就**完全不碰网络** —— 教室机上那些第三方 CDN（google.cn / bing / 各家小站）
        //    大半连不上，靠"下载成功一次才有缓存"救不了第一次开机。
        var bundled = BundledIconPath();
        if (bundled is not null)
        {
            try
            {
                using var fs = File.OpenRead(bundled);
                var bmp = Bitmap.DecodeToWidth(fs, 128);
                Dispatcher.UIThread.Post(() =>
                {
                    _icon = bmp;
                    _iconLoaded = true;
                    Raise(nameof(IconImage));
                    Raise(nameof(IconPlaceholder));
                });
                return;
            }
            catch { /* 内置文件坏了 → 走原有缓存 / 网络路径 */ }
        }

        var cache = IconCachePath(uri);

        // ① 本地缓存命中：直接读盘，完全不碰网络
        if (cache is not null && File.Exists(cache))
        {
            try
            {
                using var fs = File.OpenRead(cache);
                var cached = new Bitmap(fs);
                Dispatcher.UIThread.Post(() =>
                {
                    _icon = cached;
                    _iconLoaded = true;
                    Raise(nameof(IconImage));       // Avalonia 的 Bitmap 是纯数据对象，换了新对象必须通知
                    Raise(nameof(IconPlaceholder));
                });
                return;
            }
            catch
            {
                try { File.Delete(cache); } catch { }   // 缓存坏了 → 删掉，走下面的网络路径
            }
        }

        // ② 网络下载
        await IconGate.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(uri).ConfigureAwait(false);
                    var bmp = Bitmap.DecodeToWidth(new MemoryStream(bytes), 128);

                    if (cache is not null)
                    {
                        try { File.WriteAllBytes(cache, bytes); } catch { }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        _icon = bmp;
                        _iconLoaded = true;
                        Raise(nameof(IconImage));
                        Raise(nameof(IconPlaceholder));
                    });
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == 0) { await Task.Delay(400).ConfigureAwait(false); continue; }
                    LogIconFailure(uri, ex);
                }
            }
        }
        finally
        {
            IconGate.Release();
        }

        // 下载 / 解码失败：头像兜底留在那儿（与原版 ImageFailed 一致）
        Dispatcher.UIThread.Post(() =>
        {
            _iconLoaded = false;
            Raise(nameof(IconPlaceholder));
        });
    }

    /// <summary>缓存文件名：id + URL 哈希（同一应用换了图标地址会各存一份，不会读串）。</summary>
    private string? IconCachePath(Uri uri)
    {
        try
        {
            var hash = 0;
            foreach (var c in uri.AbsoluteUri) hash = (hash * 31 + c) & 0x7FFFFFFF;

            return Path.Combine(IconCacheDir, $"{SafeId()}_{hash:X8}.img");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 随安装包内置的图标路径（<c>{安装目录}\ContentIcons\&lt;id&gt;.png</c>，见 <see cref="LoadIconAsync"/> ⓪）。
    /// 没有就返回 null —— 走原来的缓存 / 网络路径。
    /// </summary>
    private string? BundledIconPath()
    {
        try
        {
            var id = SafeId();
            if (id.Length == 0) return null;
            var p = Path.Combine(AppContext.BaseDirectory, "ContentIcons", id + ".png");
            return File.Exists(p) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>id 清洗成文件名安全串（与缓存文件名同一条规则）。</summary>
    private string SafeId()
    {
        var safeId = new string((Id ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (safeId.Length == 0) safeId = "app";
        if (safeId.Length > 40) safeId = safeId[..40];
        return safeId;
    }

    /// <summary>图标下载失败留一行日志（写进 icons.log —— 诊断控制台会把整个目录实时回显出来）。</summary>
    private static void LogIconFailure(Uri uri, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClassSoftwareHub");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "icons.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] 图标下载失败 {uri.Host}: {ex.Message}\n");
        }
        catch
        {
        }
    }

    // 搜索用：名称 + 简介 + id 都参与匹配
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        var k = keyword.Trim();
        return Has(Name, k) || Has(Tagline, k) || Has(Id, k) || Has(Description, k);
    }

    private static bool Has(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>分类（对应 软件数据/categories.json）。</summary>
public sealed class DownloadCategory
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    /// <summary>界面用：分类显示名 + 该类软件数量。</summary>
    public string Display { get; set; } = "";
}

/// <summary>数据加载问题（首屏红条用；逻辑与网页端 dataLoadIssues 一致）。</summary>
public sealed class DataIssue
{
    public string File { get; set; } = "";
    public string Message { get; set; } = "";
}
