using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Views.Submit.Steps;

/// <summary>
/// 第 5 步：核对与提交（自上游 dv1.2.2 移植，2026-11-14）。
///
/// <para>
/// 把前面四步填的内容摊成一张清单，逐项可点回对应步骤补改；
/// 底部「提交」由向导壳调用 <see cref="SubmitAsync"/>，页面自己负责回执与结果态。
/// </para>
///
/// <para>
/// ⚠️ 移植说明：上游用 <see cref="SubmitEndpoint.Ordered"/> 逐个入口试、用
/// <c>Http.PostAsync</c> 自己发；本工程已有 <see cref="SubmitClient.PostAsync"/>
/// 做同一件事（多入口、超时、回执解析），这里直接复用，**不再另写一套请求**。
/// </para>
/// </summary>
public sealed partial class SubmitReviewPage : UserControl
{
    private readonly SubmitDraft _draft;

    private readonly StackPanel _summary = new() { Spacing = 6 };
    private readonly Border _errorBar;
    private readonly TextBlock _errorText;
    private readonly Border _resultBar;
    private readonly TextBlock _resultText;
    private readonly Button _githubFallback;

    private bool _submitting;

    public SubmitReviewPage()
    {
        _draft = SubmitSession.Current?.Draft ?? SubmitDraft.CreateEmpty();
        (_errorBar, _errorText) = SubmitStartPage.MakeBar("还需补充");
        (_resultBar, _resultText) = SubmitStartPage.MakeBar("提交结果");

        _githubFallback = new Button
        {
            Content = "改用 GitHub 网页投稿",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _githubFallback.Click += (_, _) => OpenGithubSubmit();

        BuildUi();
        RefreshSummary();
    }

    /// <summary>子项内容变了（跳回去改过）就重画一次清单。</summary>
    public void RefreshSummary()
    {
        _summary.Children.Clear();

        Section("软件信息", new (string, string)[]
        {
            ("名称", _draft.Name),
            ("软件 ID", _draft.Id),
            ("分类", _draft.Category),
            ("一句话简介", _draft.Tagline),
        }, jumpTo: 1);

        Section("下载项", _draft.Downloads
            .Where(d => d.Url.Trim().Length > 0)
            .Select(d => (
                Label: $"下载项 {d.Number}",
                Value: string.Join("  ",
                    new[] { d.Platform, d.Size, d.Url }.Where(s => !string.IsNullOrWhiteSpace(s)))))
            .ToArray(), jumpTo: 2);

        Section("补充信息", new (string, string)[]
        {
            ("官网", _draft.Website),
            ("GitHub", _draft.Github),
            ("开发者", _draft.Author),
            ("开源协议", _draft.License),
        }, jumpTo: 3);
    }

    private void Section(string title, (string Label, string Value)[] rows, int jumpTo)
    {
        var filled = rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToArray();
        if (filled.Length == 0) return;

        _summary.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 2),
        });

        foreach (var (label, value) in filled)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var l = new TextBlock { Text = label, FontSize = 13, Opacity = 0.65 };
            var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };

            Grid.SetColumn(l, 0);
            Grid.SetColumn(v, 1);
            row.Children.Add(l);
            row.Children.Add(v);

            _summary.Children.Add(row);
        }

        var edit = new Button
        {
            Content = "修改",
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        edit.Click += (_, _) => SubmitSession.Current?.JumpTo(jumpTo);
        _summary.Children.Add(edit);
    }

    private void BuildUi()
    {
        var panel = new StackPanel { Width = 600, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };

        panel.Children.Add(SubmitExtraPage.Header("核对并提交",
            "确认无误后点右下角「提交」。提交后会进入人工审核，联系方式仅管理员可见。"));

        panel.Children.Add(_errorBar);
        panel.Children.Add(_summary);
        panel.Children.Add(_resultBar);
        panel.Children.Add(_githubFallback);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Grid { Margin = new Thickness(24, 28, 24, 24), Children = { panel } },
        };
    }

    // ── 提交（由向导壳调） ───────────────────────────────────────────────

    /// <summary>
    /// 提交当前草稿。成功时通知会话进入「已完成」态（底部按钮变「关闭」）；
    /// 失败时把原因留在页面上，并给一条去 GitHub 网页投稿的退路。
    /// </summary>
    public async Task SubmitAsync()
    {
        if (_submitting) return;

        _errorBar.IsVisible = false;
        _resultBar.IsVisible = false;

        // 提交前再过一遍全量校验：前几步可能被跳回来改坏了
        var problem = _draft.ValidateAll();
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        _submitting = true;
        ShowResult("正在提交…");

        try
        {
            var payload = _draft.BuildPayload();
            var json = JsonSerializer.Serialize(payload);

            var reply = await Services.SubmitClient.PostAsync(
                "/api/submit", json, "已提交，等待审核。");

            if (reply is null)
            {
                ShowResult("提交失败：所有接口都连不上，请检查网络后重试。");
                _githubFallback.IsVisible = true;
                return;
            }

            if (!reply.Ok)
            {
                ShowResult("提交失败：" + reply.Message);
                _githubFallback.IsVisible = true;
                return;
            }

            ShowResult(reply.Message.Length > 0 ? reply.Message : "已提交，等待审核。");
            _githubFallback.IsVisible = false;

            // 成功：进入「已完成」态，底部按钮变「关闭」，不再允许往前翻
            SubmitSession.Current?.MarkCompleted();
        }
        catch (Exception ex)
        {
            AppLog.Exception("submit", ex, "投稿向导提交");
            ShowResult("提交失败：" + ex.Message);
            _githubFallback.IsVisible = true;
        }
        finally
        {
            _submitting = false;
        }
    }

    private void ShowResult(string text)
    {
        _resultText.Text = text;
        _resultBar.IsVisible = true;
    }

    public void ShowError(string message)
    {
        _errorText.Text = message;
        _errorBar.IsVisible = true;
    }

    private void OpenGithubSubmit()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/c1201y/ClassSoftwareHub/issues/new")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Catch("submit", "打开 GitHub 投稿页", ex);
        }
    }
}
