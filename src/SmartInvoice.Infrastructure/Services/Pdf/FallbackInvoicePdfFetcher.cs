using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Fetcher mặc định cho hóa đơn không có tvandnkntt hoặc key chưa có implementation.
/// Trả về Failure với thông báo rõ ràng; sau này có thể thêm cách lấy PDF chung (vd. từ link tra cứu).
/// </summary>
public sealed class FallbackInvoicePdfFetcher : IInvoicePdfFallbackFetcher
{
    private readonly ILogger _logger;

    public FallbackInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(FallbackInvoicePdfFetcher));
    }

    public Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fallback PDF fetcher: chưa hỗ trợ lấy PDF cho hóa đơn này (không có hoặc chưa đăng ký nhà cung cấp).");
        return Task.FromResult<InvoicePdfResult>(
            new InvoicePdfResult.Failure("Chưa hỗ trợ lấy PDF cho nhà cung cấp dịch vụ hóa đơn này. Bạn có thể mở link tra cứu trong trình duyệt để tải PDF."));
    }
}
