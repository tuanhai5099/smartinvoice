namespace SmartInvoice.Application.Services;

/// <summary>
/// Use case: lấy PDF cho một hóa đơn từ payload JSON.
/// Tự parse payload lấy key tvandnkntt, chọn fetcher tương ứng và gọi FetchPdfAsync.
/// </summary>
public interface IInvoicePdfService
{
    /// <summary>
    /// Lấy PDF hóa đơn từ payload JSON đầy đủ (nguyên đối tượng payload, không cần chi tiết).
    /// Service sẽ đọc key tvandnkntt trong payload để chọn strategy lấy PDF phù hợp.
    /// </summary>
    /// <param name="payloadJson">Toàn bộ JSON payload của hóa đơn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Kết quả PDF (bytes + tên file gợi ý) hoặc lỗi.</returns>
    Task<InvoicePdfResult> GetPdfForInvoiceAsync(string payloadJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy PDF hóa đơn theo company và external id (id từ API). Load payload từ DB, chọn fetcher theo tvandnkntt và gọi FetchPdfAsync.
    /// </summary>
    Task<InvoicePdfResult> GetPdfForInvoiceByExternalIdAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy thông tin gợi ý tra cứu hóa đơn (link tra cứu, mã tra cứu, MST người bán) cho một hóa đơn.
    /// Dùng để hiển thị popup "Gợi ý tra cứu" và mở trình duyệt nhúng/ngoài.
    /// </summary>
    Task<InvoiceLookupSuggestion?> GetLookupSuggestionAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default);
}

/// <summary>Thông tin gợi ý tra cứu hóa đơn cho một nhà cung cấp dịch vụ.</summary>
public sealed record InvoiceLookupSuggestion(
    string ProviderKey,
    string? ProviderName,
    string? SearchUrl,
    string? SecretCode,
    string? SellerTaxCode);

/// <summary>Chiến lược trích xuất thông tin gợi ý tra cứu (link, mã tra cứu, MST bán) từ payload theo từng nhà cung cấp.</summary>
public interface IInvoiceLookupProvider
{
    /// <summary>Mã nhà cung cấp dịch vụ hóa đơn (tvandnkntt / msttcgp).</summary>
    string ProviderKey { get; }

    /// <summary>
    /// Lấy gợi ý tra cứu từ payload JSON của hóa đơn.
    /// Không được gọi sang fetcher PDF; chỉ parse dữ liệu có sẵn.
    /// </summary>
    InvoiceLookupSuggestion? GetSuggestion(string payloadJson, string? sellerTaxCode);
}

/// <summary>Registry chọn IInvoiceLookupProvider theo key nhà cung cấp (tvandnkntt).</summary>
public interface IInvoiceLookupProviderRegistry
{
    /// <summary>Lấy provider tương ứng với mã nhà cung cấp; null nếu chưa có.</summary>
    IInvoiceLookupProvider? GetProvider(string? providerKey);
}
