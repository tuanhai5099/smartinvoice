using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Core;

namespace SmartInvoice.InvoicePdfFetchers;

// Viettel API trả JSON camelCase; dùng option case-insensitive khi deserialize.
internal static class ViettelJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>
/// Lấy PDF hóa đơn từ Viettel (vinvoice.viettel.vn): gọi API generate captcha → dùng offsetX từ response → verify → download PDF.
/// Payload: supplierTaxCode = nbmst (mã số thuế), reservationCode = value của cttkhac có ttruong "Mã số bí mật".
/// </summary>
public sealed class ViettelInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp Viettel (NCC).</summary>
    public string ProviderKey => "0100109106";

    private const string BaseUrl = "https://vinvoice.viettel.vn";
    private const string CaptchaGeneratePath = "/api/services/einvoiceuaa/api/captcha/generate";
    private const string CaptchaVerifyPath = "/api/services/einvoiceuaa/api/captcha/verify";
    private const string DownloadPdfPath = "/api/services/einvoicequery/sync/utility/downloadPDF";

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public ViettelInvoicePdfFetcher(HttpClient httpClient, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = loggerFactory?.CreateLogger(nameof(ViettelInvoicePdfFetcher)) ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new InvoicePdfResult.Failure("Payload hóa đơn trống. Cần nbmst và mã số bí mật (cttkhac) để tải PDF Viettel.");

        if (!TryParsePayload(payloadJson, out var stc, out var res) || string.IsNullOrWhiteSpace(stc) || string.IsNullOrWhiteSpace(res))
            return new InvoicePdfResult.Failure("Payload thiếu nbmst hoặc mã số bí mật (cttkhac có ttruong \"Mã số bí mật\"). Không thể tải PDF Viettel.");

        var supplierTaxCode = stc!;
        var reservationCode = res!;

        try
        {
            // 1. GET captcha/generate
            _logger.LogDebug("Viettel PDF: GET captcha generate.");
            var generateUrl = BaseUrl + CaptchaGeneratePath;
            using var generateResponse = await _httpClient.GetAsync(generateUrl, cancellationToken).ConfigureAwait(false);
            generateResponse.EnsureSuccessStatusCode();
            var generateJson = await generateResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var generate = JsonSerializer.Deserialize<ViettelCaptchaGenerateResponse>(generateJson, ViettelJson.Options);
            if (generate?.Token == null)
            {
                return new InvoicePdfResult.Failure("Phản hồi generate captcha Viettel không hợp lệ (thiếu token).");
            }

            // 2. Dùng offsetX từ response generate (API đã trả sẵn)
            var offsetX = generate.OffsetX;
            _logger.LogDebug("Viettel PDF: POST captcha verify với offsetX={OffsetX} từ generate.", offsetX);

            // 3. POST captcha/verify
            var verifyUrl = BaseUrl + CaptchaVerifyPath;
            var verifyBody = new { token = generate.Token, offsetX };
            using var verifyResponse = await _httpClient.PostAsJsonAsync(verifyUrl, verifyBody, cancellationToken).ConfigureAwait(false);
            verifyResponse.EnsureSuccessStatusCode();
            var verify = await verifyResponse.Content.ReadFromJsonAsync<ViettelCaptchaVerifyResponse>(ViettelJson.Options, cancellationToken).ConfigureAwait(false);
            if (verify?.Success != true || string.IsNullOrWhiteSpace(verify.Token))
            {
                return new InvoicePdfResult.Failure("Xác thực captcha Viettel thất bại hoặc không trả về token.");
            }

            // 4. POST download PDF
            var downloadUrl = $"{BaseUrl}{DownloadPdfPath}?taxCode={Uri.EscapeDataString(supplierTaxCode)}";
            var downloadBody = new
            {
                supplierTaxCode,
                reservationCode,
                recaptcha = verify.Token
            };
            _logger.LogDebug("Viettel PDF: POST download PDF.");
            using var downloadResponse = await _httpClient.PostAsJsonAsync(downloadUrl, downloadBody, cancellationToken).ConfigureAwait(false);
            downloadResponse.EnsureSuccessStatusCode();
            var pdfBytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                return new InvoicePdfResult.Failure("Phản hồi tải PDF Viettel rỗng.");
            }

            var suggestedFileName = GetSuggestedFileNameFromPayload(payloadJson);
            if (string.IsNullOrWhiteSpace(suggestedFileName) || !suggestedFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                suggestedFileName = downloadResponse.Content.Headers.ContentDisposition?.FileName?.Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(suggestedFileName) || !suggestedFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                suggestedFileName = "Invoice.pdf";

            _logger.LogInformation("Viettel PDF: đã tải {File} ({Size} bytes).", suggestedFileName, pdfBytes.Length);
            return new InvoicePdfResult.Success(pdfBytes, suggestedFileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Viettel PDF: lỗi HTTP.");
            return new InvoicePdfResult.Failure("Lỗi kết nối Viettel: " + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Viettel PDF: lỗi.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF: " + ex.Message);
        }
    }

    /// <summary>Lấy suggested file name đồng bộ hệ thống: {KyHieu}-{SoHoaDon}.pdf từ payload (khhdon, shdon).</summary>
    private static string? GetSuggestedFileNameFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            var kh = r.TryGetProperty("khhdon", out var khProp) ? khProp.GetString()?.Trim() : null;
            var sh = r.TryGetProperty("shdon", out var shProp) ? shProp.GetInt32() : (int?)null;
            if (string.IsNullOrEmpty(kh) || sh == null) return null;
            var baseName = $"{SanitizeFileName(kh)}-{sh.Value}.pdf";
            return baseName;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>Parse payload: supplierTaxCode = nbmst, reservationCode từ cttkhac hoặc ttkhac&gt;ttchung (ttruong "Mã số bí mật"). Thử nhiều root (ndhdon, hdon, data) khi payload là response chi tiết.</summary>
    private static bool TryParsePayload(string payloadJson, out string? supplierTaxCode, out string? reservationCode)
    {
        supplierTaxCode = null;
        reservationCode = null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                if (string.IsNullOrWhiteSpace(supplierTaxCode) && candidate.TryGetProperty("nbmst", out var nbmstProp))
                {
                    var nbmst = nbmstProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(nbmst))
                        supplierTaxCode = nbmst;
                }
                if (string.IsNullOrWhiteSpace(reservationCode))
                    reservationCode = GetMaSoBiMatFromPayload(candidate);
                if (!string.IsNullOrWhiteSpace(supplierTaxCode) && !string.IsNullOrWhiteSpace(reservationCode))
                    return true;
            }
            return !string.IsNullOrWhiteSpace(supplierTaxCode) && !string.IsNullOrWhiteSpace(reservationCode);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Trả về các node có thể chứa nbmst/cttkhac/ttkhac: bản thân r, r.ndhdon, r.hdon, r.data (hoặc phần tử đầu data).</summary>
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

    /// <summary>Lấy mã số bí mật: ưu tiên cttkhac (ttruong "Mã số bí mật"), fallback ttkhac[].ttchung (MST 0100109106 thường đặt ở đây).</summary>
    private static string? GetMaSoBiMatFromPayload(JsonElement r)
    {
        var fromCttkhac = GetMaSoBiMatFromCttkhac(r);
        if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();
        return GetMaSoBiMatFromTtkhacTtchung(r);
    }

    private static string? GetMaSoBiMatFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(tt) || !IsMaSoBiMatTtruong(tt)) continue;
            var dl = item.TryGetProperty("dlieu", out var d) ? d.GetString() : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
            if (!string.IsNullOrWhiteSpace(dl)) return dl.Trim();
            break;
        }
        return null;
    }

    /// <summary>Đọc mã số bí mật từ ttkhac[].ttchung (một số payload Viettel MST 0100109106 đặt ở đây).</summary>
    private static string? GetMaSoBiMatFromTtkhacTtchung(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind != JsonValueKind.Object || !outer.TryGetProperty("ttchung", out var ttchung)) continue;
            var v = GetMaSoBiMatFromTtchungElement(ttchung);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static string? GetMaSoBiMatFromTtchungElement(JsonElement ttchung)
    {
        if (ttchung.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ttchung.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!IsMaSoBiMatTtruong(item.TryGetProperty("ttruong", out var t) ? t.GetString()?.Trim() : null)) continue;
                var dl = item.TryGetProperty("dlieu", out var d) ? d.GetString() : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                if (!string.IsNullOrWhiteSpace(dl)) return dl.Trim();
            }
        }
        else if (ttchung.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ttchung.EnumerateObject())
            {
                if (!IsMaSoBiMatFieldName(prop.Name)) continue;
                var val = prop.Value;
                var s = val.ValueKind == JsonValueKind.String ? val.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(s) && val.ValueKind == JsonValueKind.Object)
                    s = (val.TryGetProperty("dlieu", out var d) ? d.GetString() : (val.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null))?.Trim();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private const string MaSoBiMatCanonical = "masobimat";

    private static bool IsMaSoBiMatTtruong(string? ttruong)
    {
        var n = StringNormalization.NormalizeForComparison(ttruong);
        return n == MaSoBiMatCanonical || n.Contains(MaSoBiMatCanonical, StringComparison.Ordinal);
    }

    private static bool IsMaSoBiMatFieldName(string name)
    {
        var n = StringNormalization.NormalizeForComparison(name);
        return n == MaSoBiMatCanonical || n.Contains(MaSoBiMatCanonical, StringComparison.Ordinal)
            || n == "reservationcode" || n.Contains("reservationcode", StringComparison.Ordinal);
    }

    private sealed class ViettelCaptchaGenerateResponse
    {
        public string? BackgroundUrl { get; set; }
        public string? PuzzleUrl { get; set; }
        public string? Token { get; set; }
        public int OffsetY { get; set; }
        public int OffsetX { get; set; }
        public int SlideCaptchaOffsetMargin { get; set; }
    }

    private sealed class ViettelCaptchaVerifyResponse
    {
        public bool Success { get; set; }
        public string? Token { get; set; }
        public string? Message { get; set; }
    }
}
