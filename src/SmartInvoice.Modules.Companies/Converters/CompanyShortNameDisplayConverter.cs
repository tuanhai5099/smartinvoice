using System.Globalization;
using System.Windows.Data;
using SmartInvoice.Application.DTOs;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Hiển thị mã công ty (tên gọi nhỏ) trong dropdown; nếu không có mã thì hiển thị tên.</summary>
public sealed class CompanyShortNameDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not CompanyDto dto)
            return string.Empty;
        var code = dto.CompanyCode?.Trim();
        var name = dto.CompanyName?.Trim() ?? "";
        return !string.IsNullOrEmpty(code) ? code : (name ?? "");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
