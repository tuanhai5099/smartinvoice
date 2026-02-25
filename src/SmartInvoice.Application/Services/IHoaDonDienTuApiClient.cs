using SmartInvoice.Application.DTOs;

namespace SmartInvoice.Application.Services;

/// <summary>
/// Client gọi API trang hóa đơn điện tử: captcha, đăng nhập, profile, tra cứu hóa đơn.
/// Token được lưu và gửi kèm request; caller chịu trách nhiệm lưu token sau login.
/// </summary>
public interface IHoaDonDienTuApiClient
{
    /// <summary>
    /// Lấy captcha (key + nội dung SVG). Ảnh có thể render ra PNG với nền trắng để giải.
    /// </summary>
    Task<CaptchaResponse> GetCaptchaAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Đăng nhập với username, password, captcha key và captcha text.
    /// Trả về token (và refreshToken nếu có); caller lưu vào Company.
    /// </summary>
    Task<AuthenticateResponse> AuthenticateAsync(string username, string password, string captchaKey, string captchaValue, CancellationToken cancellationToken = default);
    /// <summary>
    /// Đổi refresh token lấy access token mới (và có thể refresh token mới). Dùng khi access token hết hạn (401).
    /// </summary>
    Task<AuthenticateResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
    /// <summary>
    /// Lấy thông tin profile (tên công ty) khi đã có Bearer token.
    /// </summary>
    Task<ProfileResponse> GetProfileAsync(string accessToken, CancellationToken cancellationToken = default);
    /// <summary>
    /// Lấy danh sách user (system-taxpayer/users). Phần tử đầu của datas là thông tin công ty đang đăng nhập.
    /// Trả về JSON của phần tử đầu (object) hoặc null nếu không có.
    /// </summary>
    Task<string?> GetSystemTaxpayerUsersFirstItemJsonAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tra cứu hóa đơn bán ra (query/invoices/sold). fromDate/toDate format dd/MM/yyyy, state cho phân trang.
    /// </summary>
    Task<InvoiceListApiResponse?> GetInvoicesSoldAsync(string accessToken, DateTime fromDate, DateTime toDate, string? state, int size = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tra cứu hóa đơn bán ra từ máy (sco-query/invoices/sold). Cùng tham số như GetInvoicesSoldAsync.
    /// </summary>
    Task<InvoiceListApiResponse?> GetInvoicesSoldScoAsync(string accessToken, DateTime fromDate, DateTime toDate, string? state, int size = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tra cứu hóa đơn mua vào (query/invoices/purchase). Cùng tham số và format thời gian như bán ra.
    /// </summary>
    Task<InvoiceListApiResponse?> GetInvoicesPurchaseAsync(string accessToken, DateTime fromDate, DateTime toDate, string? state, int size = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tra cứu hóa đơn mua vào từ máy (sco-query/invoices/purchase). Cùng tham số như GetInvoicesPurchaseAsync.
    /// </summary>
    Task<InvoiceListApiResponse?> GetInvoicesPurchaseScoAsync(string accessToken, DateTime fromDate, DateTime toDate, string? state, int size = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy chi tiết một hóa đơn (query/invoices/detail hoặc sco-query). Trả về JSON đầy đủ (có hdhhdvu nếu chi tiết).
    /// </summary>
    Task<string?> GetInvoiceDetailJsonAsync(string accessToken, string nbmst, string khhdon, int shdon, ushort khmshdon, bool fromSco = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách hóa đơn liên quan (query/invoices/relative hoặc sco-query/invoices/relative).
    /// Trả về JSON thô để caller tự parse.
    /// </summary>
    Task<string?> GetInvoiceRelativeJsonAsync(string accessToken, string nbmst, ushort khmshdon, string khhdon, int shdon, bool fromSco = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tải XML hóa đơn (query/invoices/export-xml hoặc sco-query/invoices/export-xml khi fromSco = true).
    /// Hóa đơn từ máy tính tiền (SCO) phải dùng URL sco-query.
    /// </summary>
    Task<byte[]?> GetInvoiceExportAsync(string accessToken, string nbmst, string khhdon, int shdon, ushort khmshdon, bool fromSco = false, CancellationToken cancellationToken = default);
}

public record CaptchaResponse(string Key, string ContentSvg);

public record AuthenticateResponse(bool Success, string? Token, string? RefreshToken, string? Message);

public record ProfileResponse(string? Name, string? TaxCode);
