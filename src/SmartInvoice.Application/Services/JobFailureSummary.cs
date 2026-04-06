using System.Text.Json;

namespace SmartInvoice.Application.Services;

/// <summary>
/// Danh sách hóa đơn (ExternalId) lỗi theo từng bước: chi tiết, XML, PDF — lưu JSON trên <see cref="Core.Domain.BackgroundJob"/>.</summary>
public sealed class JobFailureSummary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public List<string> DetailFailedIds { get; set; } = new();
    public List<string> XmlFailedIds { get; set; } = new();
    public List<string> PdfFailedIds { get; set; } = new();

    /// <summary>Chi tiết từng HĐ lỗi bước đồng bộ chi tiết (hiển thị trong báo cáo job).</summary>
    public List<InvoiceFailureItem> DetailFailures { get; set; } = new();

    public List<InvoiceFailureItem> XmlFailures { get; set; } = new();
    public List<InvoiceFailureItem> PdfFailures { get; set; } = new();

    public static JobFailureSummary Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JobFailureSummary();
        try
        {
            return JsonSerializer.Deserialize<JobFailureSummary>(json, JsonOptions) ?? new JobFailureSummary();
        }
        catch
        {
            return new JobFailureSummary();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
