using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 投稿向导里的「上传到本站」公共动作（自上游 dv1.2.2 移植，2026-11-15）。
///
/// <para>
/// 第 2 步的图标、第 3 步的安装包都走这里。原本这两处各自写一遍选文件、上传、
/// 进度回填与错误文案，重复且容易只改一边；集中到一处后行为一致。
/// </para>
///
/// <para>
/// ⚠️ 上传协议本身在 <see cref="OssUpload"/>（与网页端同一套分片直传），这里只负责
/// 「选文件 → 传 → 把结果交给调用方 → 把失败翻成人话」，不碰 OSS 细节。
/// </para>
/// </summary>
internal static class SubmitUpload
{
    /// <summary>选一个文件；用户取消返回 null。</summary>
    public static async Task<string?> PickFileAsync(
        Visual owner, string title, IReadOnlyList<FilePickerFileType> filters)
    {
        try
        {
            var top = TopLevel.GetTopLevel(owner);
            if (top is null) return null;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = filters,
            });

            if (files.Count == 0) return null;

            var path = files[0].TryGetLocalPath();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception ex)
        {
            AppLog.Catch("submit", "选择上传文件", ex);
            return null;
        }
    }

    /// <summary>
    /// 上传一个文件，把进度与结果写进给定的控件。
    /// <paramref name="onDone"/> 在上传成功后拿到要写进数据的地址。
    /// </summary>
    public static async Task UploadAsync(
        Visual owner,
        string filePath,
        OssPurpose purpose,
        Button button,
        ProgressBar? bar,
        TextBlock hint,
        Action<OssUploadResult> onDone)
    {
        button.IsEnabled = false;
        if (bar is not null)
        {
            bar.Value = 0;
            bar.IsVisible = true;
        }

        hint.IsVisible = true;
        hint.Text = "正在上传…";

        // 让用户能中途放弃长传（2.5 GB 的包在教室网络里能传很久）
        using var cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<UploadProgress>(p =>
            {
                if (bar is not null) bar.Value = p.Fraction;
                hint.Text = $"正在上传 {HumanSize(p.Sent)} / {HumanSize(p.Total)}（{p.Fraction * 100:0}%）";
            });

            var result = await OssUpload.UploadAsync(filePath, purpose, progress, cts.Token);

            onDone(result);

            hint.Text = result.OrphanMinutes > 0
                ? $"已上传，请在 {result.OrphanMinutes} 分钟内完成提交，否则会被自动清理。"
                : "已上传。";
        }
        catch (OssUploadException ex)
        {
            hint.Text = DescribeError(ex);
            AppLog.Warning("submit", $"上传失败｜用途={purpose}｜{ex.Code}｜{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            hint.Text = "已取消上传。";
        }
        catch (Exception ex)
        {
            hint.Text = "上传失败：" + ex.Message;
            AppLog.Exception("submit", ex, "上传失败");
        }
        finally
        {
            if (bar is not null) bar.IsVisible = false;
            button.IsEnabled = true;
        }
    }

    /// <summary>上传失败的本地化说法（服务端的英文/XML 原始报错不适合直接甩给用户）。</summary>
    public static string DescribeError(OssUploadException ex) => ex.Code switch
    {
        OssUploadErrorCode.TooLarge =>
            $"文件超过上限（{HumanSize(OssUpload.MaxBytes)}），请改填官网或 GitHub Releases 直链。",
        OssUploadErrorCode.SignFailed => "站点没有接受这次上传：" + ex.Message,
        OssUploadErrorCode.Unreachable => "连不上提交服务，请检查网络后重试。",
        OssUploadErrorCode.Network => "传输中断，请检查网络后重试。",
        OssUploadErrorCode.Aborted => "已取消上传。",
        _ => "站点拒绝了这次上传：" + ex.Message,
    };

    public static string HumanSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:0.##} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => bytes + " B",
    };

    /// <summary>一行「进度条 + 状态文字」，上传时显示、平时收起。</summary>
    public static (ProgressBar Bar, TextBlock Hint) MakeProgressRow()
    {
        var bar = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 1,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var hint = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };

        return (bar, hint);
    }

    /// <summary>把「进度条 + 状态文字」塞进一个竖排容器。</summary>
    public static StackPanel WrapProgress(ProgressBar bar, TextBlock hint)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(bar);
        stack.Children.Add(hint);
        return stack;
    }

    /// <summary>常见的文件选择过滤器。</summary>
    public static class Filters
    {
        public static readonly FilePickerFileType Images = new("图片")
        {
            Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.ico" },
        };

        public static readonly FilePickerFileType AnyFile = FilePickerFileTypes.All;
    }
}

/// <summary>
/// 「进树后再订阅」的通用收集器（2026-11-15）。
///
/// <para>
/// ⚠️ 本工程的硬性约束：投稿向导窗口在 Show() 之前就把页面建好，此时页面里的控件
/// 都还没挂进任何可视树。在这个状态下订阅 TextBox 的 TextChanged 或
/// GetObservable(TextProperty)，之后控件挂进树里也**不会**再触发 ——
/// 表现是「输入框能打字，但绑定逻辑一次都没跑」。
/// </para>
/// <para>
/// 用法：建完控件后把订阅动作交给 <see cref="Add"/>，再把
/// <see cref="Flush"/> 挂到页面的 AttachedToVisualTree、<see cref="Clear"/> 挂到
/// DetachedFromVisualTree。
/// </para>
/// </summary>
internal sealed class DeferredBindings
{
    private readonly List<Func<IDisposable>> _pending = new();
    private readonly List<IDisposable> _live = new();
    private bool _flushed;

    /// <summary>登记一个「进树后再执行」的订阅动作，返回值是它的 IDisposable。</summary>
    public void Add(Func<IDisposable> bind) => _pending.Add(bind);

    /// <summary>把登记的订阅全部执行（进树时调，幂等）。</summary>
    public void Flush()
    {
        if (_flushed) return;
        _flushed = true;

        foreach (var bind in _pending)
        {
            try { _live.Add(bind()); }
            catch (Exception ex) { AppLog.Catch("submit", "绑定输入框", ex, LogLevel.Debug); }
        }

        _pending.Clear();
    }

    /// <summary>释放全部订阅并复位（离树时调）。</summary>
    public void Clear()
    {
        foreach (var sub in _live)
        {
            try { sub.Dispose(); } catch { }
        }

        _live.Clear();
        _flushed = false;
    }
}

/// <summary>
/// 把一个输入框绑定到一个回调，**等控件进树之后才真正订阅**（见 <see cref="DeferredBindings"/>）。
/// 订阅那一刻会先按当前值回调一次，保证草稿里已有的内容也同步过去。
/// </summary>
internal static class TextBoxBinding
{
    public static void Bind(DeferredBindings bag, TextBox box, Action<string?> onChanged)
    {
        bag.Add(() =>
        {
            var sub = box.GetObservable(TextBox.TextProperty)
                .Subscribe(new AnonymousObserver<string?>(onChanged));

            onChanged(box.Text);
            return sub;
        });
    }
}