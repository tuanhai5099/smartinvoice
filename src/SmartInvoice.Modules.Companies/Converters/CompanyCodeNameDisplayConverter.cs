using System.Globalization;
using System.Windows.Data;
using SmartInvoice.Application.DTOs;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Trả về chuỗi "Mã công ty - Tên công ty" cho card công ty (Mã / Tên Gợi Nhớ).
/// </summary>
public sealed class CompanyCodeNameDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not CompanyDto dto)
            return string.Empty;
        var code = dto.CompanyCode?.Trim();
        var name = dto.CompanyName?.Trim() ?? "";
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(name))
            return "Chưa có mã/tên";
        if (string.IsNullOrEmpty(code))
            return name;
        return string.IsNullOrEmpty(name) ? code : $"{code} - {name}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
