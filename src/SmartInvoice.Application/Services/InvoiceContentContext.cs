namespace SmartInvoice.Application.Services;

/// <summary>
/// Bối cảnh thống nhất cho tra cứu và tải PDF: luôn có JSON gốc từ DB để router chọn đúng provider;
/// <see cref="ContentForFetcher"/> là chuỗi thực sự truyền vào <c>FetchPdfAsync</c> (JSON hoặc XML).
/// </summary>
public sealed record InvoiceContentContext(
    string ContentForFetcher,
    string InvoiceJsonPayload,
    string? SellerTaxCode,
    string? ProviderTaxCode,
    InvoiceFetcherContentKind ContentKind = InvoiceFetcherContentKind.Json,
    bool UsedJsonFallbackAfterXmlFailure = false,
    string? XmlPreparationFailureReason = null);

public enum InvoiceFetcherContentKind
{
    Json = 0,
    Xml = 1
}
