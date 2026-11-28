using ClassSoftwareHub.Desktop.Views;

namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 侧边栏「隐藏」时，把由它/托盘拉起、仍停留在桌面上的附属小窗一并收起。
/// 只影响显示状态，不退出程序；下次从托盘打开对应工具时仍可继续用。
/// </summary>
public static class FloatingWindowCoordinator
{
    public static void HideDetachedWindows()
    {
        Try(() => ToolPaletteWindow.HidePalette());
        Try(() => FileSearchWindow.HideWindow());
        Try(() => QuickLookupWindow.HideWindow());
        Try(() => HashCheckWindow.HideWindow());
        Try(() => ImageColorWindow.HideWindow());
        Try(() => SubjectToolsWindow.HideWindow());
        Try(() => PptMenuWindow.HideMenu());
        Try(() => TrayMenuWindow.DismissOpen());
        Try(() => VirtualKeyboardWindow.HideKeyboard());
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { Core.AppLog.Catch("floating", "收起附属悬浮窗", ex, Core.LogLevel.Debug); }
    }
}