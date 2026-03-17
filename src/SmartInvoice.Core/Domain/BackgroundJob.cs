namespace SmartInvoice.Core.Domain;

/// <summary>Loại job nền: tải hóa đơn, xuất Excel, tải XML hàng loạt, tải PDF hàng loạt.</summary>
public enum BackgroundJobType
{
    DownloadInvoices = 1,
    ExportExcel = 2,
    DownloadXmlBulk = 3,
    DownloadPdfBulk = 4
}

/// <summary>Trạng thái job nền.</summary>
public enum BackgroundJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>Job chạy nền: tải hóa đơn (và XML/PDF) cho một công ty trong khoảng ngày.</summary>
public class BackgroundJob
{
    public Guid Id { get; set; }
    public BackgroundJobType Type { get; set; }
    public BackgroundJobStatus Status { get; set; }

    public Guid CompanyId { get; set; }

    /// <summary>true = bán ra (sold), false = mua vào (purchase).</summary>
    public bool IsSold { get; set; }

    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }

    /// <summary>Có đồng bộ chi tiết (hdhhdvu) hay chỉ header.</summary>
    public bool IncludeDetail { get; set; }

    /// <summary>Có tải XML (ZIP/XML) sau khi sync xong không.</summary>
    public bool DownloadXml { get; set; }

    /// <summary>Dự phòng cho tương lai: tải PDF.</summary>
    public bool DownloadPdf { get; set; }

    /// <summary>Key xuất Excel (ví dụ: "tonghop", "chitiet", "default"). Null cho job không phải ExportExcel.</summary>
    public string? ExportKey { get; set; }

    /// <summary>Chỉ xuất sheet tổng hợp (true) hay cả sheet chi tiết (false). Dùng cho job ExportExcel.</summary>
    public bool IsSummaryOnly { get; set; }

    /// <summary>Số bước đã hoàn thành (ví dụ: số hóa đơn đã xử lý, hoặc bước pipeline).</summary>
    public int ProgressCurrent { get; set; }

    /// <summary>Tổng số bước dự kiến.</summary>
    public int ProgressTotal { get; set; }

    public string? Description { get; set; }
    public string? LastError { get; set; }

    /// <summary>Đường dẫn file/folder kết quả (ví dụ: ZIP XML đã tạo).</summary>
    public string? ResultPath { get; set; }

    /// <summary>Số hóa đơn đã đồng bộ từ API (bước 1).</summary>
    public int SyncCount { get; set; }

    /// <summary>Tổng số hóa đơn cần tải XML (bước 2).</summary>
    public int XmlTotal { get; set; }

    /// <summary>Số XML tải thành công.</summary>
    public int XmlDownloadedCount { get; set; }

    /// <summary>Số XML tải thất bại (lỗi).</summary>
    public int XmlFailedCount { get; set; }

    /// <summary>Số hóa đơn không có XML (API trả rỗng).</summary>
    public int XmlNoXmlCount { get; set; }

    /// <summary>JSON payload cho job tải XML/PDF hàng loạt: InvoiceIds, ExportXmlFolderPath (chỉ XML).</summary>
    public string? PayloadJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

