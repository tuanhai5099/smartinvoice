using SmartInvoice.Application.DTOs;

namespace SmartInvoice.Application.Services;

public interface ICompanyAppService
{
    Task<IReadOnlyList<CompanyDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<CompanyDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Lấy công ty để sửa (có Password và CompanyCode).</summary>
    Task<CompanyDto?> GetByIdForEditAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CompanyDto> CreateAsync(CompanyEditDto dto, CancellationToken cancellationToken = default);
    /// <summary>
    /// Thêm công ty mới: thử đăng nhập trước. Thành công thì lưu công ty + token; thất bại thì đếm lần sai, quá 3 lần thì chặn.
    /// </summary>
    Task<AddCompanyResult> AddCompanyWithLoginAsync(CompanyEditDto dto, CancellationToken cancellationToken = default);
    Task UpdateAsync(Guid id, CompanyEditDto dto, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>
    /// Kiểm tra token còn hợp lệ không (gọi API profile). Nếu 401 thì thử refresh token (nếu có) rồi thử lại. Trả về true nếu token dùng được.
    /// </summary>
    Task<bool> ValidateTokenAsync(Guid companyId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Đảm bảo công ty có access token hợp lệ: thử dùng token hiện tại, nếu 401 thì gọi API refresh (nếu có refresh token) và cập nhật DB. Trả về true nếu có token dùng được (caller nên load lại company để lấy token mới nếu đã refresh).
    /// </summary>
    Task<bool> EnsureValidTokenAsync(Guid companyId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Đăng nhập trang hóa đơn điện tử, cập nhật AccessToken/RefreshToken và thông tin công ty (tên, MST).
    /// </summary>
    Task<LoginResult> LoginAndSyncProfileAsync(Guid companyId, CancellationToken cancellationToken = default);
}

public record LoginResult(bool Success, string? Message, string? CompanyName, string? TaxCode);

public record AddCompanyResult(bool Success, string Message, int? FailureCount = null);
