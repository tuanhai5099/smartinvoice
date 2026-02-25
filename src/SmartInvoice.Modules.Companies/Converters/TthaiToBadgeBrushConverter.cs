using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SmartInvoice.Modules.Companies.Converters;

/// <summary>
/// Màu nền badge trạng thái (kiểu web): mới = xanh lá, thay thế/được thay thế = cam, điều chỉnh/được điều chỉnh = vàng, hủy = đỏ.
/// </summary>
public sealed class TthaiToBadgeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush BrushMoi = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush BrushThayThe = new(Color.FromRgb(0xF9, 0x73, 0x16));
    private static readonly SolidColorBrush BrushDieuChinh = new(Color.FromRgb(0xEA, 0xB3, 0x08));
    private static readonly SolidColorBrush BrushHuy = new(Color.FromRgb(0xEF, 0x44, 0x44));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not short tthai)
            return BrushMoi;
        return tthai switch
        {
            1 => BrushMoi,
            2 or 4 => BrushThayThe,
            3 or 5 => BrushDieuChinh,
            6 => BrushHuy,
            _ => BrushMoi
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Màu chữ badge: nền đậm (xanh, cam, đỏ) = chữ trắng; nền vàng nhạt = chữ đen.
/// </summary>
public sealed class TthaiToBadgeForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush ForegroundDark = new(Color.FromRgb(0x1F, 0x29, 0x37));
    private static readonly SolidColorBrush ForegroundLight = new(Colors.White);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not short tthai)
            return ForegroundLight;
        return tthai switch
        {
            1 or 2 or 4 or 6 => ForegroundLight,
            _ => ForegroundDark
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
