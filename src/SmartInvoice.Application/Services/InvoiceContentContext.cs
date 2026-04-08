namespace SmartInvoice.Application.Services;

/// <summary>
/// Bối cảnh thống nhất cho tra cứu và tải PDF:
/// <see cref="InvoiceJsonPayload"/> — JSON gốc (cttkhac, mhdon, msttcgp, …), dùng cho router + popup tra cứu + fetcher cần đọc trường phụ;
/// <see cref="ContentForFetcher"/> — payload gửi vào fetcher mặc định (XML khi PDF XML-first, hoặc JSON).
/// Fetcher chỉ parse JSON từ <see cref="InvoiceJsonPayload"/> qua <see cref="InvoicePayloadJsonAccessor"/>, không giả định <see cref="ContentForFetcher"/> luôn là JSON.
/// </summary>
public sealed record InvoiceContentContext(
    string ContentForFetcher,
    string InvoiceJsonPayload,
    string? SellerTaxCode,
    string? ProviderTaxCode,
    Guid? CompanyId = null,
    InvoiceFetcherContentKind ContentKind = InvoiceFetcherContentKind.Json,
    bool UsedJsonFallbackAfterXmlFailure = false,
    string? XmlPreparationFailureReason = null);

public enum InvoiceFetcherContentKind
{
    Json = 0,
    Xml = 1
}
