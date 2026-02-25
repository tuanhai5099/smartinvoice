using System.Collections.Generic;
using SmartInvoice.Core.Domain;

namespace SmartInvoice.Application.Services;

public record BackgroundJobCreateDto(
    Guid CompanyId,
    bool IsSold,
    DateTime FromDate,
    DateTime ToDate,
    bool IncludeDetail,
    bool DownloadXml,
    bool DownloadPdf,
    bool ExportExcel
);

/// <summary>Tham số tạo job xuất Excel (chạy nền, xong báo user).</summary>
public record ExportExcelCreateDto(
    Guid CompanyId,
    bool IsSold,
    DateTime FromDate,
    DateTime ToDate,
    /// <summary>Key handler/template: "tonghop", "chitiet", "default". Theo cấu hình có thể gọi handler khác.</summary>
    string ExportKey,
    /// <summary>true = chỉ sheet Tổng hợp; false = sheet Tổng hợp + Chi tiết.</summary>
    bool IsSummaryOnly
);

public record BackgroundJobDto(
    Guid Id,
    BackgroundJobType Type,
    BackgroundJobStatus Status,
    Guid CompanyId,
    string? CompanyName,
    bool IsSold,
    DateTime FromDate,
    DateTime ToDate,
    bool IncludeDetail,
    bool DownloadXml,
    bool DownloadPdf,
    string? ExportKey,
    bool IsSummaryOnly,
    int ProgressCurrent,
    int ProgressTotal,
    string? Description,
    string? LastError,
    string? ResultPath,
    int SyncCount,
    int XmlTotal,
    int XmlDownloadedCount,
    int XmlFailedCount,
    int XmlNoXmlCount,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt
)
{
    /// <summary>Tiến trình hiển thị: đang đồng bộ / đang tải XML (x/y) / xuất Excel / hoặc tóm tắt khi xong.</summary>
    public string ProgressDisplayText
    {
        get
        {
            if (Status == BackgroundJobStatus.Running)
            {
                if (Type == BackgroundJobType.ExportExcel)
                    return "Đang xuất Excel...";
                if (ProgressTotal <= 2 && ProgressCurrent == 1)
                    return "Đang đồng bộ hóa đơn...";
                if (ProgressTotal > 2)
                    return $"Tải XML ({ProgressCurrent - 1}/{ProgressTotal - 1})";
                return $"{ProgressCurrent}/{ProgressTotal}";
            }
            if (Status == BackgroundJobStatus.Completed || Status == BackgroundJobStatus.Failed)
            {
                if (Type == BackgroundJobType.ExportExcel)
                    return ResultPath != null ? "Đã xuất file Excel." : "Xong.";
                var parts = new List<string>();
                parts.Add($"Đồng bộ: {SyncCount} hóa đơn");
                if (DownloadXml && XmlTotal > 0)
                    parts.Add($"XML: thành công {XmlDownloadedCount}, thất bại {XmlFailedCount}, không có XML {XmlNoXmlCount}");
                return string.Join(". ", parts);
            }
            if (Status == BackgroundJobStatus.Pending && Type == BackgroundJobType.ExportExcel)
                return "Đang chờ xuất Excel...";
            return $"{ProgressCurrent}/{ProgressTotal}";
        }
    }

    /// <summary>Báo cáo chi tiết: đồng bộ bao nhiêu hóa đơn, tải XML bao nhiêu thành công/thất bại/không có; hoặc đường dẫn file Excel.</summary>
    public string ReportSummary
    {
        get
        {
            if (Type == BackgroundJobType.ExportExcel)
            {
                var lines = new List<string>();
                lines.Add($"• Xuất Excel: {(IsSummaryOnly ? "Tổng hợp" : "Tổng hợp + Chi tiết")} (key: {ExportKey ?? "default"}).");
                if (ResultPath != null)
                    lines.Add($"• File: {ResultPath}");
                return string.Join(Environment.NewLine, lines);
            }
            var reportLines = new List<string>();
            reportLines.Add($"• Đồng bộ hóa đơn: đã tải {SyncCount} hóa đơn từ API (tổng cộng).");
            if (DownloadXml)
            {
                reportLines.Add($"• Tải XML: tổng {XmlTotal} hóa đơn cần tải XML.");
                reportLines.Add($"  – Thành công: {XmlDownloadedCount}");
                reportLines.Add($"  – Thất bại (lỗi): {XmlFailedCount}");
                reportLines.Add($"  – Không có XML: {XmlNoXmlCount}");
            }
            else
                reportLines.Add("• Không tải XML (tùy chọn tắt).");
            return string.Join(Environment.NewLine, reportLines);
        }
    }
}

public interface IBackgroundJobService
{
    Task<BackgroundJobDto> EnqueueDownloadInvoicesAsync(BackgroundJobCreateDto options, CancellationToken cancellationToken = default);

    /// <summary>Đưa job xuất Excel vào hàng đợi (chạy nền, xong lưu file và cập nhật ResultPath).</summary>
    Task<BackgroundJobDto> EnqueueExportExcelAsync(ExportExcelCreateDto options, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundJobDto>> GetRecentJobsAsync(int maxCount, CancellationToken cancellationToken = default);

    Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>Gửi thông báo khi job nền hoàn thành (thành công/thất bại) — ví dụ: Windows toast, tray.</summary>
public interface IBackgroundJobCompletedNotifier
{
    /// <summary>Gọi khi một job đã kết thúc (Completed hoặc Failed). Có thể chạy trên background thread.</summary>
    void Notify(BackgroundJobDto job);
}
