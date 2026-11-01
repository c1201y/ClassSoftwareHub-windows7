using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>第 11 步：Hub AI（自上游 dv1.2.0-insider1.8 移植）。</summary>
public partial class OobeAiPage : UserControl
{
    private bool _loading;

    public OobeAiPage()
    {
        InitializeComponent();

        _loading = true;
        var draft = OobeSession.Current!.Draft;
        AiSwitch.IsChecked = draft.HubAiEnabled;
        NameBox.Text = draft.UserDisplayName;
        _loading = false;
    }

    private void Ai_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.HubAiEnabled = AiSwitch.IsChecked == true;
    }

    private void Name_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var session = OobeSession.Current;
        if (session is not null) session.Draft.UserDisplayName = NameBox.Text ?? "";
    }
}
