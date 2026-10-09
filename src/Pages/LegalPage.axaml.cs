using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 法律条款页（用户协议 / 隐私政策 / 免责声明）。
/// 内容取自 <see cref="LegalDocs"/>；进入时带的参数（agreement / privacy / disclaimer）决定显示哪一份。
/// 语言开关在中文 / English 之间切换（两套文案都在 LegalDocs 里备好）。
/// </summary>
public sealed partial class LegalPage : PageBase
{
    private string _key = "agreement";
    private bool _english;

    public LegalPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        if (parameter is string key && key.Length > 0)
            _key = key;
        Render();
    }

    private void LangSwitch_Toggled(object? sender, RoutedEventArgs e)
    {
        _english = LangSwitch.IsChecked == true;
        Render();
    }

    private void Render()
    {
        var doc = LegalDocs.ByKey(_key);
        var lang = _english ? doc.En : doc.Zh;
        DocTitle.Text = lang.Title;
        DocMeta.Text = $"{doc.Version} · 生效 {doc.Effective}";
        DocIntro.Text = lang.Intro;
        SectionList.ItemsSource = lang.Sections;
    }
}
