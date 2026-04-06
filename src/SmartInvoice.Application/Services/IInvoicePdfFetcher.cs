namespace SmartInvoice.Application.Services;

/// <summary>
/// Strategy: cách lấy PDF cho một loại nhà cung cấp dịch vụ hóa đơn (theo key tvandnkntt).
/// Mỗi implementation có thể: parse payload, giải captcha, gọi portal/API, parse response và trả về PDF.
/// </summary>
public interface IInvoicePdfFetcher
{
    /// <summary>
    /// Lấy PDF hóa đơn từ payload JSON đầy đủ của hóa đơn (không cần chi tiết dòng).
    /// Implementation có thể parse payload để lấy url/mã tra cứu, giải captcha nếu cần, gọi HTTP và trả về bytes.
    /// </summary>
    /// <param name="payloadJson">Toàn bộ JSON payload của một hóa đơn (từ API).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Kết quả: Success với PDF bytes và tên file gợi ý, hoặc Failure với thông báo lỗi.</returns>
    Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default);

    /// <summary>Tải PDF dùng đúng <see cref="InvoiceContentContext.ContentForFetcher"/> (JSON hoặc XML).</summary>
    Task<InvoicePdfResult> AcquirePdfAsync(InvoiceContentContext context, CancellationToken cancellationToken = default) =>
        FetchPdfAsync(context.ContentForFetcher, cancellationToken);
}

/// <summary>
/// Fetcher gắn với một (hoặc nhiều) key nhà cung cấp (tvandnkntt).
/// Registry dùng để map key → fetcher; hóa đơn không có key hoặc key chưa đăng ký dùng fallback.
/// </summary>
public interface IKeyedInvoicePdfFetcher : IInvoicePdfFetcher
{
    /// <summary>Mã nhà cung cấp dịch vụ hóa đơn (tvandnkntt) mà fetcher này xử lý. Không null, có thể nhiều key nếu dùng nhiều instance.</summary>
    string ProviderKey { get; }
}

/// <summary>Marker cho fetcher mặc định (fallback) khi không có tvandnkntt hoặc key chưa đăng ký.</summary>
public interface IInvoicePdfFallbackFetcher : IInvoicePdfFetcher { }
