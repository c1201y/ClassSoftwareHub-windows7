using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.SlideSync;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.SlideSync.Ui;

/// <summary>
/// 设置页里那几个"颜色 / 圆角"控件需要真正的 Color、double 和"跟随还是自定义"三态，
/// 而 <see cref="SyncSettings"/> 里存的是十六进制字符串和可空整数。
/// 这个小视图模型负责翻译，并在设置变化时让绑定重新取值（PropertyChanged 传空字符串 = 全部刷新）。
/// </summary>
public sealed class SlideSyncAppearanceViewModel : INotifyPropertyChanged, IDisposable
{
    readonly SyncSettings _settings;
    readonly PptResumeService _service;

    public SlideSyncAppearanceViewModel(SyncSettings settings, PptResumeService service)
    {
        _settings = settings;
        _service = service;
        // 具名方法 + IDisposable：以前这里是匿名 lambda，宿主每开一次控制面板就多挂一个订阅，
        // 实测订阅数 0→2→3→4，vm 与整棵视觉树再也回收不掉。谁创建谁负责 Dispose。
        _settings.PropertyChanged += OnSettingsChanged;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>退订设置变更。控制面板关窗时必须调用，否则每开一次面板就漏一个订阅。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _settings.PropertyChanged -= OnSettingsChanged; } catch { }
    }

    bool _disposed;

    void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        Refresh();
    }

    /// <summary>当前实际生效的外观；服务还没起来时现算一份，设置页也不会显示成空白。</summary>
    CardAppearance Look
    {
        get { return _service != null && _service.Appearance != null ? _service.Appearance : CardAppearance.Resolve(_settings, null); }
    }

    public string ThemeSummary
    {
        get
        {
            var theme = _service != null ? _service.Theme : null;
            if (theme == null || !theme.Loaded)
            {
                return "还没读到软件的外观设置，暂时用内置配色。";
            }
            return "来自软件主题：" + theme.Describe();
        }
    }

    public string CardSummary
    {
        get
        {
            var look = Look;
            return string.Format(
                CultureInfo.CurrentCulture,
                "当前卡片：{0} × {1} 像素，不透明度 {2}%，圆角 {3}，正文字号 {4}，{5}，垂直位置 {6}%。",
                look.CardWidth, look.CardHeight, (int)Math.Round(look.BgAlpha / 255.0 * 100), look.Radius,
                look.FontBody + look.FontDelta, look.OnRight ? "贴屏幕右边" : "贴屏幕左边", look.VerticalPercent);
        }
    }

    /// <summary>插件此刻是待命还是空闲 —— 老师一眼能看出「上课前 2 分钟自动开启」有没有生效。</summary>
    public string RuntimeSummary
    {
        get
        {
            if (_service == null) return "课件续页还没启动。";
            if (_service.Armed)
            {
                return "后台正在监视放映：打开课件会弹卡片，默认 " + _settings.AutoHideSeconds
                     + " 秒后自己贴边收起；关掉课件就隐藏，记录照旧。";
            }
            return "课件续页当前没有运行。";
        }
    }

    /// <summary>0 = 贴右边，1 = 贴左边。</summary>
    public int SideIndex
    {
        get { return _settings.CardOnLeft ? 1 : 0; }
        set { _settings.CardOnLeft = value == 1; }
    }

    public Color AccentColor
    {
        get { return FromHex(_settings.AccentColor, Look.AccentR, Look.AccentG, Look.AccentB); }
        set { _settings.AccentColor = ToHex(value); }
    }

    public Color BackgroundColor
    {
        get { return FromHex(_settings.CardBackgroundColor, Look.BgR, Look.BgG, Look.BgB); }
        set { _settings.CardBackgroundColor = ToHex(value); }
    }

    public bool CustomRadius
    {
        get { return _settings.CardCornerRadius >= 0; }
        set
        {
            _settings.CardCornerRadius = value ? Math.Max(0, (int)Math.Round((double)Look.Radius)) : -1;
            Refresh();
        }
    }

    public double CornerRadius
    {
        get { return _settings.CardCornerRadius >= 0 ? _settings.CardCornerRadius : Math.Round((double)Look.Radius); }
        set
        {
            // 拖动滑块本身就是在说"我要自己定圆角"，顺手把开关扳到自定义，省得老师先找开关。
            _settings.CardCornerRadius = (int)Math.Round(Math.Clamp(value, 0, 24));
            Refresh();
        }
    }

    public void Refresh()
    {
        var handler = PropertyChanged;
        if (handler != null) handler(this, new PropertyChangedEventArgs(string.Empty));
    }

    static Color FromHex(string hex, int fr, int fg, int fb)
    {
        Color parsed;
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out parsed))
        {
            return parsed;
        }
        return Color.FromRgb((byte)fr, (byte)fg, (byte)fb);
    }

    static string ToHex(Color c)
    {
        return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
    }
}
