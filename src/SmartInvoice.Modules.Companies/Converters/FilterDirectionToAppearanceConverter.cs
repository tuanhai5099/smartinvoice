using System;
using System.Globalization;
using System.Windows.Data;
using SmartInvoice.Modules.Companies.ViewModels;
using Wpf.Ui.Controls;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Đổi FilterDirectionKind + parameter (MuaVao/BanRa) sang ControlAppearance của ui:Button.
/// Đúng hướng lọc  -> Primary (nền xanh), còn lại -> Secondary (xám).
/// </summary>
public sealed class FilterDirectionToAppearanceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FilterDirectionKind direction || parameter is not string target)
            return ControlAppearance.Secondary;

        return (direction == FilterDirectionKind.MuaVao && string.Equals(target, "MuaVao", StringComparison.OrdinalIgnoreCase))
               || (direction == FilterDirectionKind.BanRa && string.Equals(target, "BanRa", StringComparison.OrdinalIgnoreCase))
            ? ControlAppearance.Primary
            : ControlAppearance.Secondary;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

