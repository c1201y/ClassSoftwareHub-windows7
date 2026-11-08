using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Data;
using ClassSoftwareHub.Desktop.Platform;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Pages;

/// <summary>
/// 法律条款页（用户协议 / 隐私政策 / 免责声明）。
/// 内容由 <see cref="LegalTextSource"/> 提供：在线优先 → 本地缓存 → <see cref="LegalDocs"/> 内置兜底
/// （与上游 dv1.2.0-insider1.10 的「条款不必发版」同口径；内置那份只在离线 / 首装时用）。
/// 进入时带的参数（agreement / privacy / disclaimer）决定显示哪一份。
/// 语言开关在中文 / English 之间切换（两套文案都在 LegalDocs 里备好）。
/// </summary>
public sealed partial class LegalPage : PageBase
{
    private string _key = "agreement";
    private bool _english;
    private bool _refreshed;

    public LegalPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object? parameter)
    {
        if (parameter is string key && key.Length > 0)
            _key = key;
        Render();

        // 先把手上这份画出来（进程内首次会同步读一次缓存，几 KB，不卡），
        // 再去网上静默拉一次 —— ⛔ 不能等网络，教室网离线时会白等六条线路。
        _ = RefreshInBackgroundAsync();
    }

    /// <summary>语言切换（图标键）：点一下在 中文 / English 之间切。</summary>
    private void LangButton_Click(object? sender, RoutedEventArgs e)
    {
        _english = !_english;
        Render();
    }

    private void Render()
    {
        var doc = LegalTextSource.Find(_key) ?? LegalDocs.ByKey(_key);
        var lang = _english ? doc.En : doc.Zh;
        DocTitle.Text = lang.Title;
        DocMeta.Text = $"{doc.Version} · 生效 {doc.Effective}";
        DocIntro.Text = lang.Intro;
        SectionList.ItemsSource = lang.Sections;

        // 提示字与悬浮提示始终指向"能切过去的那门语言"
        var target = _english ? "中文" : "English";
        LangHint.Text = target;
        ToolTip.SetTip(LangButton, "切换到 " + target);

        UpdateSourceBar();
    }

    /// <summary>
    /// 只有「一次都没拉到过、现在显示的是随程序内置那份」时才出这条提示。
    ///
    /// <para>
    /// 正常情况用户不需要知道正文是从站点还是缓存来的 —— 写出来像推责。
    /// 但它出现时必须说清一件事：手上这份可能已经过期，否则用户在旧条款上做的判断会失真。
    /// </para>
    /// </summary>
    private void UpdateSourceBar()
    {
        try
        {
            if (!LegalTextSource.IsBuiltIn)
            {
                SourceBar.IsOpen = false;
                SourceBar.IsVisible = false;
                return;
            }

            SourceBar.Severity = FluentAvalonia.UI.Controls.InfoBarSeverity.Informational;
            SourceBar.Message = "当前显示随程序内置的条款，可能不是最新；联网后会自行获取最新版本。";
            SourceBar.IsOpen = true;
            SourceBar.IsVisible = true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "条款来源提示刷新失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 后台拉一次最新条款；真拿到新版才重画。
    /// <para>
    /// 每次进页面只试一次（<c>_refreshed</c>）：同一进程内来回翻页不必反复打六条线路。
    /// 重画前再确认页面还挂着 —— 用户可能已经导航走了，这时改控件没有意义。
    /// </para>
    /// </summary>
    private async Task RefreshInBackgroundAsync()
    {
        // 主窗口首帧已经拉过一次（MainWindow.OnFirstActivated）→ 这次就不重复打六条线路了
        if (_refreshed || LegalTextSource.RefreshedThisRun) return;
        _refreshed = true;

        try
        {
            if (!await LegalTextSource.RefreshAsync().ConfigureAwait(false)) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (VisualRoot is not null) Render();
            });
        }
        catch (Exception ex)
        {
            AppLog.Warning("legal", "后台刷新条款失败：" + ex.Message);
        }
    }
}
