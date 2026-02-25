using System.Windows;
using SmartInvoice.Modules.Companies.Views;

namespace SmartInvoice.Modules.Companies.Services;

public sealed class InvoiceViewerService : IInvoiceViewerService
{
    public void OpenHtmlViewer(string filePath, string? companyCode, string? companyName, object? invoice, Func<Task<(string? printPath, string? error)>>? getPrintPathAsync = null)
    {
        var window = new InvoiceHtmlViewerWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.SetContext(companyCode, companyName, invoice);
        window.SetPrintProvider(getPrintPathAsync);
        window.LoadFile(filePath);
        window.ShowDialog();
    }

    public void OpenHtmlViewerWithContent(string htmlContent, string? companyCode, string? companyName, object? invoice, Func<Task<(string? printPath, string? error)>>? getPrintPathAsync = null)
    {
        var window = new InvoiceHtmlViewerWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.SetContext(companyCode, companyName, invoice);
        window.SetPrintProvider(getPrintPathAsync);
        window.LoadContent(htmlContent);
        window.ShowDialog();
    }

    public void OpenLookupBrowser(string url, string? companyCode, string? companyName, object? invoice, string? searchCode)
    {
        var window = new InvoiceHtmlViewerWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.SetContext(companyCode, companyName, invoice);
        window.LoadUrlWithAutoFill(url, searchCode);
        window.ShowDialog();
    }
}
