using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Download PDF cho Grab (vn.einvoice.grab.com) bằng query parameter <c>Fkey</c>.
/// - Match theo MST người bán: <c>nbmst</c> = 0312650437 (grab).
/// - Lấy <c>Fkey</c> từ <c>invoice payload JSON</c>:
///   + ưu tiên cttkhac[] có ttruong="Fkey"/"Fkey..." rồi lấy value (dlieu/dLieu/...)
///   + fallback ttkhac[] (tương tự) và ttkhac[].ttchung.Fkey.
/// - Download URL: https://vn.einvoice.grab.com/Invoice/DowloadPdf?Fkey={Fkey}
/// </summary>
[InvoiceProvider("0312650437", InvoiceProviderMatchKind.SellerTaxCode, MayRequireUserIntervention = true)]
[InvoiceProvider("312650437", InvoiceProviderMatchKind.SellerTaxCode, MayRequireUserIntervention = true)]
public sealed class GrabInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    public string ProviderKey => "0312650437";

    private const string BaseUrl = "https://vn.einvoice.grab.com";
    private const string DownloadPdfPath = "/Invoice/DowloadPdf"; // giữ nguyên spelling từ yêu cầu

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public GrabInvoicePdfFetcher(HttpClient httpClient, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = loggerFactory?.CreateLogger(nameof(GrabInvoicePdfFetcher))
                  ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new InvoicePdfResult.Failure("Payload hóa đơn trống. Không thể lấy Fkey để download PDF Grab.");

        var fkey = GetFkeyFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(fkey))
        {
            return new InvoicePdfResult.Failure(
                "Payload hóa đơn thiếu trường Fkey (không tìm thấy trong cttkhac/ttkhac). Không thể tải PDF Grab.");
        }

        var downloadUrl = $"{BaseUrl}{DownloadPdfPath}?Fkey={Uri.EscapeDataString(fkey.Trim())}";
        try
        {
            _logger.LogDebug("Grab PDF: GET {Url}", downloadUrl);

            using var resp = await _httpClient.GetAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var pdfBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (pdfBytes == null || pdfBytes.Length == 0)
                return new InvoicePdfResult.Failure("Phản hồi tải PDF Grab rỗng.");

            // Best-effort kiểm tra header để tránh lưu nhầm HTML lỗi.
            if (pdfBytes.Length >= 5)
            {
                var header = Encoding.ASCII.GetString(pdfBytes.AsSpan(0, 5));
                if (!header.StartsWith("%PDF-", StringComparison.Ordinal))
                    return new InvoicePdfResult.Failure("Phản hồi từ Grab không phải PDF hợp lệ (thiếu %PDF- header).");
            }

            return new InvoicePdfResult.Success(pdfBytes, "grab-invoice.pdf");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Da huy lay PDF Grab.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Grab PDF: lỗi HTTP khi GET.");
            return new InvoicePdfResult.Failure("Loi ket noi Grab: " + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Grab PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Loi lay PDF Grab: " + ex.Message);
        }
    }

    private static string? GetFkeyFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var direct = GetFkeyFromDirectFields(candidate);
                if (!string.IsNullOrWhiteSpace(direct)) return direct;

                var fromCttkhac = GetFkeyFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac;

                var fromTtkhac = GetFkeyFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return fromTtkhac;
            }
        }
        catch
        {
            // ignore parse errors
        }

        return null;
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

    private static string? GetFkeyFromDirectFields(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;

        // Trường hợp payload có Fkey trực tiếp ở root.
        if (r.TryGetProperty("Fkey", out var fkeyProp))
        {
            var s = fkeyProp.ValueKind == JsonValueKind.String ? fkeyProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }

        // Một số payload đặt key dưới dạng property object (không chuẩn).
        foreach (var prop in r.EnumerateObject())
        {
            var nameNorm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(prop.Name);
            if (!nameNorm.Contains("fkey", StringComparison.Ordinal)) continue;

            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var s = TryGetDataValue(prop.Value);
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }

        return null;
    }

    private static string? GetFkeyFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            // Chuẩn: { ttruong: "Fkey", dlieu: "..." }
            if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
            {
                var ttStr = tt.GetString();
                if (!string.IsNullOrWhiteSpace(ttStr) && IsFkeyFieldName(ttStr))
                {
                    var v = TryGetDataValue(item);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }

            // Fkey có thể nằm trực tiếp trong item (không cần ttruong).
            foreach (var prop in item.EnumerateObject())
            {
                var nameNorm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(prop.Name);
                if (!nameNorm.Contains("fkey", StringComparison.Ordinal)) continue;

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var s = TryGetDataValue(prop.Value);
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
        }

        return null;
    }

    private static string? GetFkeyFromTtkhac(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            // Kiểu A: item có ttruong + dlieu/dLieu
            if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
            {
                var ttStr = tt.GetString();
                if (!string.IsNullOrWhiteSpace(ttStr) && IsFkeyFieldName(ttStr))
                {
                    var v = TryGetDataValue(item);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }

            // Kiểu B: item.ttchung có property Fkey
            if (item.TryGetProperty("ttchung", out var ttchung))
            {
                var fromTtchung = GetFkeyFromTtchung(ttchung);
                if (!string.IsNullOrWhiteSpace(fromTtchung)) return fromTtchung;
            }

            // Kiểu C: item chứa trực tiếp property name chứa fkey
            foreach (var prop in item.EnumerateObject())
            {
                var nameNorm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(prop.Name);
                if (!nameNorm.Contains("fkey", StringComparison.Ordinal)) continue;

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var s = TryGetDataValue(prop.Value);
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
        }

        return null;
    }

    private static string? GetFkeyFromTtchung(JsonElement ttchung)
    {
        if (ttchung.ValueKind == JsonValueKind.Object)
        {
            // ttchung.Fkey
            if (ttchung.TryGetProperty("Fkey", out var fkeyProp))
            {
                var s = fkeyProp.ValueKind == JsonValueKind.String ? fkeyProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }

            // ttchung có key khác tên nhưng chứa "fkey"
            foreach (var prop in ttchung.EnumerateObject())
            {
                var nameNorm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(prop.Name);
                if (!nameNorm.Contains("fkey", StringComparison.Ordinal)) continue;

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var s = TryGetDataValue(prop.Value);
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
        }
        else if (ttchung.ValueKind == JsonValueKind.Array)
        {
            // Một số payload ttchung là mảng các item { ttruong, dlieu... }
            foreach (var item in ttchung.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
                {
                    var ttStr = tt.GetString();
                    if (!string.IsNullOrWhiteSpace(ttStr) && IsFkeyFieldName(ttStr))
                    {
                        var v = TryGetDataValue(item);
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                }

                foreach (var prop in item.EnumerateObject())
                {
                    var nameNorm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(prop.Name);
                    if (!nameNorm.Contains("fkey", StringComparison.Ordinal)) continue;

                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var s = TryGetDataValue(prop.Value);
                        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                    }
                }
            }
        }

        return null;
    }

    private static bool IsFkeyFieldName(string? ttruong)
    {
        if (string.IsNullOrWhiteSpace(ttruong)) return false;
        var n = SmartInvoice.Core.StringNormalization.NormalizeForComparison(ttruong);
        return n == "fkey" || n.Contains("fkey", StringComparison.Ordinal);
    }

    private static string? TryGetDataValue(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

        // Theo chuẩn thường gặp: dlieu/dLieu.
        if (obj.TryGetProperty("dlieu", out var d) && d.ValueKind == JsonValueKind.String)
        {
            var s = d.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        if (obj.TryGetProperty("dLieu", out var dL) && dL.ValueKind == JsonValueKind.String)
        {
            var s = dL.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }

        // Các biến thể dữ liệu khác.
        if (obj.TryGetProperty("giatri", out var gt) && gt.ValueKind == JsonValueKind.String)
        {
            var s = gt.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        if (obj.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }

        // Fallback: lấy string property đầu tiên khác "ttruong".
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, "ttruong", StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }

        return null;
    }
}

