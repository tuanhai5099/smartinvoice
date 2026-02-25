namespace SmartInvoice.Application.Services;

/// <summary>
/// Xuất Excel theo cấu hình/key. Key đăng ký (tonghop, chitiet, default) xác định template hoặc handler;
/// sau này có thể thêm key khác hoặc lựa chọn template Excel để export.
/// </summary>
public interface IExcelExportService
{
    /// <summary>
    /// Xuất file Excel theo yêu cầu (sheet Tổng hợp; hoặc Tổng hợp + Chi tiết).
    /// </summary>
    /// <param name="request">Tham số xuất (công ty, khoảng ngày, bán/mua, key, chỉ tổng hợp hay cả chi tiết).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Đường dẫn file .xlsx đã lưu.</returns>
    Task<string> ExportAsync(ExportExcelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Tham số gọi xuất Excel (theo cấu hình/key).</summary>
public record ExportExcelRequest(
    Guid CompanyId,
    bool IsSold,
    DateTime FromDate,
    DateTime ToDate,
    /// <summary>Key handler/template: "tonghop", "chitiet", "default".</summary>
    string ExportKey,
    /// <summary>true = chỉ sheet Tổng hợp; false = sheet Tổng hợp + Chi tiết.</summary>
    bool IsSummaryOnly
);
