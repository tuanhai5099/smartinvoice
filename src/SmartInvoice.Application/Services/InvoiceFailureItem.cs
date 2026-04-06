namespace SmartInvoice.Application.Services;

/// <summary>Một hóa đơn lỗi trong job nền: đủ để hiển thị và vẫn có ExternalId cho chạy lại.</summary>
public sealed class InvoiceFailureItem
{
    public string ExternalId { get; set; } = "";

    /// <summary>Ký hiệu hóa đơn (khhdon).</summary>
    public string? KyHieu { get; set; }

    /// <summary>Mẫu số hóa đơn (khmshdon).</summary>
    public int Khmshdon { get; set; }

    /// <summary>Số hóa đơn (shdon).</summary>
    public int SoHoaDon { get; set; }

    public string? ErrorMessage { get; set; }

    public string FormatDisplayLine()
    {
        var head = $"Ký hiệu: {KyHieu ?? "—"} | Mẫu số: {Khmshdon} | Số: {SoHoaDon}";
        return string.IsNullOrWhiteSpace(ErrorMessage) ? head : $"{head} — {ErrorMessage}";
    }
}
