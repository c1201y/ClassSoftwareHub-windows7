using System;
using System.Collections.Generic;
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

namespace ClassSoftwareHub.Desktop.Views;

/// <summary>
/// 查阅单词的小浮窗（自上游 dv1.2.0-insider1.8 移植）。
///
/// <para>
/// 侧边栏「查阅单词」模块走的就是这扇窗。之所以单开一扇，是因为讲课时把整扇主窗口拉出来
/// 会盖住课件；这扇只有 480×640，能一直摆在角落。
/// </para>
/// <para>
/// 跟主页面一样分两块：上面查单词、下面翻译句子，各有各的输入框。
/// 词源调度、双向翻译、本地词库全部复用 <see cref="DictionaryService"/> —— 这里只摆界面，
/// ⛔ 不要在这扇窗里另写一套查询逻辑，否则主界面和浮窗查出来的东西会不一样。
/// </para>
/// <para>
/// 窗口边框与「文件快搜」浮窗完全同一套：<see cref="SelfDrawnFrame"/> 接管非客户区
/// （拖动 / 贴边 / 双击 / 边缘缩放仍由系统执行），关闭键自己画。
/// × / Esc = 收起来（索引/词库留在内存，下次打开秒开），真正退出由主窗口的退出链路统一关。
/// </para>
/// </summary>
public sealed partial class QuickLookupWindow : Window
{
    private const int DefaultWidthDip = 480;
    private const int DefaultHeightDip = 640;
    private const int TitleBarHeightDip = 32;

    /// <summary>补全候选最多几个。窗小，六个一屏内够挑。</summary>
    private const int SuggestLimit = 6;

    private static QuickLookupWindow? _instance;

    private SelfDrawnFrame? _frame;
    private RoundedCorners? _corners;

    private bool _shownOnce;
    private bool _visible;
    private int _fadeEpoch;

    private readonly string _backdropPrefer;

    private bool _wordBusy;
    private bool _transBusy;
    private int _suggestToken;
    private bool _settingText;
    private bool _movingSelection;

    /// <summary>最近一次真正提交查询的文本。用于挡掉"查询后延迟到达的 TextChanged"把候选列表又弹出来。</summary>
    private string _searchedText = "";

    private QuickLookupWindow()
    {
        InitializeComponent();

        var want = App.Settings.Current.Backdrop;
        _backdropPrefer = string.Equals(want, "mica", StringComparison.OrdinalIgnoreCase) ? "acrylic" : want;

        Configure();
    }

    // ── 对外 ─────────────────────────────────────────────────────────────

    /// <summary>摊开浮窗；已经开着就把它调到前面并聚焦输入框。</summary>
    public static void ShowWindow()
    {
        try
        {
            _instance ??= new QuickLookupWindow();
            _instance.BringUp();
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict", ex, "打开查阅单词窗口");
        }
    }

    public static void HideWindow() => _instance?.Collapse();

    public static void Toggle()
    {
        if (_instance is { _visible: true }) _instance.Collapse();
        else ShowWindow();
    }

    /// <summary>退出应用时真正销毁（没开过就什么都不做）。</summary>
    public static void CloseForExit()
    {
        var w = _instance;
        _instance = null;
        try { w?.Close(); } catch { }
        AppLog.Info("window", "窗口关闭请求｜窗口=查阅单词｜触发=程序化 Close");
    }

    // ── 窗口本身 ─────────────────────────────────────────────────────────

    private void Configure()
    {
        try
        {
            Title = "查阅单词";
            ShowInTaskbar = false;
            CanResize = true;
            MinWidth = 400;
            MinHeight = 420;
            Topmost = false;

            ExtendClientAreaToDecorationsHint = false;
            AttachSelfDrawnFrame();

            try { _corners = RoundedCorners.Attach(this); } catch { }

            ThemeCompat.Apply(Root);
            ClientSize = new Size(DefaultWidthDip, DefaultHeightDip);

            Closed += (_, _) => AppLog.Info("window", "窗口已关闭｜窗口=查阅单词");

            Closing += (_, args) =>
            {
                if (App.IsExiting) return;
                args.Cancel = true;
                Collapse();
            };

            // ⚠️ Window 本身没有 Loaded 事件，要挂到根元素上
            Root.Loaded += (_, _) => WordBox.Focus();
        }
        catch (Exception ex)
        {
            AppLog.Exception("dict", ex, "初始化查阅单词窗口");
        }
    }

    private void AttachSelfDrawnFrame()
    {
        try
        {
            _frame = SelfDrawnFrame.Attach(this);
            _frame.ResizeEnabled = true;
            _frame.TitleBarHeight = TitleBarHeightDip;
            _frame.InteractiveRegions = TitleBarInteractiveRegions;
            _frame.EraseColorProvider = SelfDrawnFrame.EraseColorFrom(Root);
        }
        catch (Exception ex)
        {
            _frame = null;
            AppLog.Catch("dict", "挂载自绘窗口框架（已退回原生标题栏）", ex, Core.LogLevel.Debug);
        }
    }

    private IReadOnlyList<Rect> TitleBarInteractiveRegions()
    {
        var list = new List<Rect>(1);
        AddRegion(list, CaptionCloseButton);
        return list;
    }

    private void AddRegion(List<Rect> list, Control button)
    {
        try
        {
            if (button.IsVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0)
            {
                var origin = button.TranslatePoint(new Point(0, 0), this);
                if (origin is { } p)
                    list.Add(new Rect(p.X, p.Y, button.Bounds.Width, button.Bounds.Height));
            }
        }
        catch { }
    }

    private void CaptionClose_Click(object? sender, RoutedEventArgs e) => Collapse();

    private void Root_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Collapse();
        }
    }

    // ── 显示 / 收起 ──────────────────────────────────────────────────────

    private void BringUp()
    {
        try
        {
            _fadeEpoch++;
            FlyoutFade.Prepare(Root);

            if (!_shownOnce)
            {
                _shownOnce = true;
                Show();

                ThemeCompat.Apply(Root);
                _corners?.Refresh();
                BackdropTheme.Apply(this, Root, _backdropPrefer);
                ApplyPosition();
            }
            else
            {
                var hwnd = Backdrop.TryGetHwnd(this);
                try { NativeMethods.ShowWindow(hwnd, SW_SHOW); } catch { }
                try { NativeMethods.SetForegroundWindow(hwnd); } catch { }
                Activate();
                _corners?.Refresh();
            }

            _visible = true;
            WordBox.Focus();
            FlyoutFade.In(Root, 150);
        }
        catch (Exception ex)
        {
            AppLog.Catch("dict", "显示查阅单词窗口", ex);
        }
    }

    private void Collapse()
    {
        if (!_visible) return;
        _visible = false;

        var epoch = ++_fadeEpoch;
        FlyoutFade.Out(Root, 150, () =>
        {
            if (epoch != _fadeEpoch) return;
            try { NativeMethods.ShowWindow(Backdrop.TryGetHwnd(this), SW_HIDE); }
            catch (Exception ex) { AppLog.Catch("dict", "收起查阅单词窗口", ex, Core.LogLevel.Debug); }
        });
    }

    /// <summary>第一次显示才居中：之后用户自己挪过位置就不要动它了。窗高按工作区夹一趟。</summary>
    private void ApplyPosition()
    {
        try
        {
            var work = Screens.Primary?.WorkingArea;
            if (work is null) return;

            var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;

            var maxW = work.Value.Width / scale - 40;
            var maxH = work.Value.Height / scale - 60;
            var w = Math.Clamp(DefaultWidthDip, 400, Math.Max(400, maxW));
            var h = Math.Clamp(DefaultHeightDip, 420, Math.Max(420, maxH));
            ClientSize = new Size(w, h);

            var pw = (int)Math.Round(w * scale);
            var ph = (int)Math.Round(h * scale);

            Position = new PixelPoint(
                work.Value.X + Math.Max(0, (work.Value.Width - pw) / 2),
                work.Value.Y + Math.Max(0, (work.Value.Height - ph) / 2));
        }
        catch (Exception ex)
        {
            AppLog.Catch("dict", "定位查阅单词窗口", ex, Core.LogLevel.Debug);
        }
    }

    // ── 一、查单词 ───────────────────────────────────────────────────────

    private async void WordBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        // async void 中逃逸的异常会直接终止进程，统一在入口处兜住。
        try { await WordBox_TextChangedAsync(sender, e); }
        catch (Exception ex) { AppLog.Catch("dict", "候选词更新（速查窗）", ex); }
    }

    private async Task WordBox_TextChangedAsync(object? sender, TextChangedEventArgs e)
    {
        if (_settingText) return;

        var text = WordBox.Text ?? "";
        var token = ++_suggestToken;

        // Avalonia 的 TextChanged 可能延迟派发：改写文本的事件到达时 _settingText 已复位，
        // 不挡这一下，候选列表会在结果上方重新弹出来（与主页面同一处理）。
        if (string.Equals(text.Trim(), _searchedText, StringComparison.Ordinal))
        {
            HideSuggest();
            return;
        }

        if (LexiconFormat.HasChinese(text) || text.Trim().Length == 0)
        {
            HideSuggest();
            return;
        }

        await LocalLexicon.EnsureLoadedAsync();
        if (token != _suggestToken || _settingText) return;

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
            var result = await DictionaryService.LookupAsync(text, QueryModes.Word);

            if (result.Found)
            {
                RenderWord(result.Entry!);
                ShowNote(WordNotice, result.Note, InfoBarSeverity.Informational);
            }
            else
            {
                WordBlock.IsVisible = false;
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

    private void RenderWord(DictEntry entry)
    {
        WordText.Text = entry.Word;
        PhoneticText.Text = entry.Phonetic;
        PhoneticText.IsVisible = entry.Phonetic.Length > 0;
        WordSource.Text = entry.SourceLabel;

        SensePanel.Children.Clear();
        for (var i = 0; i < entry.Senses.Count; i++)
        {
            if (i > 0 && !entry.IsZhToEn)
            {
                SensePanel.Children.Add(new Border
                {
                    Height = 1,
                    Opacity = 0.1,
                    Background = Brushes.Gray
                });
            }

            SensePanel.Children.Add(BuildSense(entry.Senses[i], entry.IsZhToEn));
        }

        WordBlock.IsVisible = true;
    }

    private static Control BuildSense(DictSense sense, bool zhToEn)
    {
        var stack = new StackPanel { Spacing = 2 };

        if (!zhToEn && sense.Pos.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = sense.Pos, FontSize = 11.5, Opacity = 0.55 });
        }

        stack.Children.Add(new SelectableTextBlock
        {
            Text = sense.Text,
            FontSize = zhToEn ? 15 : 13.5,
            TextWrapping = TextWrapping.Wrap
        });

        return stack;
    }

    // ── 二、翻译句子 ─────────────────────────────────────────────────────

    private async void Translate_Click(object? sender, RoutedEventArgs e)
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
        DirectionHint.Text = LexiconFormat.HasChinese(text) ? "中 → 英" : "英 → 中";

        try
        {
            var result = await DictionaryService.LookupAsync(text, QueryModes.Sentence);

            if (result.Translation is { IsEmpty: false })
            {
                TransDirection.Text = result.Translation.DirectionLabel;
                TransProvider.Text = result.Translation.Provider;
                TransTarget.Text = result.Translation.Target;
                TransSource.Text = result.Translation.Source;
                DirectionHint.Text = result.Translation.DirectionLabel;

                TransBlock.IsVisible = true;
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

    private async void CopyTranslation_Click(object? sender, RoutedEventArgs e)
    {
        var text = TransTarget.Text ?? "";
        if (text.Length == 0) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                ShowNote(TransNotice, "复制失败：剪贴板不可用。", InfoBarSeverity.Error);
                return;
            }

            await clipboard.SetTextAsync(text);
            ShowNote(TransNotice, "已复制译文。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowNote(TransNotice, "复制失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    // ── 公用 ─────────────────────────────────────────────────────────────

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

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
}
