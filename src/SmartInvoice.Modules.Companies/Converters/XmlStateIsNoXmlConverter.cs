using System.Globalization;
using System.Windows.Data;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.ViewModels;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Trả về true khi trạng thái XML là NoXml (không tồn tại hồ sơ gốc).</summary>
public sealed class XmlStateIsNoXmlConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        if (values[0] is not InvoiceDisplayDto inv) return false;
        if (values[1] is not InvoiceListViewModel vm) return false;
        return vm.GetXmlState(inv) == XmlDownloadState.NoXml;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
