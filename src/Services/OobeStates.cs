namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 初次配置（OOBE）的三态。存在 <see cref="AppSettings.OobeState"/> 里（int，兼容 JSON）。
///
/// <para>
/// <see cref="Unknown"/> 同时代表两件事：升级上来的老存档没有这个字段，以及"这个字段被写坏了"。
/// 两种都不该弹向导 —— 老用户已经用得很熟，再拽进一遍初次配置只是打扰。
/// </para>
/// </summary>
public static class OobeStates
{
    /// <summary>没走过 / 老存档（字段缺席）。不弹向导。</summary>
    public const int Unknown = 0;

    /// <summary>要走向导，或者上次配到一半退出了。下次启动继续问。</summary>
    public const int Pending = 1;

    /// <summary>已经走完。永远不再弹。</summary>
    public const int Done = 2;
}
