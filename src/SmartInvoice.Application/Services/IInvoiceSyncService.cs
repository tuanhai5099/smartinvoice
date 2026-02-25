using System.Globalization;
using SmartInvoice.Application.DTOs;

namespace SmartInvoice.Application.Services;

/// <summary>
/// Đồng bộ hóa đơn từ API và lấy kết quả đồng bộ lần trước.
/// </summary>
public interface IInvoiceSyncService
{
    /// <summary>
    /// Đồng bộ hóa đơn: đảm bảo token, gọi API list (sold hoặc purchase + sco) theo fromDate-toDate, tách theo từng tháng, optionally lấy chi tiết, lưu/upsert.
    /// </summary>
    /// <param name="isSold">true = bán ra (sold), false = mua vào (purchase).</param>
    Task<SyncInvoicesResult> SyncInvoicesAsync(Guid companyId, DateTime fromDate, DateTime toDate, bool includeDetail, bool isSold = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách hóa đơn đã đồng bộ lần trước (theo company). Trả về dữ liệu hiển thị từ PayloadJson.
    /// </summary>
    Task<IReadOnlyList<InvoiceDisplayDto>> GetLastSyncedInvoicesAsync(Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy một trang hóa đơn + tổng số + tổng hợp (SUM/COUNT trong DB, không load hết RAM).
    /// </summary>
    Task<(IReadOnlyList<InvoiceDisplayDto> Page, int TotalCount, InvoiceSummaryDto Summary)> GetInvoicesPagedAsync(Guid companyId, InvoiceListFilterDto filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tải XML cho tất cả hóa đơn trong danh sách, lưu vào thư mục đích. Báo cáo tiến độ qua progress.
    /// </summary>
    Task<DownloadXmlResult> DownloadInvoicesXmlAsync(Guid companyId, IReadOnlyList<InvoiceDisplayDto> invoices, string folderPath, IProgress<DownloadXmlProgress>? progress, CancellationToken cancellationToken = default);
}

public record SyncInvoicesResult(bool Success, string? Message, int TotalSynced = 0);

public record DownloadXmlProgress(int Current, int Total, string? CurrentFileName, DownloadXmlItemResult? ItemResult = null);

/// <summary>Kết quả tải XML cho một hóa đơn: thành công, không có XML (API trả về rỗng), hoặc lỗi.</summary>
public record DownloadXmlItemResult(string InvoiceKey, string SoHoaDonDisplay, bool Success, bool NoXml, string? Message);

/// <summary>Kết quả tải XML: số tải thành công, thất bại, không có XML.</summary>
public record DownloadXmlResult(bool Success, string? Message, int DownloadedCount = 0, int FailedCount = 0, int NoXmlCount = 0, string? ZipPath = null);

/// <summary>Bộ lọc danh sách hóa đơn (UI → service). LoaiHoaDon: 0=tổng số, 1=có mã, 2=không mã, 3=máy tính tiền, 4=ngoại tệ. SortBy: cột sắp xếp (NgayLap, KyHieu, SoHoaDon, CounterpartyName, TrangThaiDisplay, Tgtcthue, Tgtthue, TongTien) hoặc null.</summary>
public record InvoiceListFilterDto(
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
    string? SortBy = null,
    bool SortDescending = true
);

/// <summary>Tổng hợp tính từ DB (không load hết RAM).</summary>
public record InvoiceSummaryDto(
    int TotalCount,
    int CountCoMa,
    int CountKhongMa,
    int CountMayTinhTien,
    decimal TotalChuaThue,
    decimal TotalTienThue,
    decimal TotalThanhTien
);

/// <summary>
/// DTO hiển thị một dòng hóa đơn trên màn hình (parse từ PayloadJson; đầy đủ theo sample API).
/// </summary>
public record InvoiceDisplayDto(
    string Id,
    string? KyHieu,
    int SoHoaDon,
    string? SoHoaDonDisplay,
    DateTime? NgayKy,
    DateTime? NgayLap,
    string? NguoiBan,
    string? NbMst,
    string? NguoiMua,
    string? MstMua,
    decimal? Tgtcthue,
    decimal? Tgtthue,
    decimal? TongTien,
    string? Thtttoan,
    short Tthai,
    short Ttxly,
    string? TrangThaiDisplay,
    ushort Khmshdon,
    bool CoMa,
    bool MayTinhTien,
    bool IsBanRa,
    string? MaHoaDon = null,
    string? Dvtte = null,
    int? Thlap = null,
    int? Hthdon = null,
    string? CounterpartyName = null,
    string? CounterpartyMst = null,
    short XmlStatus = 0,
    string? XmlBaseName = null,
    decimal? ExchangeRate = null
)
{
    /// <summary>Thông tin đối tác hiển thị: Tên công ty (mã số thuế) + ngoại tệ (nếu có).</summary>
    public string? CounterpartyDisplay
    {
        get
        {
            var namePart = string.IsNullOrWhiteSpace(CounterpartyMst)
                ? (CounterpartyName ?? "")
                : $"{(CounterpartyName ?? "").Trim()} ({CounterpartyMst})";

            if (!IsForeignCurrency || string.IsNullOrWhiteSpace(Dvtte))
                return namePart;

            // Nếu có tỷ giá thì hiển thị dạng (Ngoại tệ: USD*26.239), nếu không thì chỉ (Ngoại tệ: USD).
            if (ExchangeRate is decimal rate)
            {
                var rateStr = rate.ToString("#,0.####", new CultureInfo("vi-VN"));
                return $"{namePart} (Ngoại tệ: {Dvtte}*{rateStr})";
            }

            return $"{namePart} (Ngoại tệ: {Dvtte})";
        }
    }

    /// <summary>Hóa đơn ngoại tệ khi Dvtte khác VND/VNĐ.</summary>
    public bool IsForeignCurrency =>
        !string.IsNullOrWhiteSpace(Dvtte) &&
        !string.Equals(Dvtte, "VND", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Dvtte, "VNĐ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Giá trị quy đổi ra VND cho cột Chưa thuế (chỉ áp dụng cho ngoại tệ, khi có tỷ giá).</summary>
    public decimal? TgtcthueVnd =>
        IsForeignCurrency && ExchangeRate.HasValue && Tgtcthue.HasValue
            ? decimal.Round(Tgtcthue.Value * ExchangeRate.Value, 0, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>Giá trị quy đổi ra VND cho cột Tiền thuế (chỉ áp dụng cho ngoại tệ, khi có tỷ giá).</summary>
    public decimal? TgtthueVnd =>
        IsForeignCurrency && ExchangeRate.HasValue && Tgtthue.HasValue
            ? decimal.Round(Tgtthue.Value * ExchangeRate.Value, 0, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>Giá trị quy đổi ra VND cho cột Thành tiền (chỉ áp dụng cho ngoại tệ, khi có tỷ giá).</summary>
    public decimal? TongTienVnd =>
        IsForeignCurrency && ExchangeRate.HasValue && TongTien.HasValue
            ? decimal.Round(TongTien.Value * ExchangeRate.Value, 0, MidpointRounding.AwayFromZero)
            : null;
}
