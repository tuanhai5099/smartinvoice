namespace SmartInvoice.Application.Services;

/// <summary>Kết quả lấy PDF hóa đơn: thành công (bytes + tên file gợi ý) hoặc thất bại (thông báo lỗi).</summary>
public abstract record InvoicePdfResult
{
    public bool IsSuccess => this is Success;

    public sealed record Success(byte[] PdfBytes, string? SuggestedFileName = null) : InvoicePdfResult;

    public sealed record Failure(string ErrorMessage) : InvoicePdfResult;
}
