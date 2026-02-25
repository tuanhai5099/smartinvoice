namespace SmartInvoice.Application.Services;

/// <summary>
/// Service to shut down the application (close main window, exit process).
/// Implementation is in the host (e.g. Bootstrapper) and calls Application.Current.Shutdown().
/// </summary>
public interface IApplicationShutdownService
{
    void Shutdown();
}
