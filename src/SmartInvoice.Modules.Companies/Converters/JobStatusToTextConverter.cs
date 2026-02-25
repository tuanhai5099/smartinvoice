using System.Globalization;
using System.Windows.Data;
using SmartInvoice.Core.Domain;

namespace SmartInvoice.Modules.Companies.Converters;

public sealed class JobStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not BackgroundJobStatus status)
            return string.Empty;
        return status switch
        {
            BackgroundJobStatus.Pending => "Chờ",
            BackgroundJobStatus.Running => "Đang chạy",
            BackgroundJobStatus.Completed => "Xong",
            BackgroundJobStatus.Failed => "Lỗi",
            BackgroundJobStatus.Cancelled => "Đã hủy",
            _ => status.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
