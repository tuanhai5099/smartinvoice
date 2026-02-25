using SmartInvoice.Application.DTOs;

namespace SmartInvoice.Application.Services;

/// <summary>
/// Lấy nội dung HTML để xem hóa đơn từ API detail (không phụ thuộc file XML/HTML đã tải).
/// Gọi API detail khi cần, fill template generic và trả về HTML.
/// </summary>
public interface IInvoiceDetailViewService
{
    /// <summary>
    /// Lấy HTML hiển thị hóa đơn: đảm bảo token, gọi API detail (query hoặc sco-query theo MayTinhTien), fill template và trả về HTML.
    /// </summary>
    /// <param name="companyId">Id công ty (để lấy token).</param>
    /// <param name="inv">Thông tin hóa đơn (NbMst, KyHieu, SoHoaDon, Khmshdon, MayTinhTien...).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Đường dẫn file HTML đã fill (trong thư mục tạm, có kèm details.js) để mở bằng WebView, hoặc (null, errorMessage) nếu lỗi.</returns>
    Task<(string? Html, string? Error)> GetInvoiceDetailHtmlAsync(Guid companyId, InvoiceDisplayDto inv, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy đường dẫn file HTML dùng cho In / Lưu PDF: template in (có nền, chữ ký, footer), fill token, kèm ảnh và details.js.
    /// </summary>
    Task<(string? PrintPath, string? Error)> GetInvoicePrintHtmlPathAsync(Guid companyId, InvoiceDisplayDto inv, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách hóa đơn liên quan cho một hóa đơn (thay thế / điều chỉnh...), dùng API query/invoices/relative.
    /// </summary>
    Task<(IReadOnlyList<InvoiceRelativeItemDto> Items, string? Error)> GetInvoiceRelatedAsync(Guid companyId, InvoiceDisplayDto inv, CancellationToken cancellationToken = default);
}
