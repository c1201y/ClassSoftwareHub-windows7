using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 值转换器（2026-10-18 自上游 1.2.0 移植）。
/// ⚠️ WinUI 的 <c>IValueConverter</c>（<c>Convert(value, targetType, parameter, language)</c>）
///    在 Avalonia 里是 <see cref="IValueConverter"/>（<c>Convert(value, targetType, parameter, culture)</c>），
///    而且 Avalonia **没有 Visibility 枚举** —— 控件显隐一律用 <c>bool IsVisible</c>，
///    所以这两个转换器返回 <c>bool</c> 而不是可见性值（绑定目标就是 IsVisible）。
/// </summary>

/// <summary>值为 null → false（隐藏），否则 true（用于「有图标源才显示 Image」）。</summary>
public sealed class NullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>值非 null → false（隐藏），否则 true（用于「SVG 链接要藏起 FontIcon」）。</summary>
public sealed class NotNullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
