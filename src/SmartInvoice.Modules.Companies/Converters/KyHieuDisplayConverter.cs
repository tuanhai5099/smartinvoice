using System.Globalization;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Ghép Mẫu số (MaHoaDon) và Ký hiệu (KyHieu) thành dạng "Mẫu số - Ký hiệu" hoặc chỉ "Ký hiệu" nếu không có mẫu số.</summary>
public sealed class KyHieuDisplayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var mauSo = values is { Length: >= 1 } ? values[0]?.ToString()?.Trim() : null;
        var kyHieu = values is { Length: >= 2 } ? values[1]?.ToString()?.Trim() : null;
        if (string.IsNullOrEmpty(kyHieu)) return string.Empty;
        if (string.IsNullOrEmpty(mauSo)) return kyHieu;
        return $"{mauSo} - {kyHieu}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
