using Avalonia.Controls;
using Avalonia.Styling;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;
using ClassSoftwareHub.Desktop.SlideSync.Ui;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>课件续页的导航落点：控制面板直接嵌在这一页里。</summary>
public sealed partial class PptResumePage : PageBase
{
    private ControlPanel? _panel;
    private ThemeVariantScope? _scope;

    public PptResumePage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        if (_panel is not null) return;

        // 2026-11-28（用户：俩课件续页重叠）：进这一页就把 ClassIsland 里那块同款面板请它关掉，
        // 屏幕上只留一套界面（从侧栏、导航、快捷入口进来都会走这里）。
        try { CiPanelCloser.CloseDuplicatePanels(); } catch { }

        _panel = PptResumeService.Current.CreatePanel();
        if (_panel is not null)
        {
            // 面板底色偏暗时只切换这一块的主题变体，不影响主界面。
            _scope = new ThemeVariantScope { Child = _panel };
            Content = _scope;
            Core.AppLog.Step("ppt-resume", "控制面板已嵌入课件续页页面");
        }
    }

    public override void OnNavigatedFrom()
    {
        if (_panel is null) return;
        PptResumeService.Current.ReleasePanel(_panel);
        _panel = null;
        _scope = null;
        Content = null;
    }
}