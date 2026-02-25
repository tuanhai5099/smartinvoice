using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SmartInvoice.Modules.Companies.Controls;

/// <summary>
/// UniformGrid tự tính số cột theo độ rộng thực tế và TargetItemWidth,
/// để các card công ty được dàn đều theo chiều ngang, không bị dư khoảng trắng bên phải.
/// </summary>
public class ResponsiveUniformGrid : UniformGrid
{
    public static readonly DependencyProperty TargetItemWidthProperty =
        DependencyProperty.Register(
            nameof(TargetItemWidth),
            typeof(double),
            typeof(ResponsiveUniformGrid),
            new FrameworkPropertyMetadata(380d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Chiều rộng mục tiêu của mỗi card. Số cột = ActualWidth / TargetItemWidth (làm tròn xuống, tối thiểu 1).</summary>
    public double TargetItemWidth
    {
        get => (double)GetValue(TargetItemWidthProperty);
        set => SetValue(TargetItemWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsNaN(availableSize.Width) &&
            !double.IsInfinity(availableSize.Width) &&
            availableSize.Width > 0 &&
            TargetItemWidth > 0)
        {
            var columns = Math.Max(1, (int)Math.Floor(availableSize.Width / TargetItemWidth));
            Columns = columns;
            Rows = 0; // để UniformGrid tự tính số hàng
        }

        return base.MeasureOverride(availableSize);
    }
}

