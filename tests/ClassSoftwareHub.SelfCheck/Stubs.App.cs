namespace ClassSoftwareHub.Desktop;

/// <summary>
/// 自检工程的最小替身：<c>SoftwareSlug.TakenIds</c> 会读站内清单，
/// 自检不加载内容存储，这里给一个空清单即可（查重退化成「不重名」）。
/// </summary>
internal static class App
{
    public static ContentStub Content { get; } = new();
}

internal sealed class ContentStub
{
    public System.Collections.Generic.List<AppEntryStub> Apps { get; } = new();
}

internal sealed class AppEntryStub
{
    public string Id { get; set; } = "";
}
