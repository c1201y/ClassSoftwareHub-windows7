using System;
using System.Globalization;
using Avalonia.Data.Converters;
using ClassSoftwareHub.Desktop.Core;
using ClassSoftwareHub.Desktop.Services;

namespace ClassSoftwareHub.Desktop.Controls;

/// <summary>
/// 日志行「级别 → 主题画刷」转换器（自上游 dv1.1.3 的
/// <c>LogList_ContainerContentChanging</c> 移植）。
///
/// ⚠️ 为什么要有它：原版在容器复用事件里按项查表赋色，Avalonia 没有对应事件
///    （<c>ContainerContentChanging</c> 不存在），所以改成模板里绑这个转换器。
///    颜色一律走 <see cref="LogReader.BrushOf"/> → <c>ThemeBrush.Get</c>，
///    ⛔ 不要在这里自己查 <c>Application.Resources</c>（那是应用级主题，跟窗口那棵树不是一回事）。
///
/// 日志概览页（<c>LogViewerPage</c>）与详细视图（<c>LogDetailWindow</c>）共用这一个。
/// </summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LogLevel level ? LogReader.BrushOf(level, null) : LogReader.NeutralBrush(null);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
