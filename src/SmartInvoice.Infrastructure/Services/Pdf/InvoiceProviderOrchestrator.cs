using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Router PDF theo <see cref="InvoiceContentContext.InvoiceJsonPayload"/>; tra cứu qua <see cref="IInvoiceLookupCatalog"/>.
/// </summary>
public sealed class InvoiceProviderOrchestrator : IInvoiceProviderOrchestrator
{
    private readonly IInvoicePdfProviderResolver _resolver;
    private readonly IInvoiceLookupCatalog _lookupCatalog;
    private readonly ILogger<InvoiceProviderOrchestrator> _logger;

    public InvoiceProviderOrchestrator(
        IInvoicePdfProviderResolver resolver,
        IInvoiceLookupCatalog lookupCatalog,
        ILoggerFactory loggerFactory)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _lookupCatalog = lookupCatalog ?? throw new ArgumentNullException(nameof(lookupCatalog));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<InvoiceProviderOrchestrator>();
    }

    public InvoiceLookupSuggestion? ResolveLookup(InvoiceContentContext context) =>
        _lookupCatalog.Resolve(context);

    public Task<InvoicePdfResult> AcquirePdfAsync(InvoiceContentContext context, CancellationToken cancellationToken = default)
    {
        var json = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(json))
            return Task.FromResult<InvoicePdfResult>(new InvoicePdfResult.Failure("Payload hóa đơn trống."));

        var handler = _resolver.ResolveFetcher(context);
        _logger.LogDebug("Acquire PDF via {Type}.", handler.GetType().Name);
        return handler.AcquirePdfAsync(context, cancellationToken);
    }
}
