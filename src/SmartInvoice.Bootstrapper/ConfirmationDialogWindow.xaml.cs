using System.Windows;

namespace SmartInvoice.Bootstrapper;

public partial class ConfirmationDialogWindow : Window
{
    public string Message { get; }

    public ConfirmationDialogWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        Message = message;
        DataContext = this;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

