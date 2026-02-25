using System;
using System.Globalization;
using System.Windows.Data;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>Chuyển URL đầy đủ thành dạng rút gọn chỉ hiển thị domain (vd. https://van.ehoadon.vn/Lookup → van.ehoadon.vn).</summary>
public sealed class UrlToShortDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return "(Chưa có link)";
        try
        {
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.IsAbsoluteUri)
                return uri.Host ?? url;
        }
        catch
        {
            // ignore
        }
        return url;
    }

    public object ConvertBack(object value, Type targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
