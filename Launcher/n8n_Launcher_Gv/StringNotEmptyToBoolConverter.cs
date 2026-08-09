using System.Globalization;
using System.Windows.Data;

namespace n8n_launcher_Gv;

/// <summary>
/// 将字符串转换为布尔值：非空白字符串返回 true，空或纯空白返回 false。
/// 用于路径为空时禁用对应的勾选框。
/// </summary>
public sealed class StringNotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
