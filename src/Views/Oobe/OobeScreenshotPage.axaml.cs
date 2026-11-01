using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Platform;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>第 8 步：截图（自上游 dv1.2.0-insider1.8 移植）。只改草稿，收尾时连同设置一起落盘。</summary>
public partial class OobeScreenshotPage : UserControl
{
    private readonly bool _loading = true;

    public OobeScreenshotPage()
    {
        InitializeComponent();

        ShotAutoSaveSwitch.IsChecked = OobeSession.Current!.Draft.ShotAutoSave;
        _loading = false;

        RefreshDir();
    }

    private OobeDraft? Draft => OobeSession.Current?.Draft;

    private void ShotAutoSave_Toggled(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var draft = Draft;
        if (draft is null) return;

        draft.ShotAutoSave = ShotAutoSaveSwitch.IsChecked == true;
        UpdateEnabledState();
    }

    private async void ShotDir_Change_Click(object? sender, RoutedEventArgs e)
    {
        var session = OobeSession.Current;
        if (session is null) return;

        try
        {
            // 原版 WinRT FolderPicker + InitializeWithWindow；Avalonia 走 StorageProvider。
            // ⚠️ Win7 走 FilePickerCompat 兜底（Avalonia 选择器在「库」里选位置会静默失败）。
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;

            var r = await FilePickerCompat.PickFolderAsync(top, "选择截图保存目录");
            if (r.Error is not null)
            {
                AppLog.Warn("oobe", "选截图目录被拒：" + r.Error);
                return;
            }
            if (r.Files.Count == 0) return;

            session.Draft.ShotSaveDir = r.Files[0];
            RefreshDir();
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "选择截图目录失败：" + ex.Message);
        }
    }

    private void ShotDir_Open_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = EffectiveDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("oobe", "打开截图目录失败：" + ex.Message);
        }
    }

    private void ShotDir_Reset_Click(object? sender, RoutedEventArgs e)
    {
        var draft = Draft;
        if (draft is null) return;

        draft.ShotSaveDir = "";
        RefreshDir();
    }

    /// <summary>空目录 = 桌面（与截图保存逻辑同一口径）。</summary>
    private string EffectiveDir()
    {
        var dir = (Draft?.ShotSaveDir ?? "").Trim();
        return dir.Length > 0 ? dir : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private void RefreshDir()
    {
        var dir = (Draft?.ShotSaveDir ?? "").Trim();
        ShotDirText.Text = dir.Length > 0 ? dir : "桌面（默认）";
        UpdateEnabledState();
    }

    private void UpdateEnabledState() => DirCard.IsEnabled = ShotAutoSaveSwitch.IsChecked == true;
}
