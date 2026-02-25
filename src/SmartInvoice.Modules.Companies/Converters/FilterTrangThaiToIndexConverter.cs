using System.Globalization;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Chuyển FilterTrangThai (short: -1,1..6) sang SelectedIndex (0..6) cho ComboBox Trạng thái hóa đơn.</summary>
public sealed class FilterTrangThaiToIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is short s)
            return s < 0 ? 0 : (s <= 6 ? (int)s : 6);
        return 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i && i >= 0 && i <= 6)
            return (short)(i == 0 ? -1 : i);
        return (short)-1;
    }
}
