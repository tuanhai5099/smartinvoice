using System.Globalization;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Hiển thị ngày trong DatePicker chỉ theo dd/MM/yyyy (không có thứ).
/// Convert: DateTime → "dd/MM/yyyy"; ConvertBack: chuỗi dd/MM/yyyy → DateTime.
/// </summary>
public sealed class DatePickerDisplayConverter : IValueConverter
{
    private const string Format = "dd/MM/yyyy";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return string.Empty;
        return dt.ToString(Format, Invariant);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return Binding.DoNothing;
        return DateTime.TryParseExact(s.Trim(), Format, Invariant, DateTimeStyles.None, out var result)
            ? result
            : Binding.DoNothing;
    }
}
