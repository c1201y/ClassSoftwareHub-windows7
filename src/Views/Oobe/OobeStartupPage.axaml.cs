using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 9 步：启动方式（自上游 dv1.2.0-insider1.8 移植）。
/// 只改草稿；开机自启写注册表、最小化重算命令行这些留到收尾。
/// </summary>
public partial class OobeStartupPage : UserControl
{
    private bool _loading;

    public OobeStartupPage()
    {
        InitializeComponent();

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        AutoStartSwitch.IsChecked = draft.AutoStart;
        MinimizeSwitch.IsChecked = draft.MinimizeOnStart;
        TraySwitch.IsChecked = draft.CloseToTray;
        _loading = false;

        UpdateMinimizeState();
    }

    private void AutoStart_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is null) return;

        session.Draft.AutoStart = AutoStartSwitch.IsChecked == true;
        UpdateMinimizeState();
    }

    private void Minimize_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.MinimizeOnStart = MinimizeSwitch.IsChecked == true;
    }

    private void Tray_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.CloseToTray = TraySwitch.IsChecked == true;
    }

    /// <summary>自启关掉时，「开机时最小化启动」没有意义 —— 置灰并说明当前状态。</summary>
    private void UpdateMinimizeState()
    {
        var on = AutoStartSwitch.IsChecked == true;
        MinimizeCard.IsEnabled = on;
        MinimizeHint.Opacity = on ? 0.55 : 0.4;
        MinimizeHint.Text = on
            ? "开机启动时直接收进托盘（任务栏上不留按钮），想用的时候点托盘图标。"
            : "只有打开「开机自动启动」时，这个选项才有用。";
    }
}
