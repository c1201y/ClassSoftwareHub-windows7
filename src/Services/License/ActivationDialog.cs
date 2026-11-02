using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Core;
using FluentAvalonia.UI.Controls;

namespace ClassSoftwareHub.Desktop.Services.License;

/// <summary>
/// 激活对话框：说明「当前不是正版」，收一个激活码并现场兑换。
/// 只在服务端拒绝请求（或用户主动点「激活正版」）时出现，官方构建永远走不到这里。
/// </summary>
internal static class ActivationDialog
{
    /// <summary>弹出对话框并完成一次激活。返回 true 表示已拿到可用凭证。</summary>
    internal static async Task<bool> ShowAsync(string? reason = null)
    {
        var window = App.MainWindow;
        if (window is null) return false;

        var activated = false;

        var codeBox = new TextBox
        {
            Watermark = "CSH-XXXX-XXXX-XXXX-XXXX",
            MaxLength = 40,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            IsVisible = false,
        };

        var deviceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "设备码 " + LicenseStore.DeviceCode,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                    FontSize = 12,
                    Opacity = 0.8,
                },
            },
        };

        var copy = new Button
        {
            Content = "复制",
            FontSize = 12,
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
                if (clipboard is not null) await clipboard.SetTextAsync(LicenseStore.DeviceCode);
                copy.Content = "已复制";
            }
            catch (Exception ex)
            {
                AppLog.Fail("license", ex, "复制设备码失败");
            }
        };
        deviceRow.Children.Add(copy);

        // 不想激活的出路：官方发布版由服务端自动签发凭证，无需任何操作
        var officialButton = new Button
        {
            Content = "打开官网下载官方版",
            FontSize = 12,
            Padding = new Thickness(10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        officialButton.Click += (_, _) =>
        {
            try
            {
                window.OpenExternal(ShellConfig.SiteUrl);
            }
            catch (Exception ex)
            {
                AppLog.Fail("license", ex, "打开官网失败");
            }
        };

        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(reason)
                        ? "您使用的 ClassSoftwareHub 不是正版，请输入激活码激活。"
                        : reason,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                },
                new TextBlock
                {
                    Text = "不想激活的话，可以到官网下载官方发布版：在线功能无需激活，装好即可直接使用。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                },
                officialButton,
                new TextBlock
                {
                    Text = "激活只解锁在线服务（软件投稿、反馈中心、回声洞、教室喊话、下载加速）。" +
                           "本地工具、系统修复与已有数据不受影响。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.75,
                },
                new TextBlock
                {
                    Text = "请把下列设备码发给开发者换取激活码：",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.75,
                    Margin = new Thickness(0, 4, 0, 0),
                },
                deviceRow,
                codeBox,
                error,
            },
        };

        var dialog = new ContentDialog
        {
            Title = "激活正版",
            Content = content,
            PrimaryButtonText = "激活",
            CloseButtonText = "以后再说",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // 兑换要走网络，先把关闭拦下来，成功后再主动 Hide
            args.Cancel = true;
            if (dialog.IsPrimaryButtonEnabled is false) return;

            var code = LicenseClient.Normalize(codeBox.Text);
            if (code.Length != LicenseClient.CodeLength)
            {
                error.Text = $"激活码应为 {LicenseClient.CodeLength} 个字符，请检查是否漏字符。";
                error.IsVisible = true;
                return;
            }

            dialog.IsPrimaryButtonEnabled = false;
            codeBox.IsEnabled = false;
            error.Text = "正在校验…";
            error.Foreground = new SolidColorBrush(Colors.Gray);
            error.IsVisible = true;

            try
            {
                var (ok, message) = await LicenseClient.ActivateAsync(code);
                if (ok)
                {
                    activated = true;
                    dialog.Hide();
                    return;
                }

                error.Text = message;
                error.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            }
            catch (Exception ex)
            {
                AppLog.Fail("license", ex, "激活失败");
                error.Text = "激活过程出错，请稍后重试。";
                error.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
                codeBox.IsEnabled = true;
            }
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Fail("license", ex, "激活对话框无法显示");
            return false;
        }

        return activated;
    }
}
