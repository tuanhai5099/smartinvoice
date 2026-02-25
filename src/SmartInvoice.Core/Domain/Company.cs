namespace SmartInvoice.Core.Domain;

/// <summary>
/// Entity: Công ty đăng ký đăng nhập trang hóa đơn điện tử.
/// Username/Password dùng để đăng nhập; sau đăng nhập lấy đầy đủ thông tin từ system-taxpayer/users (datas[0]).
/// </summary>
public class Company
{
    public Guid Id { get; set; }
    /// <summary>Mã công ty do người dùng nhập, dùng để quản lý (tối đa 30 ký tự, unique).</summary>
    public string? CompanyCode { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    /// <summary> Tên công ty (name từ API users). </summary>
    public string? CompanyName { get; set; }
    /// <summary> Mã số thuế (username từ API = MST). </summary>
    public string? TaxCode { get; set; }
    /// <summary> Email từ API users. </summary>
    public string? Email { get; set; }
    /// <summary> groupId từ API users. </summary>
    public string? GroupId { get; set; }
    /// <summary> type từ API users (2 = taxpayer...). </summary>
    public int? UserType { get; set; }
    /// <summary> phoneNumber từ API users. </summary>
    public string? PhoneNumber { get; set; }
    /// <summary> status từ API users (1 = active...). </summary>
    public int? UserStatus { get; set; }
    /// <summary> Toàn bộ object datas[0] từ system-taxpayer/users, lưu nguyên JSON. </summary>
    public string? UserDataJson { get; set; }
    /// <summary> lastLoginAt từ API users (hoặc thời điểm login thành công). </summary>
    public DateTime? LastLoginAt { get; set; }
    /// <summary> Thời điểm đồng bộ dữ liệu từ API hóa đơn điện tử lần cuối (profile + users). </summary>
    public DateTime? LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
