using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Core.Dictionary;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.Services.Dictionary;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 查阅单词（自上游 dv1.2.0-insider1.8 移植）。
///
/// <para>
/// **两块独立区域**：上面「查单词」、下面「翻译句子」，各有各的输入框。
/// 不合成一个框的理由：查词是敲几个字母挑候选，翻译是粘一整段话 —— 挤在一个框里，
/// 补全列表在翻译时纯属碍事，而回车提交到底是"查"还是"翻"也得靠猜。
/// </para>
/// <para>
/// 页面只负责"把请求送出去、把结果摆出来"，词源怎么选由 <see cref="DictionaryService"/> 决定 ——
/// 这里不判断"有没有网""该不该先查本地"，否则换个源就要改界面。
/// </para>
/// <para>
/// ⚠️ 移植：上游的 <c>AutoSuggestBox</c> 换成了 <c>TextBox</c> + 内联 <c>ListBox</c> 候选。
///    候选列表就在卡片里，不弹 Popup —— Win7 关掉 DWM 合成时弹层圆角会露出白底（见窗口自绘那条经验）。
/// </para>
/// </summary>
public sealed partial class DictionaryPage : PageBase
{
    /// <summary>最近查询最多留几条。十条够回看，再多就把页面顶得没地方放结果了。</summary>
    private const int HistoryLimit = 10;

    /// <summary>补全候选最多几个。太多会盖住页面，八个一屏内够挑。</summary>
    private const int SuggestLimit = 8;

    /// <summary>空状态里摆几个示例词，点了直接查。</summary>
    private static readonly string[] Samples = { "apple", "classroom", "experiment", "光合作用" };

    private bool _wordBusy;
    private bool _transBusy;

    /// <summary>补全请求的序号。异步回来发现序号变了说明用户又敲了字，旧结果直接丢掉。</summary>
    private int _suggestToken;

    /// <summary>程序在改写输入框（点历史、点示例）时为 true：此时别再触发补全。</summary>
    private bool _settingText;

    /// <summary>最近一次真正提交查询的文本。用于挡掉"查询后延迟到达的 TextChanged"把候选列表又弹出来。</summary>
    private string _searchedText = "";

    /// <summary>用上下键移动候选时置 true：SelectionChanged 不该被当成"用户点了某个候选"。</summary>
    private bool _movingSelection;

    /// <summary>当前显示的词条，复制按钮要拿它拼文本。</summary>
    private DictEntry? _lastEntry;

    public DictionaryPage()
    {
        InitializeComponent();
        RenderHistory();
        RenderSamples();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        RenderHistory();

        // 打开这页十有八九就是要查个词：把焦点直接放进输入框，省一次点击
        WordBox.Focus();
    }

    // ═══════════════════════ 一、查单词 ═══════════════════════

    private async void WordBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_settingText) return;

        var text = WordBox.Text ?? "";
        var token = ++_suggestToken;

        // ⚠️ Avalonia 的 TextChanged 可能在属性变更之后才派发：查询时程序改写文本，等事件到达时
        //    _settingText 已经复位。不挡这一下，候选列表会在结果卡上方重新弹出来。
        if (string.Equals(text.Trim(), _searchedText, StringComparison.Ordinal))
        {
            HideSuggest();
            return;
        }

        // 中文不打补全：词库只存英文词，拿中文当前缀一个候选都出不来
        if (LexiconFormat.HasChinese(text) || text.Trim().Length == 0)
        {
            HideSuggest();
            return;
        }

        await LocalLexicon.EnsureLoadedAsync();
        if (token != _suggestToken || _settingText) return;   // 期间又敲了字，这次的结果作废

        var list = LocalLexicon.Suggest(text, SuggestLimit);
        if (list.Count == 0)
        {
            HideSuggest();
            return;
        }

        SuggestList.Items.Clear();
        foreach (var word in list) SuggestList.Items.Add(word);
        SuggestList.SelectedIndex = -1;
        SuggestList.IsVisible = true;
    }

    private void SuggestList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_movingSelection) return;
        if (SuggestList.SelectedItem is not string word) return;

        HideSuggest();
        _ = SearchWordAsync(word);
    }

    private void WordBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                var text = SuggestList.IsVisible && SuggestList.SelectedItem is string chosen
                    ? chosen
                    : WordBox.Text;
                HideSuggest();
                _ = SearchWordAsync(text);
                break;

            case Key.Down when SuggestList.IsVisible:
                e.Handled = true;
                MoveSelection(1);
                break;

            case Key.Up when SuggestList.IsVisible:
                e.Handled = true;
                MoveSelection(-1);
                break;

            case Key.Escape when SuggestList.IsVisible:
                e.Handled = true;
                HideSuggest();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = SuggestList.ItemCount;
        if (count == 0) return;

        var next = SuggestList.SelectedIndex + delta;
        if (next < 0) next = count - 1;
        if (next >= count) next = 0;

        _movingSelection = true;
        try { SuggestList.SelectedIndex = next; }
        finally { _movingSelection = false; }
    }

    private void HideSuggest()
    {
        SuggestList.IsVisible = false;
        SuggestList.Items.Clear();
    }

    private async Task SearchWordAsync(string? query)
    {
        if (_wordBusy) return;

        var text = (query ?? "").Trim();
        if (text.Length == 0) return;

        if (!string.Equals(WordBox.Text, text, StringComparison.Ordinal))
        {
            _settingText = true;
            try { WordBox.Text = text; }
            finally { _settingText = false; }
        }

        _searchedText = text;
        HideSuggest();
        _wordBusy = true;
        HideNote(WordNotice);

        try
        {
            // 明确按"单词"查：翻译有单独的区域，这里不再自动兜底翻译
            var result = await DictionaryService.LookupAsync(text, QueryModes.Word);

            if (result.Found)
            {
                RenderEntry(result.Entry!);
                Remember(text);
                ShowNote(WordNotice, result.Note, InfoBarSeverity.Informational);
            }
            else
            {
                _lastEntry = null;
                WordBlock.IsVisible = false;
                WordEmpty.IsVisible = true;
                ShowNote(WordNotice, result.Note, InfoBarSeverity.Warning, result.Error);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict", ex, "查询单词");
            ShowNote(WordNotice, "查询时出错：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _wordBusy = false;
        }
    }

    private void RenderEntry(DictEntry entry)
    {
        _lastEntry = entry;

        WordText.Text = entry.Word;
        WordSource.Text = entry.SourceLabel;
        PhoneticText.Text = entry.Phonetic;
        PhoneticText.IsVisible = entry.Phonetic.Length > 0;

        // 中译英方向没有音标、也没法念，喇叭一并收起来
        SpeakButton.IsVisible = !entry.IsZhToEn;

        SensePanel.Children.Clear();
        for (var i = 0; i < entry.Senses.Count; i++)
        {
            if (i > 0 && !entry.IsZhToEn) SensePanel.Children.Add(Separator());
            SensePanel.Children.Add(BuildSense(entry.Senses[i], entry.IsZhToEn));
        }

        ExamplePanel.Children.Clear();
        foreach (var example in entry.Examples) ExamplePanel.Children.Add(BuildExample(example));

        ExampleBlock.IsVisible = entry.Examples.Count > 0;

        WordBlock.IsVisible = true;
        WordEmpty.IsVisible = false;
    }

    private Control BuildSense(DictSense sense, bool zhToEn)
    {
        // 中译英：每个英文词都能点一下复制 —— 抄进教案时省得手动选中
        if (zhToEn)
        {
            var link = new Button
            {
                Content = sense.Text,
                FontSize = 15,
                Padding = new Thickness(0, 1, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ThemeBrush.Get(this, "AccentTextFillColorPrimaryBrush"),
            };
            link.Click += (_, _) => _ = CopyTextAsync(sense.Text, WordNotice, "已复制「" + sense.Text + "」。");
            return link;
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (sense.Pos.Length > 0)
        {
            var pos = new TextBlock
            {
                Text = sense.Pos,
                FontSize = 12.5,
                Opacity = 0.6,
                MinWidth = 30,
                Margin = new Thickness(0, 3, 8, 0)
            };
            Grid.SetColumn(pos, 0);
            row.Children.Add(pos);
        }

        var text = new SelectableTextBlock
        {
            Text = sense.Text,
            FontSize = 14,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(text, sense.Pos.Length > 0 ? 1 : 0);
        Grid.SetColumnSpan(text, sense.Pos.Length > 0 ? 1 : 2);
        row.Children.Add(text);

        return row;
    }

    private static Control BuildExample(DictExample example)
    {
        var stack = new StackPanel { Spacing = 2 };

        stack.Children.Add(new SelectableTextBlock
        {
            Text = example.En,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });

        if (example.Zh.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = example.Zh,
                FontSize = 12.5,
                Opacity = 0.62,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return stack;
    }

    private static Control Separator() => new Border
    {
        Height = 1,
        Opacity = 0.1,
        Background = Brushes.Gray,
        Margin = new Thickness(0, 2, 0, 2)
    };

    private void Speak_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastEntry is null) return;
        Pronunciation.Play(_lastEntry.Word);
    }

    private void CopyWord_Click(object? sender, RoutedEventArgs e)
        => _ = CopyTextAsync(BuildCopyText(_lastEntry), WordNotice, "已复制到剪贴板。");

    /// <summary>拼成能直接粘进教案的纯文本：词 + 音标 + 逐条释义 + 例句。</summary>
    private static string BuildCopyText(DictEntry? entry)
    {
        if (entry is null) return "";

        var sb = new StringBuilder();
        sb.Append(entry.Word);
        if (entry.Phonetic.Length > 0) sb.Append(' ').Append(entry.Phonetic);
        sb.AppendLine();

        foreach (var sense in entry.Senses)
        {
            if (sense.Pos.Length > 0) sb.Append(sense.Pos).Append(' ');
            sb.AppendLine(sense.Text);
        }

        foreach (var example in entry.Examples)
        {
            sb.AppendLine().AppendLine(example.En);
            if (example.Zh.Length > 0) sb.AppendLine(example.Zh);
        }

        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════ 二、翻译句子 ═══════════════════════

    private async void Translate_Click(object? sender, RoutedEventArgs e) => await TranslateAsync();

    private async Task TranslateAsync()
    {
        if (_transBusy) return;

        var text = (SentenceBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            ShowNote(TransNotice, "请先输入要翻译的句子。", InfoBarSeverity.Informational);
            return;
        }

        _transBusy = true;
        TranslateButton.IsEnabled = false;
        HideNote(TransNotice);

        // 先给出方向提示：翻译要几秒，先让用户知道"它认出来这是中译英"
        DirectionHint.Text = LexiconFormat.HasChinese(text) ? "中 → 英" : "英 → 中";

        try
        {
            var result = await DictionaryService.LookupAsync(text, QueryModes.Sentence);

            if (result.Translation is { IsEmpty: false })
            {
                RenderTranslation(result.Translation);
                ShowNote(TransNotice, result.Note, InfoBarSeverity.Informational);
            }
            else
            {
                TransBlock.IsVisible = false;
                ShowNote(TransNotice, result.Note, InfoBarSeverity.Warning, result.Error);
            }
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict", ex, "翻译句子");
            ShowNote(TransNotice, "翻译时出错：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _transBusy = false;
            TranslateButton.IsEnabled = true;
        }
    }

    private void RenderTranslation(TranslationResult translation)
    {
        TransDirection.Text = translation.DirectionLabel;
        TransProvider.Text = translation.Provider;
        TransTarget.Text = translation.Target;
        TransSource.Text = translation.Source;

        DirectionHint.Text = translation.DirectionLabel;
        TransBlock.IsVisible = true;
    }

    private void CopyTranslation_Click(object? sender, RoutedEventArgs e)
        => _ = CopyTextAsync(TransTarget.Text ?? "", TransNotice, "已复制译文。");

    // ═══════════════════════ 公用 ═══════════════════════

    private static void ShowNote(InfoBar bar, string note, InfoBarSeverity severity, string extra = "")
    {
        if (note.Length == 0 && extra.Length == 0) return;

        bar.Severity = severity;
        bar.Message = extra.Length > 0 ? note + "（" + extra + "）" : note;
        bar.IsVisible = true;
        bar.IsOpen = true;
    }

    private static void HideNote(InfoBar bar)
    {
        bar.IsOpen = false;
        bar.IsVisible = false;
    }

    /// <summary>写剪贴板并回一句提示。失败也不能崩：剪贴板被别的程序占着是常事。</summary>
    private async Task CopyTextAsync(string text, InfoBar bar, string message)
    {
        if (text.Length == 0) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                ShowNote(bar, "复制失败：剪贴板不可用。", InfoBarSeverity.Error);
                return;
            }

            await clipboard.SetTextAsync(text);
            ShowNote(bar, message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNote(bar, "复制失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Remember(string word)
    {
        if (word.Length == 0) return;

        var history = App.Settings.Current.DictionaryHistory;
        history.RemoveAll(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, word);

        while (history.Count > HistoryLimit) history.RemoveAt(history.Count - 1);

        App.Settings.Save();
        RenderHistory();
    }

    private void RenderHistory()
    {
        var history = App.Settings.Current.DictionaryHistory;

        HistoryPanel.Children.Clear();
        foreach (var word in history)
        {
            // 老存档里可能还有整句，一条长句能把整块撑开 —— 超过 18 个字就截断显示
            var label = word.Length > 18 ? word[..18] + "…" : word;

            var item = new Button
            {
                Content = label,
                FontSize = 13,
                Padding = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ThemeBrush.Get(this, "AccentTextFillColorPrimaryBrush"),
                Tag = word,
            };
            ToolTip.SetTip(item, word);

            var captured = word;
            item.Click += (_, _) => _ = SearchWordAsync(captured);
            HistoryPanel.Children.Add(item);
        }

        HistoryBlock.IsVisible = history.Count > 0;
    }

    /// <summary>清空最近查询。不弹确认 —— 这里存的是"查过哪些词"，丢了没有代价，多一次确认反而烦。</summary>
    private void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.Current.DictionaryHistory.Clear();
        App.Settings.Save();
        RenderHistory();
    }

    private void RenderSamples()
    {
        SamplePanel.Children.Clear();
        foreach (var word in Samples)
        {
            var chip = new Button
            {
                Content = word,
                FontSize = 12.5,
                Padding = new Thickness(12, 5, 12, 5),
                CornerRadius = new CornerRadius(14)
            };

            var captured = word;
            chip.Click += (_, _) => _ = SearchWordAsync(captured);
            SamplePanel.Children.Add(chip);
        }
    }

    // 浮层是公共控件（Controls/HubAiFlyout），与「软件下载」页同一份 —— 别在页面里再写一套动画与发送逻辑。
    private void HubAiEntry_Click(object? sender, RoutedEventArgs e) => AiFlyout.Toggle();
}
