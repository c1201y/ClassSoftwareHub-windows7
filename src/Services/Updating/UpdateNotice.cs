using System;

namespace ClassSoftwareHub.Desktop.Services.Updating;

/// <summary>
/// 「检测到新版本」的进程级通知（自上游 1.2.0 移植）。
///
/// 为什么需要它：更新检查若只在启动时弹一次三选一对话框（立即更新 / 后台下载 / 稍后），
/// 用户一选「稍后」，这条信息就**彻底消失了** —— 直到下次开机才会再弹一次。
/// 自启动的机器上用户很容易忘记，因此需要在首页做一个常驻的更新入口。
///
/// ⛔⛔ 这里**只存结果、不发起检查**。首页绝不能自己去联网 —— 那会拖慢首屏。
///    检查照旧由 <c>MainWindow</c> 在启动时 / 用户手动触发，拿到结果往这一放，首页订阅着看。
///
/// ⚠️ 它和「待装」是两回事，别混：
///    · 待装 ＝ **包已经下载并校验完**，存档里有 <c>UpdatePendingPath</c>，入口是首页那颗「立即安装」；
///    · 这里 ＝ **只是查到了新版本，还没下载**，入口是首页那颗「立即更新」。
///    两者同时在时**以待装为准**（它离"装上"更近一步），由首页那边决定优先级。
/// </summary>
public static class UpdateNotice
{
    private static readonly object Gate = new();
    private static UpdateRelease? _pending;

    /// <summary>最近一次检查发现的可用更新；没有就返回 null。</summary>
    public static UpdateRelease? Pending
    {
        get
        {
            lock (Gate) return _pending;
        }
    }

    /// <summary>
    /// 记下"有新版本可用"（检查拿到 <c>HasUpdate == true</c> 时调）。
    /// ⚠️ 同一个版本重复调用不会惊动订阅者 —— 否则每次启动检查都会让首页白刷一遍。
    /// </summary>
    public static void Set(UpdateRelease release)
    {
        lock (Gate)
        {
            if (_pending?.Tag == release.Tag) return;
            _pending = release;
        }

        Core.AppLog.Info("update", $"可用更新已记录｜版本={release.Tag}｜入口=首页「发现新版本」");
        Raise();
    }

    /// <summary>
    /// 清掉"有新版本"这个状态。两种情况要调：
    /// ① 检查回了"已是最新"（用户可能刚装上）；② 已经走完"下好包"进入待装流程。
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            if (_pending is null) return;
            _pending = null;
        }

        // 级别用「调试」：每次启动检查查到最新都会走一遍，写「信息」会把正常日志刷满
        Core.AppLog.Debug("update", "可用更新已清除｜入口=首页「发现新版本」");
        Raise();
    }

    /// <summary>
    /// 状态变了。
    /// ⚠️ 不保证在 UI 线程（检查跑在后台任务里）—— 订阅方自己回 UI 线程再动控件。
    /// </summary>
    public static event Action? Changed;

    private static void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Core.AppLog.Exception("update", ex, "广播可用更新状态变化");
        }
    }
}
