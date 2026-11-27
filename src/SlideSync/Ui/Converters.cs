// 本文件移植自 ClassSlideSync（MIT License, Copyright (c) 2026 kkt446），原项目见 NOTICE.md。
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ClassSoftwareHub.Desktop.SlideSync;

namespace ClassSoftwareHub.Desktop.SlideSync.Ui;

/// <summary>Avalonia 的 ColorPicker 只认 Color，设置里存的是 "#AARRGGBB" 字符串。</summary>
public sealed class HexColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return Colors.White;
        string t = text!.Trim();
        if (t.Length > 0 && t[0] != '#') t = "#" + t;
        Color color;
        return Color.TryParse(t, out color) ? color : Colors.White;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color c)
        {
            // 统一写成 #AARRGGBB：Avalonia 的 Color.ToString() 在 255 时只给 6 位，
            // 而设置字段是按 8 位读的（Appearance.cs 里解析），写短了会丢 alpha。
            return "#" + ((uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B)
                .ToString("X8", CultureInfo.InvariantCulture);
        }
        return value as string ?? "#FFFFFFFF";
    }
}

/// <summary>滑条上「出厂默认值」那根短线的定位（与插件设置页同一份实现）。</summary>
public sealed class DefaultMarkConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double width = value is double d ? d : (value is float f ? f : double.NaN);
        double min, max, fallback;
        if (!DefaultMarkGeometry.TryParseParameter(parameter as string, out min, out max, out fallback))
            return new Thickness(0);
        return new Thickness(DefaultMarkGeometry.Offset(width, min, max, fallback), 0, 0, 0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}