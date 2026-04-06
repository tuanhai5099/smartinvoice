using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ Vininvoice (MST nhà cung cấp 0109282176).
/// API: https://tracuu.vininvoice.vn/erp/rest/s1/iam-entry/invoices/{id}/pdf
/// Trong đó {id} là GUID đọc từ trường MCCQT (hoặc tương đương) trong JSON payload.
/// </summary>
[InvoiceProvider("0109282176", InvoiceProviderMatchKind.ProviderTaxCode)]
public sealed class VininvoiceInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (Vininvoice).</summary>
    public string ProviderKey => "0109282176";

    private const string DownloadUrlTemplate = "https://tracuu.vininvoice.vn/erp/rest/s1/iam-entry/invoices/{0}/pdf";

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public VininvoiceInvoicePdfFetcher(HttpClient httpClient, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = loggerFactory.CreateLogger(nameof(VininvoiceInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var id = GetInvoiceIdFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Vininvoice PDF: payload không tìm thấy MCCQT/id hóa đơn.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu MCCQT (id hóa đơn trên cổng Vininvoice). Không thể tải PDF.");
        }

        var url = string.Format(DownloadUrlTemplate, Uri.EscapeDataString(id.Trim()));
        try
        {
            _logger.LogDebug("Vininvoice PDF: GET {Url}", url);
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return new InvoicePdfResult.Failure("Phản hồi từ Vininvoice rỗng. Kiểm tra MCCQT hoặc thử lại sau.");
            }

            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileName = "invoice.pdf";

            _logger.LogInformation("Vininvoice PDF: đã tải {File} ({Size} bytes).", fileName, bytes.Length);
            return new InvoicePdfResult.Success(bytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Vininvoice PDF: lỗi HTTP khi GET {Url}", url);
            return new InvoicePdfResult.Failure("Lỗi kết nối Vininvoice: " + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vininvoice PDF: lỗi khi lấy PDF với MCCQT.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF: " + ex.Message);
        }
    }

    private static string? GetInvoiceIdFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                // 1) Ưu tiên field MCCQT / mcCQT trong cttkhac/ttkhac
                var fromCttkhac = GetIdFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();

                var fromTtkhac = GetIdFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return fromTtkhac.Trim();

                // 2) Thử trực tiếp field MCCQT / id / invoiceId ở root
                var direct = GetIdFromDirectFields(candidate);
                if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> GetInvoiceRootCandidates(JsonElement r)
    {
        yield return r;
        if (r.ValueKind != JsonValueKind.Object) yield break;
        if (r.TryGetProperty("ndhdon", out var ndhdon) && ndhdon.ValueKind == JsonValueKind.Object)
            yield return ndhdon;
        if (r.TryGetProperty("hdon", out var hdon) && hdon.ValueKind == JsonValueKind.Object)
            yield return hdon;
        if (r.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
                yield return data;
            else if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                yield return data[0];
        }
    }

    private static bool IsMccqtFieldName(string name)
    {
        var norm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(name);
        return norm.Contains("mccqt", StringComparison.Ordinal)
               || norm.Contains("mahoadoncoquanthu", StringComparison.Ordinal)
               || norm.Contains("invoiceid", StringComparison.Ordinal);
    }

    private static string? GetIdFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
            var ttStr = tt.GetString();
            if (string.IsNullOrWhiteSpace(ttStr) || !IsMccqtFieldName(ttStr)) continue;

            var dlieu = item.TryGetProperty("dlieu", out var dl) ? dl.GetString() : null;
            if (string.IsNullOrWhiteSpace(dlieu) && item.TryGetProperty("dLieu", out var dL))
                dlieu = dL.GetString();
            if (!string.IsNullOrWhiteSpace(dlieu)) return dlieu;
        }
        return null;
    }

    private static string? GetIdFromTtkhac(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind != JsonValueKind.Object || !outer.TryGetProperty("ttchung", out var ttchung))
                continue;

            if (ttchung.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ttchung.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(tt) || !IsMccqtFieldName(tt)) continue;

                    var dl = item.TryGetProperty("dlieu", out var dlEl) ? dlEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(dl) && item.TryGetProperty("dLieu", out var dL))
                        dl = dL.GetString();
                    if (!string.IsNullOrWhiteSpace(dl)) return dl;
                }
            }
            else if (ttchung.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in ttchung.EnumerateObject())
                {
                    if (!IsMccqtFieldName(prop.Name)) continue;
                    var val = prop.Value;
                    if (val.ValueKind == JsonValueKind.String)
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                    if (val.ValueKind == JsonValueKind.Object)
                    {
                        var s = val.TryGetProperty("dlieu", out var dl) && dl.ValueKind == JsonValueKind.String
                            ? dl.GetString()
                            : (val.TryGetProperty("dLieu", out var dL) && dL.ValueKind == JsonValueKind.String ? dL.GetString() : null);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
        }
        return null;
    }

    private static string? GetIdFromDirectFields(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in r.EnumerateObject())
        {
            if (!IsMccqtFieldName(prop.Name)) continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var s = prop.Value.TryGetProperty("dlieu", out var dl) && dl.ValueKind == JsonValueKind.String
                    ? dl.GetString()
                    : (prop.Value.TryGetProperty("dLieu", out var dL) && dL.ValueKind == JsonValueKind.String ? dL.GetString() : null);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }
}

