using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ MyInvoice (NCC 0108971656) bằng MCCQT trong XML.
/// URL: https://tracuu.myinvoice.vn/erp/rest/s1//iam-entry/invoices/{MCCQT}/pdf
/// </summary>
[InvoiceProvider("0108971656", InvoiceProviderMatchKind.ProviderTaxCode)]
public sealed class MyinvoiceInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    public string ProviderKey => "0108971656";

    private const string DownloadUrlTemplate = "https://tracuu.myinvoice.vn/erp/rest/s1//iam-entry/invoices/{0}/pdf";

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public MyinvoiceInvoicePdfFetcher(HttpClient httpClient, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = loggerFactory.CreateLogger(nameof(MyinvoiceInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var mccqt = TryGetMccqtFromXml(payloadJson);
        if (string.IsNullOrWhiteSpace(mccqt))
            return new InvoicePdfResult.Failure("Không tìm thấy MCCQT trong XML. Không thể tải PDF MyInvoice.");

        var url = string.Format(DownloadUrlTemplate, Uri.EscapeDataString(mccqt.Trim()));
        try
        {
            _logger.LogDebug("Myinvoice PDF: GET {Url}", url);
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return new InvoicePdfResult.Failure("Phản hồi PDF từ MyInvoice rỗng.");

            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileName = $"Myinvoice-{mccqt}.pdf";

            return new InvoicePdfResult.Success(bytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF MyInvoice.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Myinvoice PDF: lỗi HTTP khi tải với MCCQT={Mccqt}", mccqt);
            return new InvoicePdfResult.Failure("Lỗi kết nối MyInvoice: " + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Myinvoice PDF: lỗi khi tải với MCCQT={Mccqt}", mccqt);
            return new InvoicePdfResult.Failure("Lỗi lấy PDF MyInvoice: " + ex.Message);
        }
    }

    private static string? TryGetMccqtFromXml(string xmlPayload)
    {
        if (string.IsNullOrWhiteSpace(xmlPayload))
            return null;

        try
        {
            var doc = XDocument.Parse(xmlPayload, LoadOptions.PreserveWhitespace);
            var value = doc
                .Descendants()
                .FirstOrDefault(x => string.Equals(x.Name.LocalName, "MCCQT", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        catch
        {
            // not XML -> continue best effort for JSON-wrapped XML if needed
        }

        try
        {
            using var json = JsonDocument.Parse(xmlPayload);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("MCCQT", out var mccqt) &&
                mccqt.ValueKind == JsonValueKind.String)
            {
                return mccqt.GetString()?.Trim();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
