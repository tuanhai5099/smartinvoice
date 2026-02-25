using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Converts binding value (enum) to Visible when value equals ConverterParameter (enum name), else Collapsed.
/// Parameter: enum name string, e.g. "DateRange".
/// </summary>
public sealed class EnumEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return Visibility.Collapsed;
        var paramStr = parameter.ToString();
        var valueStr = value.ToString();
        return string.Equals(valueStr, paramStr, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
