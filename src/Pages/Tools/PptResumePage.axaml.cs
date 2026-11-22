using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages.Tools;

/// <summary>
/// 课件续页的导航落点：面板是独立窗口，这里打开它，导航本身不承载设置。
/// </summary>
public sealed partial class PptResumePage : PageBase
{
    public PptResumePage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        PptResumeService.Current.OpenPanel();
    }

    private void OpenPanel_Click(object? sender, RoutedEventArgs e)
        => PptResumeService.Current.OpenPanel();
}