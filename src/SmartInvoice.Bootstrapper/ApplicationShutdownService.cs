using SmartInvoice.Application.Services;

namespace SmartInvoice.Bootstrapper;

/// <summary>
/// Shuts down the WPF application by calling Application.Current.Shutdown().
/// </summary>
public sealed class ApplicationShutdownService : IApplicationShutdownService
{
    public void Shutdown()
    {
        System.Windows.Application.Current.Shutdown();
    }
}
