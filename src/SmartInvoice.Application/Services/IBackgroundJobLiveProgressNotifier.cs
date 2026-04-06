namespace SmartInvoice.Application.Services;

/// <summary>Báo tiến trình từng hóa đơn cho job tải XML/PDF hàng loạt (đồng bộ popup UI, không ghi DB chi tiết).</summary>
public interface IBackgroundJobLiveProgressNotifier
{
    void ReportBulkXmlProgress(Guid jobId, DownloadXmlProgress progress);

    void ReportBulkPdfItem(BulkPdfItemProgress item);
}

/// <summary>Một bước tải PDF trong job bulk.</summary>
public sealed record BulkPdfItemProgress(
    Guid JobId,
    int Current,
    int Total,
    string ExternalId,
    string DisplayLabel,
    bool Success,
    bool Skipped,
    bool NeedsManualIntervention,
    string? Message,
    bool IsStartOfItem = false);
