using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Data;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 第 2 步：用户协议（自上游 dv1.2.0-insider1.8 移植）。正文取自 <see cref="LegalDocs"/>（与站点同步，别手抄）；
/// 勾选前不能前进（<see cref="CanAdvance"/> 由导航壳在「下一步」时询问）。
/// </summary>
public partial class OobeAgreementPage : UserControl, IOobeStepGuard
{
    public OobeAgreementPage()
    {
        InitializeComponent();
        BuildBody();
    }

    public bool CanAdvance => AgreeCheck.IsChecked == true;

    private void BuildBody()
    {
        var doc = LegalDocs.ByKey("agreement");
        if (doc is null)
        {
            AgreementBody.Children.Add(Paragraph("条款内容加载失败，请稍后重试。", secondary: true));
            return;
        }

        var lang = doc.Zh;
        AgreementBody.Children.Add(new TextBlock
        {
            Text = $"{lang.Title}　{doc.Version} · 生效 {doc.Effective}",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (lang.Intro.Length > 0) AgreementBody.Children.Add(Paragraph(lang.Intro));

        foreach (var section in lang.Sections)
        {
            AgreementBody.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontSize = 13.5,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            });

            foreach (var item in section.Items) AgreementBody.Children.Add(Paragraph(item));
        }
    }

    private static TextBlock Paragraph(string text, bool secondary = false) => new()
    {
        // 条款源里用 **…** 标重点；纯 TextBlock 不认 Markdown，去掉星号以免正文里露出符号
        Text = text.Replace("**", ""),
        FontSize = 12.5,
        Opacity = secondary ? 0.6 : 0.8,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 19,
    };

    private void Agree_Changed(object? sender, RoutedEventArgs e)
    {
        AgreeHint.IsVisible = !CanAdvance;
        OobeSession.Current?.RefreshChrome();
    }
}
