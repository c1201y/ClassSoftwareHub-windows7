using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassSoftwareHub.Desktop.Core;

namespace ClassSoftwareHub.Desktop.Services.License;

/// <summary>
/// 激活流程的唯一入口：保证同一时刻只有一个对话框，并且用户放弃过的入口不再重复打扰。
///
/// <para>
/// 调用分两类：服务端回绝时（<see cref="ClientAuthHandler"/>）不带 <c>scope</c>，共用全局作用域；
/// 进入在线页面时（<see cref="Pages.ShellPage"/>）按页面 tag 给 <c>scope</c>，
/// 这样「以后再说」只关掉那一页，其它入口仍会各提示一次。
/// </para>
/// </summary>
internal static class LicenseGate
{
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    /// <summary>用户点过「以后再说」的作用域（页面 tag，或 <see cref="GlobalScope"/>）。</summary>
    private static readonly HashSet<string> Dismissed = new(StringComparer.Ordinal);

    /// <summary>不限入口的调用（服务端直接回绝）共用一个作用域。</summary>
    private const string GlobalScope = "*";

    internal static bool IsActivated => LicenseStore.IsActivated;

    /// <summary>已经拿到可用凭证（或本身是官方构建）就返回 true；否则弹窗让用户激活。</summary>
    internal static async Task<bool> EnsureAsync(string? reason = null, string? scope = null)
    {
        // 官方构建由服务端验签后自动签发令牌，本机没有凭证也不需要打扰用户
        if (LicenseStore.IsActivated || BuildIdentity.IsOfficial) return true;

        var key = string.IsNullOrEmpty(scope) ? GlobalScope : scope;
        if (IsDismissed(key)) return false;

        // 已有对话框在走（例如喊话轮询与投稿同时被拒）→ 这次直接按未授权处理，不排第二个窗
        if (!DialogGate.Wait(0)) return false;

        try
        {
            if (LicenseStore.IsActivated || BuildIdentity.IsOfficial) return true;
            if (IsDismissed(key)) return false;

            var ok = await ShowOnUiThreadAsync(reason).ConfigureAwait(false);
            if (!ok) MarkDismissed(key);
            return ok;
        }
        finally
        {
            DialogGate.Release();
        }
    }

    /// <summary>用户主动点「激活正版」时调用：清掉「已放弃」标记，让对话框能再次出现。</summary>
    internal static void ClearDismissed()
    {
        lock (Dismissed) Dismissed.Clear();
    }

    private static bool IsDismissed(string key)
    {
        lock (Dismissed) return Dismissed.Contains(key);
    }

    private static void MarkDismissed(string key)
    {
        lock (Dismissed) Dismissed.Add(key);
    }

    private static Task<bool> ShowOnUiThreadAsync(string? reason)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    tcs.TrySetResult(await ActivationDialog.ShowAsync(reason).ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    AppLog.Fail("license", ex, "激活对话框异常");
                    tcs.TrySetResult(false);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Fail("license", ex, "无法调度激活对话框");
            tcs.TrySetResult(false);
        }

        return tcs.Task;
    }
}
