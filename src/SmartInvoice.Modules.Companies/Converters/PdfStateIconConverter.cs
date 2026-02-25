using System;
using System.Globalization;
using System.Windows.Data;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.ViewModels;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Hiển thị icon cho cột PDF: "" = chưa có PDF, "✓" = đã tải PDF.</summary>
public sealed class PdfStateIconConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return string.Empty;
        if (values[0] is not InvoiceDisplayDto inv) return string.Empty;
        if (values[1] is not InvoiceListViewModel vm) return string.Empty;
        // value thứ 3 (PdfStateRefreshTrigger) chỉ để kích hoạt re-evaluate, không dùng nội dung.
        return vm.HasPdf(inv) ? "✓" : string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

