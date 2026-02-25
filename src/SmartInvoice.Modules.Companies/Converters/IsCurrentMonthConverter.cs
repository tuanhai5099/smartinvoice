using System.Globalization;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Dùng cho Style CalendarDayButton: so sánh ngày của nút (value[0]) với tháng đang hiển thị (value[1]).
/// Trả về true nếu ngày thuộc đúng tháng đang xem → không làm mờ; false → ngày tháng khác, làm mờ.
/// </summary>
public sealed class IsCurrentMonthConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        var day = values[0] as DateTime?;
        var display = values[1] as DateTime?;
        if (!day.HasValue || !display.HasValue) return false;
        return day.Value.Year == display.Value.Year && day.Value.Month == display.Value.Month;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
