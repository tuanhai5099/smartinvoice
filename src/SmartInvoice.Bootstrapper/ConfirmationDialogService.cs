using SmartInvoice.Application.Services;

namespace SmartInvoice.Bootstrapper;

/// <summary>
/// Shows confirmation dialogs on the UI thread using WPF MessageBox.
/// </summary>
public sealed class ConfirmationDialogService : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() =>
        {
            var owner = System.Windows.Application.Current.MainWindow;
            var window = new ConfirmationDialogWindow(title, message)
            {
                Owner = owner
            };
            return window.ShowDialog() == true;
        }).Task;
    }
}
