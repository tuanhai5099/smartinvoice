using System.Net;

namespace SmartInvoice.Infrastructure.HoaDonDienTu;

/// <summary>Retry với exponential backoff khi API trả 429 (rate limit) hoặc 503 (server busy). Tham khảo References/VLKCrawlData/RetryHelper.</summary>
internal static class ApiRetryHelper
{
    private const int DefaultMaxRetries = 3;
    private const int DefaultInitialDelayMs = 1000;

    /// <summary>Gửi request; nếu trả 429 hoặc 503 thì chờ (2^attempt * initialDelayMs) rồi thử lại, tối đa maxRetries lần.</summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        int maxRetries = DefaultMaxRetries,
        int initialDelayMs = DefaultInitialDelayMs)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            response = await send().ConfigureAwait(false);
            var status = response.StatusCode;
            if (status != (HttpStatusCode)429 && status != HttpStatusCode.ServiceUnavailable)
                return response;
            if (attempt == maxRetries)
                return response;
            response.Dispose();
            var delayMs = (int)Math.Pow(2, attempt) * initialDelayMs;
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
        return response!;
    }
}
