using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Visibility when SortBy (value) equals column name (parameter), else Collapsed.
/// ConverterParameter = SortMemberPath, e.g. "KyHieu".
/// </summary>
public sealed class SortByToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter == null) return Visibility.Collapsed;
        var columnName = parameter.ToString();
        if (string.IsNullOrEmpty(columnName)) return Visibility.Collapsed;
        return value != null && string.Equals(value.ToString(), columnName, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// MultiValue: [0]=SortBy (string), [1]=SortDescending (bool). Parameter=column name.
/// Returns "▼" when this column is sorted descending, "▲" when ascending, "" otherwise.
/// </summary>
public sealed class SortArrowTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2 || parameter == null) return string.Empty;
        var columnName = parameter.ToString();
        if (string.IsNullOrEmpty(columnName)) return string.Empty;
        var sortBy = values[0]?.ToString();
        if (sortBy == null || !string.Equals(sortBy, columnName, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var desc = values[1] is true;
        return desc ? "▼" : "▲";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
