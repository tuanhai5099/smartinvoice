using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ MISA meInvoice (NCC 0101243150) bằng HTTP GET.
/// Mã bí mật lấy từ cttkhac: item có ttruong = "transaction id" (hoặc "transactionid"), lấy value của dlieu.
/// URL: https://www.meinvoice.vn/tra-cuu/DownloadHandler.ashx?Type=pdf&Code={transactionId}
/// </summary>
public sealed class MeinvoiceInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (MISA meInvoice).</summary>
    public string ProviderKey => "0101243150";

    private const string DownloadUrlTemplate = "https://www.meinvoice.vn/tra-cuu/DownloadHandler.ashx?Type=pdf&Code={0}";

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public MeinvoiceInvoicePdfFetcher(HttpClient httpClient, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = loggerFactory.CreateLogger(nameof(MeinvoiceInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var transactionId = GetTransactionIdFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(transactionId))
        {
            _logger.LogWarning("Meinvoice PDF: payload không có cttkhac với ttruong 'transaction id'.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu mã giao dịch (cttkhac.transaction id). Không thể tải PDF từ meInvoice.");
        }

        var url = string.Format(DownloadUrlTemplate, Uri.EscapeDataString(transactionId.Trim()));
        try
        {
            _logger.LogDebug("Meinvoice PDF: GET {Url}", url);
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return new InvoicePdfResult.Failure("Phản hồi từ meInvoice rỗng. Kiểm tra mã giao dịch hoặc thử lại sau.");
            }

            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileName = "invoice.pdf";

            _logger.LogInformation("Meinvoice PDF: đã tải {File} ({Size} bytes).", fileName, bytes.Length);
            return new InvoicePdfResult.Success(bytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Meinvoice PDF: lỗi HTTP khi GET {Url}", url);
            return new InvoicePdfResult.Failure("Lỗi kết nối meInvoice: " + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meinvoice PDF: lỗi khi lấy PDF với transaction id.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF: " + ex.Message);
        }
    }

    /// <summary>Lấy giá trị transaction id từ cttkhac: item có ttruong = "transaction id" (hoặc "transactionid") thì lấy dlieu.</summary>
    private static string? GetTransactionIdFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                var ttStr = tt.GetString();
                if (string.IsNullOrWhiteSpace(ttStr)) continue;
                var normalized = ttStr.Trim().Replace(" ", "").Replace("_", "");
                if (!string.Equals(normalized, "transactionid", StringComparison.OrdinalIgnoreCase)) continue;
                var dlieu = item.TryGetProperty("dlieu", out var dl) ? dl.GetString() : null;
                if (string.IsNullOrWhiteSpace(dlieu) && item.TryGetProperty("dLieu", out var dL))
                    dlieu = dL.GetString();
                return string.IsNullOrWhiteSpace(dlieu) ? null : dlieu.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
