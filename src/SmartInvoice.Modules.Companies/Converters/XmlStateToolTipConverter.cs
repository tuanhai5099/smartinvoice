using System.Globalization;
using System.Windows.Data;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.ViewModels;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Trả về ToolTip cho cột XML: "Không tồn tại XML" khi NoXml, null khi khác.</summary>
public sealed class XmlStateToolTipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return null!;
        if (values[0] is not InvoiceDisplayDto inv) return null!;
        if (values[1] is not InvoiceListViewModel vm) return null!;
        var state = vm.GetXmlState(inv);
        return state == XmlDownloadState.NoXml ? "Không tồn tại XML" : null!;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
