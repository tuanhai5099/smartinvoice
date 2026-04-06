namespace SmartInvoice.Application.Services;

/// <summary>
/// Orchestrator tập trung: tra cứu qua <see cref="IInvoiceLookupCatalog"/>; tải PDF qua fetcher đã resolve;
/// tải PDF qua <see cref="IInvoicePdfFetcher.AcquirePdfAsync"/> với đúng <see cref="InvoiceContentContext.ContentForFetcher"/>.
/// </summary>
public interface IInvoiceProviderOrchestrator
{
    /// <summary>Trả về gợi ý tra cứu hoặc fallback cổng GDT nếu provider không cung cấp.</summary>
    InvoiceLookupSuggestion? ResolveLookup(InvoiceContentContext context);

    /// <summary>Tải PDF theo provider đã resolve từ <see cref="InvoiceContentContext.InvoiceJsonPayload"/>.</summary>
    Task<InvoicePdfResult> AcquirePdfAsync(InvoiceContentContext context, CancellationToken cancellationToken = default);
}
