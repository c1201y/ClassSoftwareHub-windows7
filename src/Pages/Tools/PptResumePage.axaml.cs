using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// PPT 续播的完整页面（2026-10-31）：导入 CSES 课表、设学期起始、指定/清除各科课件。
/// 后台的「放映到第几页」由 <see cref="PptResumeService"/> 记录，这里只负责展示和手动操作。
/// </summary>
public sealed partial class PptResumePage : PageBase
{
    private bool _hooked;

    public PptResumePage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        PptResumeService.Current.EnsureStarted();
        Hook();
        Refresh();
    }

    public override void OnNavigatedFrom() => Unhook();

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        PptResumeService.Current.Changed += Refresh;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        PptResumeService.Current.Changed -= Refresh;
    }

    // ── 刷新界面 ─────────────────────────────────────────────────────────

    private void Refresh()
    {
        try
        {
            var snapshot = PptResumeService.Current.Snapshot();

            TimetableText.Text = snapshot.Timetable is null
                ? "尚未导入课表（CSES JSON）。"
                : "已导入 " + Path.GetFileName(snapshot.TimetableSource) + "："
                  + CsesSchedule.Describe(snapshot.Timetable);
            ClearTimetableButton.IsEnabled = snapshot.Timetable is not null;

            if (!SemesterStartBox.IsFocused)
                SemesterStartBox.Text = snapshot.SemesterStart;

            ParityText.Text = DescribeParity(snapshot.SemesterStart);

            LessonText.Text = snapshot.Lesson is null
                ? "当前没有排课"
                : (snapshot.Lesson.Ongoing ? "现在：" : "临近：")
                  + snapshot.Lesson.Subject + " · " + snapshot.Lesson.TimeText;
            ResumeCurrentButton.IsEnabled = snapshot.Lesson is not null;

            var live = snapshot.Live;
            LiveText.Text = live.Playing
                ? "正在放映：" + Path.GetFileName(live.File) + " · 第 " + live.Slide
                  + (live.Total > 0 ? " / " + live.Total : "") + " 页"
                  + (live.Subject.Length > 0 ? "（" + live.Subject + "）" : "（未识别科目）")
                : live.Status;

            StatusText.Text = snapshot.LastMessage.Length > 0 && snapshot.MessageAt > snapshot.LiveAt
                ? snapshot.LastMessage
                : "放映时后台每 2 秒记录一次页码；直接关掉 PPT 也不会丢。";

            BuildSubjectRows(snapshot.Subjects);
        }
        catch (Exception ex)
        {
            StatusText.Text = "刷新失败：" + ex.Message;
        }
    }

    private static string DescribeParity(string semesterStart)
    {
        if (!CsesSchedule.TryDate(semesterStart, out _))
            return "未填学期起始：单双周课表都按可用处理";

        var parity = CsesSchedule.WeekParity(semesterStart, DateTime.Now);
        return parity switch
        {
            "odd" => "本周：单周",
            "even" => "本周：双周",
            _ => "学期还没开始（按填写的日期推算）",
        };
    }

    private void BuildSubjectRows(IReadOnlyList<PptSubjectView> subjects)
    {
        SubjectList.Children.Clear();

        if (subjects.Count == 0)
        {
            SubjectList.Children.Add(new TextBlock
            {
                Text = "还没有科目。导入课表后，这里会列出所有科目。",
                FontSize = 12,
                Opacity = 0.6,
            });
            return;
        }

        foreach (var subject in subjects)
            SubjectList.Children.Add(BuildSubjectRow(subject));
    }

    private Control BuildSubjectRow(PptSubjectView subject)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,*,Auto"),
            Margin = new Thickness(0, 3, 0, 3),
        };

        var name = new TextBlock
        {
            Text = subject.Name,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(name);

        var detail = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Text = subject.HasRecord
                ? subject.FileName
                  + " · 第 " + subject.Slide + (subject.Total > 0 ? " / " + subject.Total : "") + " 页"
                  + (subject.SavedAt.Length > 0 ? " · " + subject.SavedAt : "")
                  + (subject.InTimetable ? "" : " · 不在课表中")
                : "还没有课件记录",
        };
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var resume = SmallButton("续播");
        resume.IsEnabled = subject.HasRecord;
        resume.Click += (_, _) =>
        {
            PptResumeService.Current.Resume(subject.Name);
            StatusText.Text = "正在唤起演示程序，从「" + subject.Name + "」上次的页码继续…";
        };
        buttons.Children.Add(resume);

        var pick = SmallButton("选择课件");
        pick.Click += async (_, _) =>
        {
            try { await PickForSubjectAsync(subject.Name); }
            catch (Exception ex) { StatusText.Text = "选择课件失败：" + ex.Message; }
        };
        buttons.Children.Add(pick);

        if (subject.HasRecord)
        {
            var clear = SmallButton("清除");
            clear.Click += (_, _) =>
            {
                PptResumeService.Current.RemoveRecord(subject.Name);
                Refresh();
            };
            buttons.Children.Add(clear);
        }

        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);

        return row;
    }

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        MinWidth = 0,
        Padding = new Thickness(10, 3, 10, 3),
        FontSize = 12,
    };

    // ── 操作 ─────────────────────────────────────────────────────────────

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            var filters = new List<FilePickerFileType>
            {
                new("CSES 课表（JSON）") { Patterns = new[] { "*.json", "*.cses" } },
                new("所有文件") { Patterns = new[] { "*" } },
            };

            var result = await FilePickerCompat.PickFilesAsync(top, "导入 CSES 课表", false, filters);
            if (result.Files.Count == 0)
            {
                if (!string.IsNullOrEmpty(result.Error)) StatusText.Text = result.Error;
                return;
            }

            var (ok, message) = PptResumeService.Current.ImportCses(result.Files[0]);
            StatusText.Text = message;
            if (ok) Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入课表失败：" + ex.Message;
        }
    }

    private void ClearTimetable_Click(object? sender, RoutedEventArgs e)
    {
        PptResumeService.Current.ClearTimetable();
        Refresh();
    }

    private void ApplySemester_Click(object? sender, RoutedEventArgs e)
    {
        var (_, message) = PptResumeService.Current.ApplySemesterStart(SemesterStartBox.Text ?? "");
        StatusText.Text = message;
        Refresh();
    }

    private void ResumeCurrent_Click(object? sender, RoutedEventArgs e)
    {
        PptResumeService.Current.ResumeCurrent();
        StatusText.Text = "正在唤起演示程序，从上次的页码继续…";
    }

    private void OpenWindow_Click(object? sender, RoutedEventArgs e) => Views.PptResumeWindow.ShowWindow();

    private async System.Threading.Tasks.Task PickForSubjectAsync(string subject)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var filters = new List<FilePickerFileType>
        {
            new("演示文稿") { Patterns = new[] { "*.pptx", "*.ppt", "*.ppsx", "*.pps", "*.pptm" } },
            new("所有文件") { Patterns = new[] { "*" } },
        };

        var result = await FilePickerCompat.PickFilesAsync(top, "选择「" + subject + "」的课件", false, filters);
        if (result.Files.Count == 0)
        {
            if (!string.IsNullOrEmpty(result.Error)) StatusText.Text = result.Error;
            return;
        }

        var (_, message) = PptResumeService.Current.AssignFile(subject, result.Files[0]);
        StatusText.Text = message;
        Refresh();
    }
}
