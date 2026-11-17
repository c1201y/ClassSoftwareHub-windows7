using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 2 步：软件信息（自上游 dv1.2.2 移植，2026-11-14）。必填项都在这页，填完即可继续。
///
/// <para>
/// 软件 ID 随名称自动生成（中文名转拼音，见 <see cref="SoftwareSlug"/>），
/// 用户手改过之后名称再变也不覆盖 —— 这是上游 1.2.2 的行为，与旧页不同。
/// </para>
///
/// <para>
/// ⚠️ 移植说明：图标上传（OSS 直传）这一版**只保留链接输入**。
/// 上游那套要压缩、上传并回填只读地址，涉及 <see cref="OssUpload"/> 的进度回调与
/// 上传期间的控件锁；等向导整体跑通后再单独接，避免把两类问题混在一起排查。
/// </para>
/// </summary>
public sealed partial class SubmitInfoPage : UserControl, ISubmitStep
{
    private readonly SubmitDraft _draft;

    private readonly TextBox _nameBox;
    private readonly TextBox _idBox;
    private readonly TextBlock _idHint;
    private readonly ComboBox _categoryCombo;
    private readonly Border _errorBar;
    private readonly TextBlock _errorText;

    private bool _syncing;

    /// <summary>输入框的延迟绑定收集器（见 <see cref="DeferredBindings"/>）。</summary>
    private readonly DeferredBindings _bindings = new();

    public SubmitInfoPage()
    {
        _draft = SubmitSession.Current?.Draft ?? SubmitDraft.CreateEmpty();

        _nameBox = new TextBox { Watermark = "如 7-Zip", Text = _draft.Name };
        _idBox = new TextBox { Watermark = "小写字母 / 数字 / 短横线", Text = _draft.Id };
        _idHint = new TextBlock { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
        _categoryCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择分类" };

        (_errorBar, _errorText) = SubmitStartPage.MakeBar("还需补充");

        // ⚠️ 订阅必须等到控件**进入可视树**之后（2026-11-14 实测）：
        //    向导窗口是在调用 Show() 之前就把第一页建好的（见 SubmitWizardWindow 的
        //    Prepare 调用位置）。页面此时还没挂到任何可视树里，
        //    在这个状态下订阅 TextBox 的 TextChanged / GetObservable(TextProperty)，
        //    挂上去之后**不会**再触发 —— 现象就是「名字改了但 ID 一直空着」。
        //    所以改到 AttachedToVisualTree 时再订阅，并顺手把草稿里已有的值铺一次。
        // ⚠️ 订阅必须等到控件进入可视树（见 BindWhenAttached 的说明），
        //    这两只输入框与页面里其余输入框走同一个机制。
        BindWhenAttached(_nameBox, OnNameChanged);
        BindWhenAttached(_idBox, OnIdChanged);

        AttachedToVisualTree += (_, _) =>
        {
            _bindings.Flush();
            UpdateIdHint();
        };

        _categoryCombo.SelectionChanged += Category_Changed;

        BuildUi();
        BuildCategories();
        UpdateIdHint();
    }

    private void BuildUi()
    {
        var panel = new StackPanel { Width = 600, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };

        panel.Children.Add(SubmitExtraPage.Header("软件信息",
            "带 * 为必填。软件 ID 会随名称自动生成，可手动修改。"));

        panel.Children.Add(_errorBar);

        panel.Children.Add(Labelled("软件名称 *", _nameBox));

        var idGroup = new StackPanel { Spacing = 4 };
        idGroup.Children.Add(Labelled("软件 ID *", _idBox));
        idGroup.Children.Add(_idHint);
        panel.Children.Add(idGroup);

        panel.Children.Add(BuildIconSection());

        panel.Children.Add(Labelled("分类 *", _categoryCombo));

        var taglineBox = new TextBox { Watermark = "一句话简介，显示在首页卡片上", Text = _draft.Tagline };
        BindWhenAttached(taglineBox, text => { _draft.Tagline = text ?? ""; ClearError(); });
        panel.Children.Add(Labelled("一句话简介 *", taglineBox));

        var descriptionBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            Watermark = "用途与使用注意事项（可换行）",
            Text = _draft.Description,
        };
        BindWhenAttached(descriptionBox, text => { _draft.Description = text ?? ""; ClearError(); });
        panel.Children.Add(Labelled("详细介绍 *", descriptionBox));

        var contactBox = new TextBox
        {
            Watermark = "如 QQ / 邮箱 / 站内留言方式",
            Text = _draft.Contact,
        };
        BindWhenAttached(contactBox, text => { _draft.Contact = text ?? ""; ClearError(); });

        var contactGroup = Labelled("联系方式 *", contactBox);
        contactGroup.Children.Add(new TextBlock
        {
            Text = "本地加密后提交，仅管理员可解密查看",
            FontSize = 11.5,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(contactGroup);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Grid { Margin = new Thickness(24, 28, 24, 24), Children = { panel } },
        };
    }

    /// <summary>「标签 + 控件」的一竖排。</summary>
    private static StackPanel Labelled(string label, Control control)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Opacity = 0.75 });
        stack.Children.Add(control);
        return stack;
    }

    private void BuildCategories()
    {
        _categoryCombo.Items.Clear();

        try
        {
            foreach (var category in App.Content.Categories)
                _categoryCombo.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Key });
        }
        catch { /* 内容还没拉到就先空着，用户可以稍后重进 */ }

        // 恢复草稿里选过的分类
        for (var i = 0; i < _categoryCombo.ItemCount; i++)
        {
            if (_categoryCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == _draft.Category)
            {
                _categoryCombo.SelectedIndex = i;
                break;
            }
        }
    }

    // ── 图标：链接 + 上传（自上游 dv1.2.2 移植） ──────────────────────────

    private Border _iconPreview = null!;
    private TextBox _iconBox = null!;

    /// <summary>
    /// 图标区：左边一个 56×56 预览方块，右边链接输入框 + 「上传图片」按钮，
    /// 下面一行进度/状态。上传成功后回填 <c>/api/icon?k=…</c> 并把输入框锁住
    /// （键是服务端生成的，手改一个字符就失效）。
    /// </summary>
    private Control BuildIconSection()
    {
        _iconBox = new TextBox
        {
            Watermark = "https://…/icon.png",
            Text = _draft.Icon ?? "",
        };

        BindWhenAttached(_iconBox, text =>
        {
            _draft.Icon = text ?? "";
            ClearError();
            RefreshIconPreview();
        });

        _iconPreview = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 0),
        };

        var uploadButton = new Button
        {
            Content = "上传图片",
            MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var (bar, hint) = SubmitUpload.MakeProgressRow();

        var urlRow = new Grid();
        urlRow.ColumnDefinitions.Add(new ColumnDefinition());
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var urlField = new StackPanel { Spacing = 4 };
        urlField.Children.Add(new TextBlock
        {
            Text = "图标链接（选填，256×256 方图最佳）",
            FontSize = 12.5,
            Opacity = 0.75,
        });
        urlField.Children.Add(_iconBox);

        Grid.SetColumn(urlField, 0);
        Grid.SetColumn(uploadButton, 1);
        urlRow.Children.Add(urlField);
        urlRow.Children.Add(uploadButton);

        var right = new StackPanel { Spacing = 4 };
        right.Children.Add(urlRow);
        right.Children.Add(SubmitUpload.WrapProgress(bar, hint));
        right.Width = double.NaN;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_iconPreview, 0);
        Grid.SetColumn(right, 1);
        row.Children.Add(_iconPreview);
        row.Children.Add(right);

        uploadButton.Click += async (_, _) =>
        {
            var path = await SubmitUpload.PickFileAsync(
                this, "选择图标图片",
                new List<FilePickerFileType> { SubmitUpload.Filters.Images });

            if (path is null) return;

            await SubmitUpload.UploadAsync(
                this, path, OssPurpose.Icon, uploadButton, bar, hint,
                result =>
                {
                    _draft.Icon = result.Url;
                    _iconBox.Text = result.Url;
                    _iconBox.IsReadOnly = true;      // 键是服务端生成的，手改即失效
                    RefreshIconPreview();
                    ClearError();
                });
        };

        RefreshIconPreview();
        return row;
    }

    /// <summary>把输入框绑定到回调（转发到共用的延迟绑定，见 SubmitUpload.cs）。</summary>
    private void BindWhenAttached(TextBox box, Action<string?> onChanged)
        => TextBoxBinding.Bind(_bindings, box, onChanged);
    /// <summary>按当前链接刷新预览方块；没有链接或加载失败就留空。</summary>
    private void RefreshIconPreview()
    {
        try
        {
            var url = (_draft.Icon ?? "").Trim();

            if (url.Length == 0)
            {
                _iconPreview.Child = new TextBlock
                {
                    Text = "?",
                    Opacity = 0.3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                return;
            }

            // 只预览 http(s) 与本站相对地址；其余（例如本地路径）不尝试加载
            if (url.StartsWith('/')) url = ShellConfig.SiteUrl.TrimEnd('/') + url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            // 不在这里做网络加载：投稿页没必要为了预览去拉一张外网图，
            // 也可能因为证书/网络问题卡住界面。显示一个占位说明即可。
            var placeholder = new TextBlock
            {
                Text = "↗",
                Opacity = 0.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(placeholder, url);
            _iconPreview.Child = placeholder;
        }
        catch
        {
            // 预览失败不影响填写
        }
    }
    // ── 名称 → ID（自上游 1.2.2 移植：中文名转拼音） ────────────────────

    private void OnNameChanged(string? text)
    {
        if (_syncing) return;

        ClearError();
        _draft.Name = text ?? "";

        // 用户手改过 ID 之后，名称再变也不覆盖它
        if (_draft.IdEdited) return;

        // ⚠️ 整段包起来：查重要读站内清单，清单还没拉到时 TakenIds() 会抛；
        //    那种情况下退化成「不加序号」，绝不能让异常打断输入（否则 ID 永远是空的）。
        string generated;
        try
        {
            generated = SoftwareSlug.EnsureUnique(
                SoftwareSlug.FromName(text), SoftwareSlug.TakenIds());
        }
        catch
        {
            try { generated = SoftwareSlug.FromName(text); }
            catch { generated = ""; }
        }

        _syncing = true;
        _idBox.Text = generated;
        _draft.Id = generated;
        _syncing = false;

        UpdateIdHint();
    }

    private void OnIdChanged(string? text)
    {
        if (_syncing) return;

        ClearError();

        _draft.Id = (text ?? "").Trim();
        _draft.IdEdited = true;
        UpdateIdHint();
    }

    private void UpdateIdHint()
    {
        var id = (_idBox.Text ?? "").Trim();

        if (id.Length == 0)
        {
            _idHint.Text = "填写名称后自动生成，也可手动填写。";
            _idHint.Foreground = null;
            return;
        }

        if (!SoftwareSlug.IsValid(id))
        {
            _idHint.Text = "只能使用小写字母、数字与短横线，且不能以横线开头或结尾。";
            _idHint.Foreground = Brushes.OrangeRed;
            return;
        }

        if (SoftwareSlug.IsTaken(id))
        {
            _idHint.Text = "站内已有同名 ID，提交后会冲突，建议换一个。";
            _idHint.Foreground = Brushes.OrangeRed;
            return;
        }

        _idHint.Text = $"详情页地址：#/download/{id}";
        _idHint.Foreground = null;
    }

    private void Category_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;

        _draft.Category = (_categoryCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        ClearError();
    }

    /// <summary>离开可视树时断开全部订阅（窗口关掉后不该留着这些委托）。</summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _bindings.Clear();
    }
    // ── ISubmitStep ──────────────────────────────────────────────────────

    public string? Validate() => _draft.ValidateInfo();

    public void ShowError(string message)
    {
        _errorText.Text = message;
        _errorBar.IsVisible = true;
    }

    private void ClearError() => _errorBar.IsVisible = false;
}

/// <summary>就地配置控件的小工具（避免为了写一行属性再造一个局部变量）。</summary>
internal static class ControlExtensions
{
    public static T Also<T>(this T control, Action<T> configure) where T : Control
    {
        configure(control);
        return control;
    }
}

/// <summary>
/// 极简 IObserver：把「值变了」直接转成一个委托。
/// 用来订阅 Avalonia 的 <c>GetObservable(...)</c>，比手写一个观察者类省事。
/// </summary>
internal sealed class AnonymousObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;

    public AnonymousObserver(Action<T> onNext) => _onNext = onNext;

    public void OnNext(T value) => _onNext(value);

    public void OnError(Exception error) { }

    public void OnCompleted() { }
}