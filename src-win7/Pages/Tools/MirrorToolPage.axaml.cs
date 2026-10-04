using System;
using System.Collections.Concurrent;
using System.IO;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>系统镜像下载：读内容包 text/mirror-sites.json，一行一个入口，点了交系统浏览器。</summary>
public sealed partial class MirrorToolPage : PageBase
{
    public MirrorToolPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Populate();
    }


    private void Populate()
    {
        var mirror = App.Content.Mirror;

        TitleText.Text = string.IsNullOrWhiteSpace(mirror.Title) ? "系统镜像下载" : mirror.Title;
        SubtitleText.Text = mirror.Subtitle;
        NoteText.Text = mirror.Note;

        if (!string.IsNullOrWhiteSpace(mirror.Disclaimer))
        {
            DisclaimerBar.Message = mirror.Disclaimer;
            DisclaimerBar.IsOpen = true;
        }

        Rows.ItemsSource = mirror.Sites;
        EmptyText.IsVisible = mirror.Sites.Count == 0;
    }

    private void Row_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control fe) return;
        var url = fe.Tag as string;
        if (string.IsNullOrWhiteSpace(url) && fe.DataContext is MirrorSite site) url = site.Url;
        App.MainWindow?.OpenExternal(url);
    }
}

/// <summary>
/// 站点图标转换器：MirrorSite.Icon 是内容包里的字符串路径（原 WinUI 版 Image.Source 绑 string
/// 由 x:Bind 自动转 ImageSource；Avalonia 反射绑定不做这个转换，这里补上）。
/// 解不出来就返回 null —— 模板里 Badge（名字首字）会自动顶上，界面不会开天窗。
/// </summary>
public sealed class MirrorIconConverter : IValueConverter
{
    // 解码有开销，按路径缓存（镜像站点就几条，量很小）
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        return Cache.GetOrAdd(path, Load);
    }

    private static Bitmap? Load(string path)
    {
        try
        {
            // 与原版 BitmapImage.UriSource=new Uri(icon) 同语义：绝对路径直接用，
            // 相对路径按程序运行目录解析
            var full = path;
            if (!Path.IsPathRooted(path))
            {
                full = Path.Combine(AppContext.BaseDirectory, path);
                if (!File.Exists(full)) return null;
            }
            if (!File.Exists(full)) return null;
            using var fs = File.OpenRead(full);
            return new Bitmap(fs);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
