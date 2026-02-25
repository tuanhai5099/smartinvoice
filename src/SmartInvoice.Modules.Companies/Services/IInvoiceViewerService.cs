namespace SmartInvoice.Modules.Companies.Services;

/// <summary>Mở cửa sổ xem hóa đơn (HTML/ZIP/XML) với ngữ cảnh để in / lưu PDF.</summary>
public interface IInvoiceViewerService
{
    /// <param name="companyCode">Mã công ty / tên gọi nhỏ (dùng cho tên thư mục lưu PDF).</param>
    /// <param name="getPrintPathAsync">Khi có: In / Lưu PDF dùng template in (nền, chữ ký, footer) từ API detail.</param>
    void OpenHtmlViewer(string filePath, string? companyCode, string? companyName, object? invoice, Func<Task<(string? printPath, string? error)>>? getPrintPathAsync = null);

    /// <summary>Mở cửa sổ xem hóa đơn với nội dung HTML đã sinh (từ API detail + template), không cần file.</summary>
    void OpenHtmlViewerWithContent(string htmlContent, string? companyCode, string? companyName, object? invoice, Func<Task<(string? printPath, string? error)>>? getPrintPathAsync = null);

    /// <summary>
    /// Mở cửa sổ trình duyệt nhúng (WebView2) điều hướng tới một URL tra cứu và (nếu có) tự điền mã tra cứu vào ô txtMaTraCuu.
    /// Dùng cho các trang tra cứu hóa đơn như HTInvoice.
    /// </summary>
    void OpenLookupBrowser(string url, string? companyCode, string? companyName, object? invoice, string? searchCode);
}
