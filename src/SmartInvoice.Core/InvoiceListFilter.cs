namespace SmartInvoice.Core;

/// <summary>
/// Bộ lọc cho danh sách hóa đơn (phân trang, tổng hợp từ DB không load hết RAM).
/// LoaiHoaDon: 0 = tổng số, 1 = có mã, 2 = không mã, 3 = máy tính tiền, 4 = ngoại tệ (dvtte khác VND/VNĐ).
/// SortBy: tên cột entity (NgayLap, KyHieu, SoHoaDon, NguoiBan, Tthai, Tgtcthue, Tgtthue, TongTien) hoặc null = mặc định NgayLap giảm dần.
/// </summary>
public record InvoiceListFilter(
    DateTime? FromDate,
    DateTime? ToDate,
    bool? IsSold,
    short? Tthai,
    int LoaiHoaDon,
    string? SearchText,
    string? FilterKyHieu,
    string? FilterSoHoaDon,
    string? FilterMstNguoiBan,
    string? FilterTenNguoiBan,
    string? FilterMstLoaiTru,
    string? FilterLoaiTruBenBan,
    string? FilterProviderTaxCode,
    string? FilterTvanTaxCode,
    string? SortBy = null,
    bool SortDescending = true
);

/// <summary>
/// Tổng hợp số liệu hóa đơn (tính bằng SQL aggregation, không load toàn bộ).
/// </summary>
public record InvoiceSummary(
    int TotalCount,
    int CountCoMa,
    int CountKhongMa,
    int CountMayTinhTien,
    decimal TotalChuaThue,
    decimal TotalTienThue,
    decimal TotalThanhTien
);
