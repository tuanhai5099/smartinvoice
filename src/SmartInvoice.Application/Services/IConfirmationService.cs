namespace SmartInvoice.Application.Services;

/// <summary>
/// Service to show confirmation dialogs (e.g. Yes/No before delete).
/// Implementation runs on UI thread (WPF MessageBox or WPF-UI ContentDialog).
/// </summary>
public interface IConfirmationService
{
    /// <summary>
    /// Shows a confirmation dialog. Returns true if user confirms, false otherwise.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
}
