using System.Globalization;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Converts binding value (enum) to true when value equals ConverterParameter (enum name), else false.
/// Parameter: enum name string, e.g. "Month".
/// </summary>
public sealed class EnumEqualsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter != null && targetType.IsEnum)
        {
            if (Enum.TryParse(targetType, parameter.ToString(), true, out var result))
                return result;
        }
        return Binding.DoNothing;
    }
}
