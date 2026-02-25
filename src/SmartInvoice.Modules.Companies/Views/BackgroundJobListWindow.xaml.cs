using System.Windows;

namespace SmartInvoice.Modules.Companies.Views;

public partial class BackgroundJobListWindow : Wpf.Ui.Controls.FluentWindow
{
    public BackgroundJobListWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (DataContext is ViewModels.BackgroundJobListViewModel vm)
                vm.Dispose();
        };
    }
}
