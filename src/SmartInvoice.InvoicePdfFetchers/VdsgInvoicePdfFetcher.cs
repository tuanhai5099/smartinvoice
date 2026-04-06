using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ cổng VDSG (Viễn Thông Đông Sài Gòn, MST 0314058603).
/// URL tải PDF: https://portal.vdsg-invoice.vn/invoice/download/{id}?type=pdf
/// Trong đó {id} lấy từ trường "MTCuu" (hoặc tương đương) trong ttkhac/cttkhac của payload JSON.
/// </summary>
[InvoiceProvider("0314058603", InvoiceProviderMatchKind.ProviderTaxCode)]
public sealed class VdsgInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (Viễn Thông Đông Sài Gòn).</summary>
    public string ProviderKey => "0314058603";

    private const string DownloadUrlTemplate = "https://portal.vdsg-invoice.vn/invoice/download/{0}?type=pdf";

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public VdsgInvoicePdfFetcher(HttpClient httpClient, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = loggerFactory.CreateLogger(nameof(VdsgInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var mtCuu = GetMtCuuFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(mtCuu))
        {
            _logger.LogWarning("VDSG PDF: payload không tìm thấy trường MTCuu trong ttkhac/cttkhac.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu mã tra cứu (MTCuu) trong ttkhac/cttkhac. Không thể tải PDF từ cổng VDSG.");
        }

        var url = string.Format(DownloadUrlTemplate, Uri.EscapeDataString(mtCuu.Trim()));
        try
        {
            _logger.LogDebug("VDSG PDF: GET {Url}", url);
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return new InvoicePdfResult.Failure("Phản hồi từ cổng VDSG rỗng. Kiểm tra mã tra cứu MTCuu hoặc thử lại sau.");
            }

            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileName = "invoice.pdf";

            _logger.LogInformation("VDSG PDF: đã tải {File} ({Size} bytes).", fileName, bytes.Length);
            return new InvoicePdfResult.Success(bytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "VDSG PDF: lỗi HTTP khi GET {Url}", url);
            return new InvoicePdfResult.Failure("Lỗi kết nối cổng VDSG: " + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VDSG PDF: lỗi khi lấy PDF với MTCuu.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF: " + ex.Message);
        }
    }

    /// <summary>
/// Tìm giá trị MTCuu trong dữ liệu hóa đơn:
/// - Nếu chuỗi là XML: đọc TTKhac/TTin[TTruong="MTCuu"]/DLieu trong TTChung (theo cấu trúc HDon → DLHDon → TTChung → TTKhac → TTin).
/// - Nếu không phải XML: fallback sang JSON, duyệt các root candidate (r, ndhdon, hdon, data[0]) và ttkhac/cttkhac/field trực tiếp.
/// - So khớp tên trường theo NormalizeForComparison("MTCuu") → "mtcuu".
    /// </summary>
    private static string? GetMtCuuFromPayload(string payloadOrXml)
    {
        if (string.IsNullOrWhiteSpace(payloadOrXml)) return null;

        // 1) Nếu là XML: ưu tiên đọc theo cấu trúc chuẩn HDon/DLHDon/TTChung/TTKhac/TTin.
        var trimmed = payloadOrXml.Trim();
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            try
            {
                var xdoc = XDocument.Parse(trimmed);
                // Tìm mọi TTin trong TTKhac của TTChung.
                var tins = xdoc
                    .Descendants()
                    .Where(e => string.Equals(e.Name.LocalName, "TTKhac", StringComparison.OrdinalIgnoreCase))
                    .Elements()
                    .Where(e => string.Equals(e.Name.LocalName, "TTin", StringComparison.OrdinalIgnoreCase));

                foreach (var tin in tins)
                {
                    var ttruong = tin.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "TTruong", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (string.IsNullOrWhiteSpace(ttruong)) continue;
                    var norm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(ttruong);
                    if (!norm.Contains("mtcuu", StringComparison.Ordinal) &&
                        !norm.Contains("matracuu", StringComparison.Ordinal))
                        continue;

                    var dlieu = tin.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "DLieu", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (!string.IsNullOrWhiteSpace(dlieu))
                        return dlieu.Trim();
                }
            }
            catch
            {
                // Nếu XML lỗi, fallback JSON bên dưới
            }
        }

        // 2) Fallback JSON: tìm trong ttkhac/cttkhac/field trực tiếp.
        try
        {
            using var doc = JsonDocument.Parse(payloadOrXml);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                // 1) Thử trong ttkhac.ttchung / ttkhac[]
                var fromTtkhac = GetMtCuuFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return fromTtkhac.Trim();

                // 2) Thử trong cttkhac[]
                var fromCttkhac = GetMtCuuFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();

                // 3) Thử trực tiếp các field tên MTCuu/MaTraCuu/LookupCode
                var direct = GetMtCuuFromDirectFields(candidate);
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

    private const string MtCuuCanonical = "mtcuu";

    private static bool IsMtCuuName(string name)
    {
        var n = SmartInvoice.Core.StringNormalization.NormalizeForComparison(name);
        return n == MtCuuCanonical
               || n.Contains(MtCuuCanonical, StringComparison.Ordinal)
               || n.Contains("matracuu", StringComparison.Ordinal)
               || n.Contains("lookupcode", StringComparison.Ordinal);
    }

    private static string? GetMtCuuFromTtkhac(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind == JsonValueKind.Object && outer.TryGetProperty("ttchung", out var ttchung))
            {
                var v = GetMtCuuFromTtchung(ttchung);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            else
            {
                // ttkhac item dạng cttkhac-like: có ttruong / dlieu
                var v = GetMtCuuFromCttkhacLikeItem(outer);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        return null;
    }

    private static string? GetMtCuuFromTtchung(JsonElement ttchung)
    {
        if (ttchung.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ttchung.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tt) || !IsMtCuuName(tt)) continue;
                var dl = TryGetDataValue(item);
                if (!string.IsNullOrWhiteSpace(dl)) return dl;
            }
        }
        else if (ttchung.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ttchung.EnumerateObject())
            {
                if (!IsMtCuuName(prop.Name)) continue;
                var val = prop.Value;
                if (val.ValueKind == JsonValueKind.String)
                {
                    var s = val.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (val.ValueKind == JsonValueKind.Object)
                {
                    var s = TryGetDataValue(val);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        return null;
    }

    private static string? GetMtCuuFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            var v = GetMtCuuFromCttkhacLikeItem(item);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static string? GetMtCuuFromCttkhacLikeItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tt) || !IsMtCuuName(tt)) return null;
        var dl = TryGetDataValue(item);
        return string.IsNullOrWhiteSpace(dl) ? null : dl;
    }

    private static string? GetMtCuuFromDirectFields(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in r.EnumerateObject())
        {
            if (!IsMtCuuName(prop.Name)) continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var s = TryGetDataValue(prop.Value);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    /// <summary>Lấy giá trị thực tế từ object: ưu tiên dlieu/dLieu, sau đó giatri/value, cuối cùng property string đầu tiên khác ttruong.</summary>
    private static string? TryGetDataValue(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

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

