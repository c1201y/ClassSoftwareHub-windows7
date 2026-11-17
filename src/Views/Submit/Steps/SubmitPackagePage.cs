using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 3 步：安装包与系统（自上游 dv1.2.2 移植，2026-11-14）。
///
/// <para>
/// 下载项一张卡默认只露「平台 + 直链」，体积、备注、下载方式、校验值收进「更多选项」。
/// ⛔ 别再把六个框一次性铺开 —— 多数投稿人一条链接就完事，先看到六个框会直接劝退。
/// </para>
///
/// <para>
/// ⚠️ 移植说明：本站上传（OSS 直传）这一版**没有接**，只支持填直链。
/// 上游那张卡上的「上传」按钮依赖 <see cref="OssUpload"/> 的分片协议与进度回填，
/// 等向导整体跑通后再单独接 —— 先保证「手动填链接」这条主路可用。
/// 体积、备注、下载方式、校验值四项沿用草稿模型，只是收在「更多选项」里。
/// </para>
/// </summary>
public sealed partial class SubmitPackagePage : UserControl, ISubmitStep
{
    private static readonly (string Key, string Name)[] Platforms =
    {
        ("windows", "Windows"),
        ("macos", "macOS"),
        ("linux", "Linux"),
        ("android", "Android"),
        ("ios", "iOS"),
        ("web", "网页版"),
    };

    private readonly SubmitDraft _draft;

    private readonly StackPanel _platformChecks = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 14,
    };

    private readonly StackPanel _downloadList = new() { Spacing = 10 };
    private readonly Border _errorBar;
    private readonly TextBlock _errorText;

    private readonly Dictionary<string, CheckBox> _platformBoxes = new();

    /// <summary>输入框的延迟绑定收集器（见 <see cref="DeferredBindings"/>）。</summary>
    private readonly DeferredBindings _bindings = new();

    public SubmitPackagePage()
    {
        _draft = SubmitSession.Current?.Draft ?? SubmitDraft.CreateEmpty();
        (_errorBar, _errorText) = SubmitStartPage.MakeBar("还需补充");

        AttachedToVisualTree += (_, _) => _bindings.Flush();

        BuildUi();
        BuildPlatformChecks();
        RebuildDownloads();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _bindings.Clear();
    }

    private void BuildUi()
    {
        var panel = new StackPanel { Width = 600, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };

        panel.Children.Add(SubmitExtraPage.Header("安装包与系统",
            "填写文件直链。单文件上限 2.5 GB。"));

        panel.Children.Add(_errorBar);

        // 适用系统
        var platformGroup = new StackPanel { Spacing = 6 };
        platformGroup.Children.Add(new TextBlock { Text = "适用系统 *", FontSize = 14 });

        platformGroup.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _platformChecks,
        });

        platformGroup.Children.Add(new TextBlock
        {
            Text = "列表页的系统筛选只认这里勾选的项。",
            FontSize = 12,
            Opacity = 0.6,
        });
        panel.Children.Add(platformGroup);

        // 版本号 / 体积
        panel.Children.Add(SubmitExtraPage.TwoColumns(
            SubmitExtraPage.Field("版本号", "如 24.09", _draft, nameof(SubmitDraft.Version), 0),
            SubmitExtraPage.Field("体积", "如 1.6 MB", _draft, nameof(SubmitDraft.Size), 1)));

        panel.Children.Add(SubmitExtraPage.Field("系统说明（选填）",
            "如 Windows 10/11（x86 / x64 / ARM64）",
            _draft, nameof(SubmitDraft.System), -1));

        // 下载项
        var dlHeader = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        dlHeader.Children.Add(new TextBlock
        {
            Text = "下载项",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        });

        var addButton = new Button { Content = "添加下载项", HorizontalAlignment = HorizontalAlignment.Left };
        addButton.Click += (_, _) => AddDownload();
        dlHeader.Children.Add(addButton);
        panel.Children.Add(dlHeader);

        panel.Children.Add(_downloadList);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Grid { Margin = new Thickness(24, 28, 24, 24), Children = { panel } },
        };
    }

    private void BuildPlatformChecks()
    {
        _platformChecks.Children.Clear();
        _platformBoxes.Clear();

        foreach (var (key, name) in Platforms)
        {
            var box = new CheckBox { Content = name, IsChecked = _draft.Platforms.Contains(key) };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true)
                {
                    if (!_draft.Platforms.Contains(key)) _draft.Platforms.Add(key);
                }
                else
                {
                    _draft.Platforms.Remove(key);
                }

                ClearError();
            };

            _platformBoxes[key] = box;
            _platformChecks.Children.Add(box);
        }
    }

    // ── 下载项卡片 ───────────────────────────────────────────────────────

    private void AddDownload()
    {
        if (_draft.Downloads.Count >= SubmitDraft.MaxDownloads)
        {
            ShowError($"最多 {SubmitDraft.MaxDownloads} 个下载项。");
            return;
        }

        _draft.Downloads.Add(new DownloadDraft());
        RebuildDownloads();
    }

    private void RemoveDownload(DownloadDraft item)
    {
        if (_draft.Downloads.Count <= 1)
        {
            ShowError("至少要保留一个下载项。");
            return;
        }

        _draft.Downloads.Remove(item);
        RebuildDownloads();
    }

    private void RebuildDownloads()
    {
        _downloadList.Children.Clear();

        // 序号按当前顺序重排，与卡片标题一致
        for (var i = 0; i < _draft.Downloads.Count; i++) _draft.Downloads[i].Number = i + 1;

        foreach (var item in _draft.Downloads) _downloadList.Children.Add(BuildCard(item));
    }

    private Control BuildCard(DownloadDraft item)
    {
        var inner = new StackPanel { Spacing = 10 };

        // 标题行：序号 + 删除
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = item.NumberText,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var remove = new Button
        {
            Content = "删除",
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        remove.Click += (_, _) => RemoveDownload(item);

        Grid.SetColumn(title, 0);
        Grid.SetColumn(remove, 1);
        head.Children.Add(title);
        head.Children.Add(remove);
        inner.Children.Add(head);

        // 平台
        var platformBox = new TextBox { Watermark = "Windows x64 安装版", Text = item.Platform };
        TextBoxBinding.Bind(_bindings, platformBox, t => { item.Platform = t ?? ""; ClearError(); });
        inner.Children.Add(Labelled("平台（如 Windows x64 安装版）", platformBox));

        // 直链 + 上传（上传协议与网页端同一套，见 SubmitUpload）
        var urlBox = new TextBox
        {
            Watermark = "https://…/setup.exe",
            Text = item.Url,
            IsReadOnly = item.IsUrlLocked,
        };
        TextBoxBinding.Bind(_bindings, urlBox, t => { item.Url = t ?? ""; ClearError(); });

        var uploadButton = new Button
        {
            Content = "上传",
            MinWidth = 90,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var (uploadBar, uploadHint) = SubmitUpload.MakeProgressRow();

        var urlRow = new Grid();
        urlRow.ColumnDefinitions.Add(new ColumnDefinition());
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var urlField = new StackPanel { Spacing = 4 };
        urlField.Children.Add(new TextBlock { Text = "下载直链 *", FontSize = 12.5, Opacity = 0.75 });
        urlField.Children.Add(urlBox);

        Grid.SetColumn(urlField, 0);
        Grid.SetColumn(uploadButton, 1);
        urlRow.Children.Add(urlField);
        urlRow.Children.Add(uploadButton);

        var urlGroup = new StackPanel { Spacing = 4 };
        urlGroup.Children.Add(urlRow);
        urlGroup.Children.Add(SubmitUpload.WrapProgress(uploadBar, uploadHint));
        inner.Children.Add(urlGroup);

        // 上传成功后回填 oss:// 键并把下载方式显式设成「文件直链」——
        // oss:// 推断不出类型，停在「自动识别」会被当成网页跳转。
        uploadButton.Click += async (_, _) =>
        {
            var path = await SubmitUpload.PickFileAsync(
                this, "选择要上传到本站的文件",
                new List<FilePickerFileType> { SubmitUpload.Filters.AnyFile });

            if (path is null) return;

            await SubmitUpload.UploadAsync(
                this, path, OssPurpose.File, uploadButton, uploadBar, uploadHint,
                result =>
                {
                    item.Url = result.Url;
                    item.Kind = "file";
                    urlBox.Text = result.Url;
                    urlBox.IsReadOnly = true;
                    RebuildDownloads();
                    ClearError();
                });
        };

        // 更多选项（默认折叠）：体积 / 备注 / 下载方式 / 校验值
        var more = new StackPanel { Spacing = 10, IsVisible = false };
        more.Children.Add(SubmitExtraPage.TwoColumns(
            FieldOf("体积", "如 1.6 MB", () => item.Size, v => item.Size = v),
            FieldOf("备注", "如 便携版", () => item.Note, v => item.Note = v)));

        var kindCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        kindCombo.Items.Add(new ComboBoxItem { Content = "自动识别", Tag = "" });
        kindCombo.Items.Add(new ComboBoxItem { Content = "文件直链", Tag = "file" });
        kindCombo.Items.Add(new ComboBoxItem { Content = "网盘", Tag = "netdisk" });
        kindCombo.Items.Add(new ComboBoxItem { Content = "应用商店", Tag = "store" });
        kindCombo.SelectedIndex = item.KindIndex;
        kindCombo.SelectionChanged += (_, _) =>
        {
            if (kindCombo.SelectedItem is ComboBoxItem ci)
            {
                item.KindIndex = kindCombo.SelectedIndex;
                _ = ci;
            }
        };
        more.Children.Add(Labelled("下载方式", kindCombo));

        var hashBox = new TextBox { Watermark = "选填，支持 MD5 / SHA-1 / SHA-256 等", Text = item.Hash };
        TextBoxBinding.Bind(_bindings, hashBox, t => { item.Hash = t ?? ""; ClearError(); });
        more.Children.Add(Labelled("校验值（选填）", hashBox));

        var toggle = new Button
        {
            Content = "更多选项",
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        toggle.Click += (_, _) =>
        {
            more.IsVisible = !more.IsVisible;
            toggle.Content = more.IsVisible ? "收起选项" : "更多选项";
        };

        inner.Children.Add(toggle);
        inner.Children.Add(more);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Child = inner,
        };
    }

    /// <summary>「标签 + 输入框」，值由 getter/setter 直连下载项（下载项不在草稿的属性表里）。</summary>
    private StackPanel FieldOf(string label, string? placeholder, Func<string> get, Action<string> set)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.75 });

        var box = new TextBox { Watermark = placeholder, Text = get() };
        TextBoxBinding.Bind(_bindings, box, set);

        stack.Children.Add(box);
        return stack;
    }

    private static StackPanel Labelled(string label, Control control)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.75 });
        stack.Children.Add(control);
        return stack;
    }

    // ── ISubmitStep ──────────────────────────────────────────────────────

    public string? Validate() => _draft.ValidatePackage();

    public void ShowError(string message)
    {
        _errorText.Text = message;
        _errorBar.IsVisible = true;
    }

    private void ClearError() => _errorBar.IsVisible = false;
}
