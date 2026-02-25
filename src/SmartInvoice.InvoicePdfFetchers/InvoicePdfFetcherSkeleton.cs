using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Skeleton mẫu để implement fetcher theo từng nhà cung cấp (tvandnkntt).
/// Copy file này, đổi tên (vd. VnptInvoicePdfFetcher), gán ProviderKey = mã số thuế nhà cung cấp,
/// implement FetchPdfAsync: parse payload → (có thể giải captcha) → gọi HTTP/portal → parse response → trả về PDF.
/// Đăng ký trong Bootstrapper: containerRegistry.Register&lt;IKeyedInvoicePdfFetcher, VnptInvoicePdfFetcher&gt;();
/// </summary>
/// <remarks>
/// Đầu vào: payload JSON đầy đủ hóa đơn (không cần chi tiết dòng).
/// Có thể inject: IHttpClientFactory/HttpClient, ICaptchaSolverService, ILogger.
/// </remarks>
public sealed class InvoicePdfFetcherSkeleton : IKeyedInvoicePdfFetcher
{
    public string ProviderKey => "0123456789"; // Thay bằng mã số thuế nhà cung cấp (tvandnkntt)

    private readonly ILogger _logger;

    public InvoicePdfFetcherSkeleton(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(InvoicePdfFetcherSkeleton));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        // TODO: 1. Parse payloadJson (JsonDocument) lấy link tra cứu, mã hóa đơn, v.v.
        // TODO: 2. Nếu portal yêu cầu captcha: lấy ảnh captcha → ICaptchaSolverService.SolveFromStreamAsync → gửi kết quả
        // TODO: 3. Gọi HTTP (GET/POST) theo cách của nhà cung cấp để lấy PDF
        // TODO: 4. Parse response (stream/bytes) → return new InvoicePdfResult.Success(pdfBytes, "HD-xxx.pdf");
        _logger.LogWarning("Skeleton PDF fetcher for key '{Key}' chưa implement.", ProviderKey);
        return new InvoicePdfResult.Failure($"Chưa implement lấy PDF cho nhà cung cấp {ProviderKey}.");
    }
}
