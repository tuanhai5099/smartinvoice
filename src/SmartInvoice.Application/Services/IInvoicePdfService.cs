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

    /// <summary>Lưu cấu hình domain tra cứu theo cặp (NCC, MST người bán).</summary>
    Task SaveProviderDomainOverrideAsync(
        Guid companyId,
        string providerTaxCode,
        string sellerTaxCode,
        string searchUrl,
        string? providerName = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Thông tin gợi ý tra cứu hóa đơn cho một nhà cung cấp dịch vụ (descriptor bất biến).</summary>
public sealed record InvoiceLookupSuggestion(
    string ProviderKey,
    string? ProviderName,
    string? SearchUrl,
    string? SecretCode,
    string? SellerTaxCode,
    /// <summary>MST nhà cung cấp dịch vụ hóa đơn (msttcgp) từ payload.</summary>
    string? ProviderTaxCode = null,
    bool RequiresDomainInput = false,
    /// <summary>Mã tra cứu phụ (vd. &quot;Mã công ty&quot; WinInvoice), đồng bộ với fetcher.</summary>
    string? AuxiliaryLookupCode = null);
