using System.Globalization;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Trả về chuỗi " (mst)" nếu có giá trị, ngược lại rỗng (để ghép với tên: "Tên (MST)").</summary>
public sealed class MstToParenthesesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? "" : $" ({s})";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
