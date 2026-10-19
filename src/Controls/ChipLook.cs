using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 筛选芯片的观感 —— 把 <see cref="ToggleButton"/> 画成"安静的一枚标签"。
/// (WinUI → Avalonia：ToggleButton 模板没有那组主题资源键，
///  改成挂 <c>chip-quiet</c> 样式类，由 App.axaml 里的全局样式给 on/off 两态上色。)
/// ...
/// </summary>
internal static class ChipLook
{
    /// <summary>把芯片的 on/off 画刷就地换成安静版本（实例级资源覆盖，模板会先命中这一层）。</summary>
    public static void Quiet(ToggleButton button)
    {
        void Set(string key, IBrush brush)
        {
            try { button.Resources[key] = brush; } catch { }
        }

        var none = new SolidColorBrush(Avalonia.Media.Colors.Transparent);
        var hover = ThemeBrush.Get(button, "SubtleFillColorSecondaryBrush");
        var press = ThemeBrush.Get(button, "SubtleFillColorTertiaryBrush");
        var onFill = ThemeBrush.Get(button, "ControlFillColorSecondaryBrush");
        var onEdge = ThemeBrush.Get(button, "CardStrokeColorDefaultBrush");
        var onText = ThemeBrush.Get(button, "TextFillColorPrimaryBrush");
        var onTextDim = ThemeBrush.Get(button, "TextFillColorSecondaryBrush");

        Set("ToggleButtonBackground", none);
        Set("ToggleButtonBackgroundPointerOver", hover);
        Set("ToggleButtonBackgroundPressed", press);
        Set("ToggleButtonBorderBrush", none);
        Set("ToggleButtonBorderBrushPointerOver", none);
        Set("ToggleButtonBorderBrushPressed", none);

        Set("ToggleButtonBackgroundChecked", onFill);
        Set("ToggleButtonBackgroundCheckedPointerOver", press);
        Set("ToggleButtonBackgroundCheckedPressed", press);
        Set("ToggleButtonForegroundChecked", onText);
        Set("ToggleButtonForegroundCheckedPointerOver", onText);
        Set("ToggleButtonForegroundCheckedPressed", onTextDim);
        Set("ToggleButtonBorderBrushChecked", onEdge);
        Set("ToggleButtonBorderBrushCheckedPointerOver", onEdge);
        Set("ToggleButtonBorderBrushCheckedPressed", onEdge);
    }
}
