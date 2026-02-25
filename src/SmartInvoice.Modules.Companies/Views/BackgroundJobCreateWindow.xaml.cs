using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SmartInvoice.Modules.Companies.Views;

public partial class BackgroundJobCreateWindow : Wpf.Ui.Controls.FluentWindow
{
    public BackgroundJobCreateWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FixDatePickerPopupAlignment();
    }

    private void FixDatePickerPopupAlignment()
    {
        FixPopupForDatePicker(JobFromDatePicker);
        FixPopupForDatePicker(JobToDatePicker);
    }

    private static void FixPopupForDatePicker(DatePicker? datePicker)
    {
        if (datePicker == null) return;
        datePicker.ApplyTemplate();
        var popup = datePicker.Template?.FindName("PART_Popup", datePicker) as Popup;
        if (popup != null)
        {
            popup.Placement = PlacementMode.Bottom;
            popup.PlacementTarget = datePicker;
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = 2;
        }
    }

    private void JobDatePicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is DatePicker dp)
            FixPopupForDatePicker(dp);
    }

    private void BtnThangJob_Click(object sender, RoutedEventArgs e)
    {
        PopupThangJob.IsOpen = true;
    }

    private void MonthJobPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PopupThangJob.IsOpen = false;
    }

    private void BtnQuyJob_Click(object sender, RoutedEventArgs e)
    {
        PopupQuyJob.IsOpen = true;
    }

    private void QuarterJobPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PopupQuyJob.IsOpen = false;
    }

    private void BtnNamJob_Click(object sender, RoutedEventArgs e)
    {
        PopupNamJob.IsOpen = true;
    }

    private void YearJobPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PopupNamJob.IsOpen = false;
    }
}

