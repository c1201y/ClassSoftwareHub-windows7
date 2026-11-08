namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 自检工程的最小替身：被链接的纯计算源码在异常分支里会写应用日志，
/// 自检不加载 Avalonia 与日志宿主，因此这里提供空实现，仅用于满足编译。
/// 不参与任何断言逻辑。
/// </summary>
internal static class AppLog
{
    public static void Exception(string tag, Exception ex, string what) { }
}
