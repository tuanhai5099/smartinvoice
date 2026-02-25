using System;
using System.Globalization;
using System.Windows.Data;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Hiển thị ký hiệu hóa đơn dạng "Mẫu số - Ký hiệu", ví dụ "1-C26TAA".
/// Nếu không có mẫu số thì chỉ hiển thị ký hiệu.
/// </summary>
public sealed class InvoiceSymbolDisplayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return string.Empty;

        // Khmshdon là ushort trong InvoiceDisplayDto
        ushort? khm = null;
        if (values[0] is ushort u)
            khm = u;
        else if (values[0] != null && ushort.TryParse(values[0].ToString(), out var parsed))
            khm = parsed;

        var kyHieu = values[1] as string ?? string.Empty;

        if (khm.HasValue && khm.Value > 0 && !string.IsNullOrWhiteSpace(kyHieu))
            return $"{khm}-{kyHieu}";

        return kyHieu;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

