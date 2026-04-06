using System.Collections.Generic;
using System.Linq;
using SmartInvoice.Core.Domain;

namespace SmartInvoice.Application.Services;

/// <summary>Chế độ chạy lại chỉ các hóa đơn đã lỗi ở job nguồn.</summary>
public enum BackgroundJobRetryMode
{
    Detail = 0,
    Xml = 1,
    Pdf = 2
}

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

/// <summary>Tham số tạo job tải XML hoặc PDF hàng loạt (từ màn hình danh sách hóa đơn).</summary>
public record BulkDownloadCreateDto(
    Guid CompanyId,
    bool IsSold,
    IReadOnlyList<string> InvoiceIds,
    /// <summary>Bắt buộc cho job XML; null cho job PDF (dùng thư mục mặc định theo công ty).</summary>
    string? ExportXmlFolderPath
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

/// <summary>Tham số tạo job phục hồi SCO nền (sau đồng bộ có lỗi list/detail SCO).</summary>
public record ScoRecoveryEnqueueDto(
    Guid CompanyId,
    bool IsSold,
    DateTime FromDate,
    DateTime ToDate,
    bool IncludeDetail,
    ScoRecoveryPlan Plan);

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
    DateTime? FinishedAt,
    int PdfDownloadedCount = 0,
    int PdfFailedCount = 0,
    int PdfSkippedCount = 0,
    string? FailureSummaryJson = null)
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
                if (Type == BackgroundJobType.RefreshInvoiceDetails)
                    return $"Đang đồng bộ lại chi tiết ({ProgressCurrent}/{ProgressTotal})";
                if (Type == BackgroundJobType.ScoRecovery)
                    return $"Phục hồi SCO nền ({ProgressCurrent}/{ProgressTotal})";
                if (Type == BackgroundJobType.DownloadXmlBulk)
                    return $"Đang tải XML hàng loạt ({ProgressCurrent}/{ProgressTotal})";
                if (Type == BackgroundJobType.DownloadPdfBulk)
                    return $"Đang tải PDF hàng loạt ({ProgressCurrent}/{ProgressTotal})";
                if (Type == BackgroundJobType.DownloadInvoices)
                {
                    if (ProgressCurrent <= 1 && ProgressTotal <= 2)
                        return "Đang đồng bộ hóa đơn...";
                    var syncXmlEnd = DownloadXml ? Math.Max(2, 1 + XmlTotal) : 2;
                    if (DownloadXml && XmlTotal > 0 && ProgressCurrent <= syncXmlEnd)
                        return $"Tải XML ({ProgressCurrent - 1}/{XmlTotal})";
                    if (DownloadPdf && ProgressCurrent > syncXmlEnd)
                    {
                        var pdfDone = ProgressCurrent - syncXmlEnd;
                        var pdfTotal = ProgressTotal - syncXmlEnd;
                        return $"Tải PDF ({pdfDone}/{pdfTotal})";
                    }
                }
                if (ProgressTotal <= 2 && ProgressCurrent == 1)
                    return "Đang đồng bộ hóa đơn...";
                if (ProgressTotal > 2)
                    return $"Tải XML ({ProgressCurrent - 1}/{ProgressTotal - 1})";
                return $"{ProgressCurrent}/{ProgressTotal}";
            }
            if (Status == BackgroundJobStatus.Completed || Status == BackgroundJobStatus.Failed)
            {
                if (Status == BackgroundJobStatus.Failed)
                    return !string.IsNullOrWhiteSpace(LastError) ? $"Thất bại: {LastError}" : "Thất bại.";

                if (Type == BackgroundJobType.ExportExcel)
                    return ResultPath != null ? "Đã xuất file Excel." : "Xong.";
                if (Type == BackgroundJobType.RefreshInvoiceDetails)
                    return $"Chi tiết: thành công {SyncCount}/{XmlTotal}, còn lỗi {XmlFailedCount}";
                if (Type == BackgroundJobType.ScoRecovery)
                    return $"SCO phục hồi xong. Đồng bộ lại: {SyncCount} HĐ; chi tiết còn lỗi: {XmlFailedCount}";
                if (Type == BackgroundJobType.DownloadXmlBulk)
                    return $"XML: thành công {XmlDownloadedCount}, thất bại {XmlFailedCount}, không có XML {XmlNoXmlCount}";
                if (Type == BackgroundJobType.DownloadPdfBulk)
                    return $"PDF: thành công {XmlDownloadedCount}, thất bại {XmlFailedCount}, bỏ qua {XmlNoXmlCount}";
                var parts = new List<string>();
                parts.Add($"Đồng bộ: {SyncCount} hóa đơn");
                if (DownloadXml && XmlTotal > 0)
                    parts.Add($"XML: thành công {XmlDownloadedCount}, thất bại {XmlFailedCount}, không có XML {XmlNoXmlCount}");
                if (DownloadPdf && (PdfDownloadedCount + PdfFailedCount + PdfSkippedCount) > 0)
                    parts.Add($"PDF: thành công {PdfDownloadedCount}, thất bại {PdfFailedCount}, bỏ qua {PdfSkippedCount}");
                return string.Join(". ", parts);
            }
            if (Status == BackgroundJobStatus.Pending && Type == BackgroundJobType.ExportExcel)
                return "Đang chờ xuất Excel...";
            if (Status == BackgroundJobStatus.Pending && Type == BackgroundJobType.RefreshInvoiceDetails)
                return "Đang chờ đồng bộ lại chi tiết...";
            if (Status == BackgroundJobStatus.Pending && Type == BackgroundJobType.ScoRecovery)
                return "Chờ phục hồi SCO nền...";
            if (Status == BackgroundJobStatus.Pending && Type == BackgroundJobType.DownloadXmlBulk)
                return "Đang chờ tải XML hàng loạt...";
            if (Status == BackgroundJobStatus.Pending && Type == BackgroundJobType.DownloadPdfBulk)
                return "Đang chờ tải PDF hàng loạt...";
            return $"{ProgressCurrent}/{ProgressTotal}";
        }
    }

    /// <summary>Báo cáo chi tiết: đồng bộ bao nhiêu hóa đơn, tải XML bao nhiêu thành công/thất bại/không có; hoặc đường dẫn file Excel.</summary>
    public string ReportSummary
    {
        get
        {
            if (Status == BackgroundJobStatus.Failed)
            {
                var failLines = new List<string>();
                failLines.Add("• Job thất bại.");
                failLines.Add($"• Lỗi: {(!string.IsNullOrWhiteSpace(LastError) ? LastError : "Không có thông tin chi tiết.")}");
                AppendFailureReportLines(failLines);
                return string.Join(Environment.NewLine, failLines);
            }

            if (Type == BackgroundJobType.ExportExcel)
            {
                var lines = new List<string>();
                lines.Add($"• Xuất Excel: {(IsSummaryOnly ? "Tổng hợp" : "Tổng hợp + Chi tiết")} (key: {ExportKey ?? "default"}).");
                if (ResultPath != null)
                    lines.Add($"• File: {ResultPath}");
                return string.Join(Environment.NewLine, lines);
            }
            if (Type == BackgroundJobType.DownloadXmlBulk)
            {
                var lines = new List<string>();
                lines.Add($"• Tải XML hàng loạt: tổng {XmlTotal} hóa đơn.");
                lines.Add($"  – Thành công: {XmlDownloadedCount}");
                lines.Add($"  – Thất bại: {XmlFailedCount}");
                lines.Add($"  – Không có XML: {XmlNoXmlCount}");
                if (ResultPath != null)
                    lines.Add($"• Gói ZIP: {ResultPath}");
                if (!string.IsNullOrWhiteSpace(LastError))
                    lines.Add($"• Cảnh báo/lỗi chi tiết: {LastError}");
                AppendFailureReportLines(lines);
                return string.Join(Environment.NewLine, lines);
            }
            if (Type == BackgroundJobType.DownloadPdfBulk)
            {
                var lines = new List<string>();
                lines.Add($"• Tải PDF hàng loạt: tổng {XmlTotal} hóa đơn.");
                lines.Add($"  – Thành công: {XmlDownloadedCount}");
                lines.Add($"  – Thất bại: {XmlFailedCount}");
                lines.Add($"  – Bỏ qua (chưa cấu hình): {XmlNoXmlCount}");
                if (ResultPath != null)
                    lines.Add($"• Gói ZIP: {ResultPath}");
                if (!string.IsNullOrWhiteSpace(LastError))
                    lines.Add($"• Cảnh báo/lỗi chi tiết: {LastError}");
                AppendFailureReportLines(lines);
                return string.Join(Environment.NewLine, lines);
            }
            if (Type == BackgroundJobType.RefreshInvoiceDetails)
            {
                var lines = new List<string>();
                lines.Add($"• Đồng bộ lại chi tiết: tổng {XmlTotal} hóa đơn.");
                lines.Add($"  – Thành công: {SyncCount}");
                lines.Add($"  – Còn lỗi: {XmlFailedCount}");
                AppendFailureReportLines(lines);
                return string.Join(Environment.NewLine, lines);
            }
            if (Type == BackgroundJobType.ScoRecovery)
            {
                var lines = new List<string>();
                lines.Add("• Job phục hồi hóa đơn máy tính tiền (SCO) chạy nền.");
                lines.Add($"  – Đồng bộ lại (bước resync nếu có): {SyncCount} hóa đơn.");
                lines.Add($"  – Chi tiết còn lại sau các wave: {XmlFailedCount}");
                AppendFailureReportLines(lines);
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
            if (DownloadPdf && (PdfDownloadedCount + PdfFailedCount + PdfSkippedCount) > 0)
            {
                reportLines.Add($"• Tải PDF (sau đồng bộ/XML): tổng thử {PdfDownloadedCount + PdfFailedCount + PdfSkippedCount} hóa đơn.");
                reportLines.Add($"  – Thành công: {PdfDownloadedCount}");
                reportLines.Add($"  – Thất bại: {PdfFailedCount}");
                reportLines.Add($"  – Bỏ qua (chưa hỗ trợ): {PdfSkippedCount}");
            }
            AppendFailureReportLines(reportLines);
            return string.Join(Environment.NewLine, reportLines);
        }
    }

    private void AppendFailureReportLines(List<string> lines)
    {
        var s = JobFailureSummary.Parse(FailureSummaryJson);
        const int maxDetailLines = 120;

        void AppendItems(string title, List<InvoiceFailureItem> items)
        {
            if (items.Count == 0) return;
            lines.Add($"• {title} ({items.Count}):");
            var take = Math.Min(items.Count, maxDetailLines);
            for (var i = 0; i < take; i++)
                lines.Add($"  – {items[i].FormatDisplayLine()}");
            if (items.Count > take)
                lines.Add($"  – … và thêm {items.Count - take} hóa đơn (danh sách đầy đủ nằm trong dữ liệu job).");
        }

        AppendItems("Hóa đơn lỗi chi tiết (đồng bộ API)", s.DetailFailures);
        AppendItems("Hóa đơn lỗi tải XML", s.XmlFailures);
        AppendItems("Hóa đơn lỗi tải PDF", s.PdfFailures);

        if (s.DetailFailures.Count == 0 && s.DetailFailedIds.Count > 0)
        {
            lines.Add($"• Lỗi chi tiết — chỉ có mã nội bộ ({s.DetailFailedIds.Count}, job cũ hoặc chưa ghi chi tiết):");
            foreach (var id in s.DetailFailedIds.Take(20))
                lines.Add($"  – {id}");
            if (s.DetailFailedIds.Count > 20)
                lines.Add($"  – … {s.DetailFailedIds.Count - 20} mã khác");
        }

        if (s.XmlFailures.Count == 0 && s.XmlFailedIds.Count > 0)
            lines.Add($"• Lỗi XML: {s.XmlFailedIds.Count} hóa đơn — dùng nút \"Lại XML lỗi\" để thử lại.");
        if (s.PdfFailures.Count == 0 && s.PdfFailedIds.Count > 0)
            lines.Add($"• Lỗi PDF: {s.PdfFailedIds.Count} hóa đơn — dùng nút \"Lại PDF lỗi\" để thử lại.");
    }
}

public interface IBackgroundJobService
{
    Task<BackgroundJobDto> EnqueueDownloadInvoicesAsync(BackgroundJobCreateDto options, CancellationToken cancellationToken = default);

    /// <summary>Đưa job xuất Excel vào hàng đợi (chạy nền, xong lưu file và cập nhật ResultPath).</summary>
    Task<BackgroundJobDto> EnqueueExportExcelAsync(ExportExcelCreateDto options, CancellationToken cancellationToken = default);

    /// <summary>Đưa job tải XML hàng loạt vào hàng đợi (chạy nền, xong báo user).</summary>
    Task<BackgroundJobDto> EnqueueDownloadXmlBulkAsync(BulkDownloadCreateDto options, CancellationToken cancellationToken = default);

    /// <summary>Đưa job tải PDF hàng loạt vào hàng đợi (chạy nền, xong báo user).</summary>
    Task<BackgroundJobDto> EnqueueDownloadPdfBulkAsync(BulkDownloadCreateDto options, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundJobDto>> GetRecentJobsAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Lấy một job theo Id (null nếu không tồn tại). Dùng cho poll tiến trình popup hàng loạt.</summary>
    Task<BackgroundJobDto?> GetJobByIdAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Tạo job mới chỉ xử lý các ExternalId đã lỗi ở job nguồn (chi tiết / XML / PDF).</summary>
    Task<BackgroundJobDto> EnqueueRetryFailedInvoicesAsync(Guid sourceJobId, BackgroundJobRetryMode mode, CancellationToken cancellationToken = default);

    /// <summary>Đưa job phục hồi SCO vào hàng đợi. Trả về null nếu không cần; trả về job hiện có nếu đã có Pending/Running trùng khóa.</summary>
    Task<BackgroundJobDto?> EnqueueScoRecoveryAsync(ScoRecoveryEnqueueDto options, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Gọi khi ứng dụng đang đóng: hủy mọi worker và job đang chạy, chờ ngắn để tác vụ dừng.</summary>
    void NotifyApplicationStopping();
}

/// <summary>Gửi thông báo khi job nền hoàn thành (thành công/thất bại) — ví dụ: Windows toast, tray.</summary>
public interface IBackgroundJobCompletedNotifier
{
    /// <summary>Gọi khi một job đã kết thúc (Completed hoặc Failed). Có thể chạy trên background thread.</summary>
    void Notify(BackgroundJobDto job);
}
