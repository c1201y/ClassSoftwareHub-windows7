using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 1 步：选择起点（自上游 dv1.2.2 移植，2026-11-14）。
///
/// <para>
/// 这一页不填任何必填项，只做一件事 —— 有仓库就先把能自动填的填掉。
/// 旧投稿页把「从 GitHub 读取」做成表单顶上的一条工具，用户得先意识到它存在；
/// 放到第一步之后，后面的填写量是可预期的。
/// </para>
///
/// <para>
/// ⚠️ 移植说明：上游用 <c>InfoBar</c> 报错、<c>x:Name</c> 取控件；Avalonia 这边
/// 用自绘的提示条（Border + 文字），语义一致 —— 出错时明确说缺什么，而不是把按钮灰掉。
/// </para>
/// </summary>
public sealed partial class SubmitStartPage : UserControl
{
    private readonly TextBox _repoBox;
    private readonly Button _importButton;
    private readonly CheckBox _prereleaseBox;
    private readonly CheckBox _overwriteBox;
    private readonly Border _errorBar;
    private readonly TextBlock _errorText;
    private readonly Border _okBar;
    private readonly TextBlock _okText;
    private readonly Border _report;
    private readonly StackPanel _reportHost;

    private bool _importing;

    private SubmitDraft Draft => SubmitSession.Current?.Draft ?? _fallback;
    private readonly SubmitDraft _fallback = SubmitDraft.CreateEmpty();

    public SubmitStartPage()
    {
        _repoBox = new TextBox { Watermark = "github.com/owner/repo 或 owner/repo" };

        _importButton = new Button { Content = "读取", MinWidth = 100, Classes = { "accent" } };
        _importButton.Click += Import_Click;

        _prereleaseBox = new CheckBox { Content = "优先取最新预发布版（Beta / Alpha）" };
        _overwriteBox = new CheckBox { Content = "覆盖已填写的内容" };

        (_errorBar, _errorText) = MakeBar("读取失败");
        (_okBar, _okText) = MakeBar("读取完成");

        _reportHost = new StackPanel { Spacing = 6 };
        _report = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Child = _reportHost,
        };

        BuildUi();

        var draft = Draft;
        _repoBox.Text = draft.GitHubRepo;
        _prereleaseBox.IsChecked = draft.PreferPrerelease;
    }

    private void BuildUi()
    {
        var panel = new StackPanel { Width = 600, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };

        panel.Children.Add(SubmitExtraPage.Header("从 GitHub 读取",
            "有公开仓库可以先读名称、简介与安装包，没有就点右下角「开始填写」手动填。"));

        // 仓库地址 + 读取按钮
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var repoField = new StackPanel { Spacing = 4 };
        repoField.Children.Add(new TextBlock { Text = "仓库地址", FontSize = 12.5, Opacity = 0.75 });
        repoField.Children.Add(_repoBox);

        _importButton.Margin = new Thickness(10, 0, 0, 0);
        _importButton.VerticalAlignment = VerticalAlignment.Bottom;

        Grid.SetColumn(repoField, 0);
        Grid.SetColumn(_importButton, 1);
        row.Children.Add(repoField);
        row.Children.Add(_importButton);
        panel.Children.Add(row);

        panel.Children.Add(_prereleaseBox);
        panel.Children.Add(_overwriteBox);

        var hint = new TextBlock
        {
            Text = "默认只补空白项，已填写的内容保留。",
            FontSize = 12,
            Opacity = 0.55,
            Margin = new Thickness(28, -8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(hint);

        panel.Children.Add(_errorBar);
        panel.Children.Add(_okBar);
        panel.Children.Add(_report);

        panel.Children.Add(new TextBlock
        {
            Text = "读取失败不影响手动填写；点右下角「开始填写」即可进入下一步。",
            FontSize = 12,
            Opacity = 0.55,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Grid { Margin = new Thickness(24, 28, 24, 24), Children = { panel } },
        };
    }

    /// <summary>提示条：一条 Border 里放一行文字，显示时才有高度。本向导各页共用。</summary>
    internal static (Border Bar, TextBlock Text) MakeBar(string title)
    {
        var text = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var bar = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            Child = text,
        };
        return (bar, text);
    }

    private async void Import_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_importing) return;

        _errorBar.IsVisible = false;
        _okBar.IsVisible = false;
        _report.IsVisible = false;

        var repoText = (_repoBox.Text ?? "").Trim();
        if (repoText.Length == 0)
        {
            ShowError("请填写仓库地址，例如 github.com/owner/repo。");
            return;
        }

        _importing = true;
        _importButton.IsEnabled = false;
        _importButton.Content = "正在读取";

        try
        {
            var draft = Draft;
            draft.GitHubRepo = repoText;
            draft.PreferPrerelease = _prereleaseBox.IsChecked == true;

            var result = await GithubImport.FetchAsync(repoText, _prereleaseBox.IsChecked == true, 12);
            ApplyImport(result);
        }
        catch (GithubImportException ex)
        {
            ShowError(ex.Kind switch
            {
                GithubImportErrorKind.Invalid => "无法识别该仓库地址，需使用 github.com/owner/repo 形式。",
                GithubImportErrorKind.NotFound => "未找到该仓库（可能为私有仓库或地址有误）。",
                GithubImportErrorKind.RateLimit => "GitHub 接口调用次数已达上限（未登录时每小时 60 次，按网络出口共享），可稍后重试。",
                _ => "读取失败：" + ex.Message,
            });
        }
        catch (Exception ex)
        {
            ShowError("读取失败：" + ex.Message);
        }
        finally
        {
            _importing = false;
            _importButton.IsEnabled = true;
            _importButton.Content = "读取";
        }
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorBar.IsVisible = true;
    }

    private void ApplyImport(GithubImportResult result)
    {
        var draft = Draft;
        var repo = result.Repo;
        var release = result.Release;
        var overwrite = _overwriteBox.IsChecked == true;

        var filled = new List<string>();
        var kept = new List<string>();
        var warnings = new List<string>();

        void Put(string label, string current, string? value, Action<string> assign)
        {
            var next = (value ?? "").Trim();
            if (next.Length == 0) return;

            var shown = (current ?? "").Trim();
            if (shown == next) return;

            if (shown.Length > 0 && !overwrite)
            {
                kept.Add(label);
                return;
            }

            assign(next);
            filled.Add(label);
        }

        try
        {
            Put("名称", draft.Name, repo.Repo, v => draft.Name = v);
            Put("简介", draft.Tagline, repo.Description, v => draft.Tagline = v);
            Put("官网", draft.Website, repo.Homepage, v => draft.Website = v);
            Put("GitHub", draft.Github, repo.HtmlUrl, v => draft.Github = v);
            Put("开发者", draft.Author, repo.Owner, v => draft.Author = v);
            Put("开源协议", draft.License, repo.License, v => draft.License = v);

            if (release is not null)
            {
                Put("版本号", draft.Version, release.TagName, v => draft.Version = v);
            }

            // ID 由名称自动生成（自上游 1.2.2 移植）：用户没手改过才覆盖
            if (!draft.IdEdited && draft.Name.Trim().Length > 0)
            {
                var generated = SoftwareSlug.FromName(draft.Name);
                if (generated.Length > 0 && generated != draft.Id)
                {
                    draft.Id = generated;
                    filled.Add("软件 ID");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add("部分字段未能填入：" + ex.Message);
        }

        // 报告：填了什么、保留了什么、有什么提醒
        _reportHost.Children.Clear();
        if (filled.Count > 0) _reportHost.Children.Add(ReportLine("已填入：" + string.Join("、", filled), 0.85));
        if (kept.Count > 0) _reportHost.Children.Add(ReportLine("已保留原内容：" + string.Join("、", kept), 0.7));
        foreach (var w in warnings) _reportHost.Children.Add(ReportLine(w, 0.7));

        if (_reportHost.Children.Count > 0) _report.IsVisible = true;

        _okText.Text = filled.Count > 0
            ? $"读取完成，已填入 {filled.Count} 项。"
            : "读取完成，但没有可自动填入的新内容。";
        _okBar.IsVisible = true;
    }

    private static TextBlock ReportLine(string text, double opacity) => new()
    {
        Text = text,
        FontSize = 12.5,
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
    };

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
