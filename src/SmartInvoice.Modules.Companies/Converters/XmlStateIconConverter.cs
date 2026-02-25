using System.Globalization;
using System.IO;
using System.Windows.Data;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.ViewModels;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Trả về ký tự hiển thị cho cột XML/PDF: "" = chưa tải, "✓" = đã tải, "✗" = không có XML (đã gọi API nhưng không có).</summary>
public sealed class XmlStateIconConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return string.Empty;
        if (values[0] is not InvoiceDisplayDto inv) return string.Empty;
        if (values[1] is not InvoiceListViewModel vm) return string.Empty;
        var state = vm.GetXmlState(inv);
        return state switch
        {
            XmlDownloadState.Downloaded => "✓",
            XmlDownloadState.NoXml => "✗",
            _ => string.Empty
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
