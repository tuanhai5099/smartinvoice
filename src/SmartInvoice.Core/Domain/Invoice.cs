namespace SmartInvoice.Core.Domain;

/// <summary>
/// Hóa đơn đồng bộ từ API hóa đơn điện tử. Lưu full JSON để hỗ trợ cập nhật khi re-sync (thay thế, hủy bỏ).
/// Các cột denormalized dùng cho lọc và tổng hợp (phân trang) không cần load hết vào RAM.
/// </summary>
public class Invoice
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary> id từ API (GUID string). Dùng để upsert khi đồng bộ lại. </summary>
    public string ExternalId { get; set; } = string.Empty;
    /// <summary> Toàn bộ JSON từ API (tổng hợp hoặc chi tiết). </summary>
    public string PayloadJson { get; set; } = string.Empty;
    /// <summary> JSON mảng hdhhdvu (chi tiết dòng) nếu đồng bộ chi tiết. </summary>
    public string? LineItemsJson { get; set; }
    /// <summary> Thời điểm đồng bộ lần cuối (cập nhật khi re-sync). </summary>
    public DateTime SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary> true = hóa đơn bán ra (sold), false = mua vào (purchase). </summary>
    public bool IsSold { get; set; } = true;

    // Denormalized từ PayloadJson cho lọc và tổng hợp (phân trang)
    public DateTime? NgayLap { get; set; }
    public short Tthai { get; set; }
    public decimal? Tgtcthue { get; set; }
    public decimal? Tgtthue { get; set; }
    public decimal? TongTien { get; set; }
    public bool CoMa { get; set; }
    public bool MayTinhTien { get; set; }
    public string? KyHieu { get; set; }
    public int SoHoaDon { get; set; }
    public string? NbMst { get; set; }
    public string? NguoiBan { get; set; }
    public string? NguoiMua { get; set; }
    public string? MstMua { get; set; }
    /// <summary>Đơn vị tiền tệ (VND, VNĐ, USD, ...). Dùng cho lọc hóa đơn ngoại tệ.</summary>
    public string? Dvtte { get; set; }

    /// <summary>
    /// Trạng thái XML: 0 = chưa tải / chưa biết, 1 = đã tải (có file XML/ZIP), 2 = đã gọi API nhưng không có XML.
    /// Lưu theo từng hóa đơn để UI có thể hiển thị ổn định giữa các lần chạy (kể cả khi file bị xóa).
    /// </summary>
    public short XmlStatus { get; set; }

    /// <summary>
    /// Tên cơ sở chuẩn để lưu XML/ZIP: &quot;{KyHieu}_{Khmshdon}_{SoHoaDon}&quot; sau khi sanitize.
    /// Kết hợp với thư mục cấu hình (ExportXmlFolderPath) để tìm đúng nơi chứa XML/HTML.
    /// </summary>
    public string? XmlBaseName { get; set; }

    /// <summary>Mã số thuế tổ chức cung cấp dịch vụ hóa đơn (msttcgp) – denormalized từ payload để lọc nhanh.</summary>
    public string? ProviderTaxCode { get; set; }

    /// <summary>Mã số thuế tổ chức TVAN đăng ký kết nối (tvanDnKntt) – denormalized từ payload để lọc nhanh.</summary>
    public string? TvanTaxCode { get; set; }
}
