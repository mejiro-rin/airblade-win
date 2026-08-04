using Microsoft.UI.Xaml.Data;

namespace AirBlade.Converters;

/// <summary>
/// 把布尔值转换为“开/关”文字，用于 Windows Terminal 风格开关左侧的标签。
/// </summary>
public sealed class BoolToOnOffTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "开" : "关";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
