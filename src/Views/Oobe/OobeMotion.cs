using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClassSoftwareHub.Desktop.Views.Oobe;

/// <summary>
/// 向导里的两段通用动效：单元素淡入、完成页卡片横滑（自上游 dv1.2.0-insider1.8 移植）。
/// 页面本体的进出场交给 <see cref="WelcomeWindow.NavigateToPage"/>，这里不再做整页上浮。
///
/// <para>
/// ⚠️ 移植：原版是 WinUI 的 <c>Storyboard</c> + 属性路径字符串；Avalonia 用
/// <see cref="Animation"/> + <see cref="Animation.RunAsync"/>。同样要注意 Avalonia 动画跑完会
/// <b>回到基准值</b>，所以每段动画结束后都要把终值写回属性（等价于原版的 FillBehavior.Stop + 手写基准值）。
/// </para>
/// </summary>
public static class OobeMotion
{
    private static bool? _animationsEnabled;

    /// <summary>系统「显示动画」开关。关掉时一切直接落到终值，别硬播。</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            if (_animationsEnabled is bool known) return known;
            try
            {
                // SPI_GETCLIENTAREAANIMATION：与 WinUI 的 UISettings.AnimationsEnabled 同一口径，
                // Win7 也认（控制面板 → 轻松使用 → 显示 → 「关闭所有不必要的动画」）。
                _animationsEnabled = SystemParametersInfo(0x1042, 0, out var enabled, 0) && enabled != 0;
            }
            catch
            {
                _animationsEnabled = true;
            }
            return _animationsEnabled.Value;
        }
    }

    /// <summary>只淡入、不位移。开场标题落位后，周围的文字用它亮出来。</summary>
    public static void Fade(Control element, int delayMs, int durationMs = 320)
    {
        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            return;
        }

        try
        {
            element.Opacity = 0;
            _ = FadeAsync(element, delayMs, durationMs);
        }
        catch
        {
            element.Opacity = 1;
        }
    }

    private static async Task FadeAsync(Control element, int delayMs, int durationMs)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            await Build(Visual.OpacityProperty, 0, 1, durationMs, new CubicEaseOut()).RunAsync(element);
        }
        catch (Exception ex)
        {
            Core.AppLog.Debug("oobe", "淡入动画失败：" + ex.Message);
        }
        finally
        {
            element.Opacity = 1;
        }
    }

    /// <summary>
    /// 两页/两张卡之间横滑换位（完成页轮播用）。前进从右侧来，后退从左侧来。
    /// ⚠️ 位移手动逐帧插值，不走 <c>Animation.RunAsync</c> —— 11.2.8 的 RunAsync 会把目标强转成
    ///    <c>Visual</c>，传 <c>TranslateTransform</c> 必抛 InvalidCastException（每翻一页一条警告）。
    /// </summary>
    public static void Swap(Control from, Control to, bool forward, double distance = 60, int durationMs = 300)
    {
        try
        {
            if (!AnimationsEnabled)
            {
                from.IsVisible = false;
                from.Opacity = 1;
                from.RenderTransform = null;
                to.IsVisible = true;
                to.Opacity = 1;
                to.RenderTransform = null;
                to.IsHitTestVisible = true;
                return;
            }

            var direction = forward ? 1 : -1;

            to.IsVisible = true;
            to.Opacity = 0;
            to.IsHitTestVisible = false;
            var toTransform = new TranslateTransform { X = distance * direction };
            to.RenderTransform = toTransform;

            // 出去那一页也先挂上 TranslateTransform：动画的基准值要在开始前就位，
            // 否则跑完回到基准值时拿到的不是我们想要的 0。
            if (from.RenderTransform is not TranslateTransform fromTransform)
            {
                fromTransform = new TranslateTransform();
                from.RenderTransform = fromTransform;
            }

            to.SetValue(Panel.ZIndexProperty, 1);
            from.SetValue(Panel.ZIndexProperty, 0);

            _ = SwapAsync(from, to, fromTransform, toTransform, distance, durationMs, direction);
        }
        catch (Exception ex)
        {
            from.IsVisible = false;
            to.IsVisible = true;
            to.Opacity = 1;
            to.RenderTransform = null;
            to.IsHitTestVisible = true;
            Core.AppLog.Warn("oobe", "换页动画失败：" + ex.Message);
        }
    }

    private static async Task SwapAsync(Control from, Control to, TranslateTransform fromTransform,
        TranslateTransform toTransform, double distance, int durationMs, int direction)
    {
        try
        {
            var inEase = new CubicEaseOut();
            var outEase = new CubicEaseIn();
            var inTotal = Math.Max(1d, durationMs);
            var outTotal = Math.Min(180d, inTotal);
            var startedAt = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                var elapsed = startedAt.Elapsed.TotalMilliseconds;
                var inProgress = Math.Min(1d, elapsed / inTotal);
                var outProgress = Math.Min(1d, elapsed / outTotal);

                toTransform.X = distance * direction * (1 - inEase.Ease(inProgress));
                to.Opacity = inEase.Ease(inProgress);
                fromTransform.X = -distance * 0.6 * direction * outEase.Ease(outProgress);
                from.Opacity = 1 - outEase.Ease(outProgress);

                if (inProgress >= 1 && outProgress >= 1) break;
                await Task.Delay(16);
            }

            to.IsHitTestVisible = true;
            to.RenderTransform = null;
            to.Opacity = 1;

            from.IsVisible = false;
            from.Opacity = 1;
            from.RenderTransform = null;
        }
        catch (Exception ex)
        {
            Core.AppLog.Warn("oobe", "换页动画执行失败：" + ex.Message);
            to.IsHitTestVisible = true;
            to.RenderTransform = null;
            to.Opacity = 1;
            from.IsVisible = false;
            from.Opacity = 1;
            from.RenderTransform = null;
        }
    }

    private static Animation Build(AvaloniaProperty property, double from, double to, int durationMs, Easing easing) => new()
    {
        Duration = TimeSpan.FromMilliseconds(durationMs),
        Easing = easing,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(property, from) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(property, to) } },
        }
    };

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);
}
