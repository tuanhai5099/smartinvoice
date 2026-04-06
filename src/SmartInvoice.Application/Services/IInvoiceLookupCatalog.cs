namespace SmartInvoice.Application.Services;

/// <summary>
/// Điểm vào duy nhất cho tra cứu từ <see cref="InvoiceContentContext"/> (popup, bulk, nền).
/// </summary>
public interface IInvoiceLookupCatalog
{
    /// <summary>
    /// Trả về gợi ý tra cứu hoặc fallback cổng GDT. Trả <c>null</c> nếu không có JSON payload.
    /// </summary>
    InvoiceLookupSuggestion? Resolve(InvoiceContentContext context);
}
