using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Exceptions;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.HoaDonDienTu;

public class HoaDonDienTuApiClient : IHoaDonDienTuApiClient
{
    private const string CaptchaUrl = "https://hoadondientu.gdt.gov.vn:30000/captcha";
    private const string LoginUrl = "https://hoadondientu.gdt.gov.vn:30000/security-taxpayer/authenticate";
    private const string RefreshUrl = "https://hoadondientu.gdt.gov.vn:30000/security-taxpayer/refresh";
    private const string ProfileUrl = "https://hoadondientu.gdt.gov.vn:30000/security-taxpayer/profile";

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public HoaDonDienTuApiClient(HttpClient httpClient, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient;
        _logger = loggerFactory.CreateLogger(nameof(HoaDonDienTuApiClient));
    }

    public async Task<CaptchaResponse> GetCaptchaAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(CaptchaUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var raw = JsonSerializer.Deserialize<CaptchaApiResponse>(json);
        if (raw == null || string.IsNullOrEmpty(raw.Key))
            throw new InvalidOperationException("Invalid captcha response.");
        return new CaptchaResponse(raw.Key, raw.Content ?? string.Empty);
    }

    public async Task<AuthenticateResponse> AuthenticateAsync(string username, string password, string captchaKey, string captchaValue, CancellationToken cancellationToken = default)
    {
        var body = new { username, password, ckey = captchaKey, cvalue = captchaValue };
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(LoginUrl, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Authenticate request failed");
            return new AuthenticateResponse(false, null, null, ex.Message);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var auth = JsonSerializer.Deserialize<AuthenticateApiResponse>(responseJson);
        if (auth == null)
            return new AuthenticateResponse(false, null, null, "Invalid response.");

        if (!response.IsSuccessStatusCode)
            return new AuthenticateResponse(false, null, null, auth.Message ?? response.ReasonPhrase);

        // API có thể trả refreshToken (camelCase) hoặc refresh_token (snake_case)
        var refreshToken = !string.IsNullOrEmpty(auth.RefreshToken) ? auth.RefreshToken : auth.RefreshTokenSnake;
        return new AuthenticateResponse(
            !string.IsNullOrEmpty(auth.Token),
            auth.Token,
            refreshToken,
            auth.Message
        );
    }

    public async Task<AuthenticateResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return new AuthenticateResponse(false, null, null, "Refresh token trống.");
        var body = new { refreshToken };
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            var response = await _httpClient.PostAsync(RefreshUrl, content, cancellationToken).ConfigureAwait(false);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var auth = JsonSerializer.Deserialize<AuthenticateApiResponse>(responseJson);
            if (auth == null)
                return new AuthenticateResponse(false, null, null, "Invalid response.");
            if (!response.IsSuccessStatusCode)
                return new AuthenticateResponse(false, null, null, auth.Message ?? response.ReasonPhrase);
            var newRefresh = !string.IsNullOrEmpty(auth.RefreshToken) ? auth.RefreshToken : auth.RefreshTokenSnake;
            return new AuthenticateResponse(
                !string.IsNullOrEmpty(auth.Token),
                auth.Token,
                newRefresh,
                auth.Message
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshToken request failed");
            return new AuthenticateResponse(false, null, null, ex.Message);
        }
    }

    public async Task<ProfileResponse> GetProfileAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize<ProfileApiResponse>(json);
        return new ProfileResponse(profile?.Name, profile?.TaxCode);
    }

    private class CaptchaApiResponse
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private class AuthenticateApiResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }
        [JsonPropertyName("refresh_token")]
        public string? RefreshTokenSnake { get; set; }
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private class ProfileApiResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("taxCode")]
        public string? TaxCode { get; set; }
    }

    private const string SystemTaxpayerUsersUrl = "https://hoadondientu.gdt.gov.vn:30000/system-taxpayer/users?size=15&sort=username:desc";

    public async Task<string?> GetSystemTaxpayerUsersFirstItemJsonAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SystemTaxpayerUsersUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("datas", out var datas) || datas.ValueKind != JsonValueKind.Array)
            return null;
        var count = datas.GetArrayLength();
        if (count == 0) return null;
        var first = datas[0];
        return first.GetRawText();
    }

    private const string InvoicesSoldUrl = "https://hoadondientu.gdt.gov.vn:30000/query/invoices/sold";
    private const string InvoicesSoldScoUrl = "https://hoadondientu.gdt.gov.vn:30000/sco-query/invoices/sold";
    private const string InvoicesPurchaseUrl = "https://hoadondientu.gdt.gov.vn:30000/query/invoices/purchase";
    private const string InvoicesPurchaseScoUrl = "https://hoadondientu.gdt.gov.vn:30000/sco-query/invoices/purchase";
    private const string InvoiceDetailUrl = "https://hoadondientu.gdt.gov.vn:30000/query/invoices/detail";
    private const string InvoiceDetailScoUrl = "https://hoadondientu.gdt.gov.vn:30000/sco-query/invoices/detail";
    private const string InvoiceRelativeUrl = "https://hoadondientu.gdt.gov.vn:30000/query/invoices/relative";
    private const string InvoiceRelativeScoUrl = "https://hoadondientu.gdt.gov.vn:30000/sco-query/invoices/relative";
    private const string InvoiceExportXmlUrl = "https://hoadondientu.gdt.gov.vn:30000/query/invoices/export-xml";
    private const string InvoiceScoExportXmlUrl = "https://hoadondientu.gdt.gov.vn:30000/sco-query/invoices/export-xml";
    private static readonly string DateFormat = "dd/MM/yyyy";

    public async Task<InvoiceListApiResponse?> GetInvoicesSoldAsync(string accessToken, DateTime fromDate, DateTime toDate, string? state, int size = 50, CancellationToken cancellationToken = default)
    {
        var url = BuildInvoicesListUrl(InvoicesSoldUrl, fromDate, toDate, state, size);
        return await GetInvoicesListAsync(accessToken, url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvoiceListApiResponse?> GetInvoicesSoldScoAsync(string accessToken, DateTime fromDate, DateTime toDate, string? state, int size = 50, CancellationToken cancellationToken = default)
    {
        var url = BuildInvoicesListUrl(InvoicesSoldScoUrl, fromDate, toDate, state, size);
        return await GetInvoicesListAsync(accessToken, url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvoiceListApiResponse?> GetInvoicesPurchaseAsync(string accessToken, DateTime fromDate, DateTime toDate, string? state, int size = 50, CancellationToken cancellationToken = default)
    {
        var url = BuildInvoicesListUrl(InvoicesPurchaseUrl, fromDate, toDate, state, size);
        return await GetInvoicesListAsync(accessToken, url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvoiceListApiResponse?> GetInvoicesPurchaseScoAsync(string accessToken, DateTime fromDate, DateTime toDate, string? state, int size = 50, CancellationToken cancellationToken = default)
    {
        var url = BuildInvoicesListUrl(InvoicesPurchaseScoUrl, fromDate, toDate, state, size);
        return await GetInvoicesListAsync(accessToken, url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Build search: API chỉ chấp nhận 1 tháng/lần; tham số phải có thời gian (theo reference VLKCrawlData: dd/MM/yyyyT00:00:00 và T23:59:59).
    /// </summary>
    private static string BuildInvoicesListUrl(string baseUrl, DateTime fromDate, DateTime toDate, string? state, int size)
    {
        var search = $"tdlap=ge={fromDate.ToString(DateFormat)}T00:00:00;tdlap=le={toDate.ToString(DateFormat)}T23:59:59";
        var url = $"{baseUrl}?sort=tdlap:desc&size={size}&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrEmpty(state))
            url += "&state=" + Uri.EscapeDataString(state);
        return url;
    }

    private async Task<HttpResponseMessage> SendAuthenticatedGetAsync(string accessToken, string url, CancellationToken cancellationToken)
    {
        if (ScoQueryResilience.IsScoQueryUrl(url))
            return await ScoQueryResilience.SendAuthorizedGetAsync(_httpClient, accessToken, url, cancellationToken).ConfigureAwait(false);
        return await ApiRetryHelper.SendWithRetryAsync(async () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InvoiceListApiResponse?> GetInvoicesListAsync(string accessToken, string url, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedGetAsync(accessToken, url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        var stateEl = root.TryGetProperty("state", out var s) ? s : default;
        var state = stateEl.ValueKind == JsonValueKind.String ? stateEl.GetString() : null;
        var time = root.TryGetProperty("time", out var tm) ? tm.GetInt32() : 0;
        var list = new List<InvoiceItemApiDto>();
        if (root.TryGetProperty("datas", out var datas) && datas.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in datas.EnumerateArray())
                list.Add(ParseInvoiceItem(item));
        }
        return new InvoiceListApiResponse(list, total, state, time);
    }

    private static InvoiceItemApiDto ParseInvoiceItem(JsonElement item)
    {
        string? id = null;
        if (item.TryGetProperty("id", out var idEl)) id = idEl.GetString();
        string? nbmst = null;
        if (item.TryGetProperty("nbmst", out var nbmstEl)) nbmst = nbmstEl.GetString();
        string? khhdon = null;
        if (item.TryGetProperty("khhdon", out var khhdonEl)) khhdon = khhdonEl.GetString();
        var shdon = item.TryGetProperty("shdon", out var shdonEl) ? shdonEl.GetInt32() : 0;
        ushort khmshdon = 0;
        if (item.TryGetProperty("khmshdon", out var khmshdonEl) && khmshdonEl.ValueKind == JsonValueKind.Number)
            khmshdon = (ushort)khmshdonEl.GetInt32();
        string? cqt = null;
        if (item.TryGetProperty("cqt", out var cqtEl)) cqt = cqtEl.GetString();
        DateTime? tdlap = TryGetDateTime(item, "tdlap");
        DateTime? nky = TryGetDateTime(item, "nky");
        string? nbten = GetString(item, "nbten");
        string? nmten = GetString(item, "nmten");
        string? nmmst = GetString(item, "nmmst");
        decimal? tgtttbso = TryGetDecimal(item, "tgtttbso");
        decimal? tgtcthue = TryGetDecimal(item, "tgtcthue");
        decimal? tgtthue = TryGetDecimal(item, "tgtthue");
        short tthai = item.TryGetProperty("tthai", out var tthaiEl) ? (short)tthaiEl.GetInt32() : (short)0;
        short ttxly = item.TryGetProperty("ttxly", out var ttxlyEl) ? (short)ttxlyEl.GetInt32() : (short)0;
        string? thdon = GetString(item, "thdon");
        string? thtttoan = GetString(item, "thtttoan");
        var rawJson = item.GetRawText();
        return new InvoiceItemApiDto(id, nbmst, khhdon, shdon, khmshdon, cqt, tdlap, nky, nbten, nmten, nmmst, tgtttbso, tgtcthue, tgtthue, tthai, ttxly, thdon, thtttoan, rawJson);
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) ? p.GetString() : null;

    private static decimal? TryGetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static DateTime? TryGetDateTime(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var s = p.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }

    public async Task<string?> GetInvoiceDetailJsonAsync(string accessToken, string nbmst, string khhdon, int shdon, ushort khmshdon, bool fromSco = false, CancellationToken cancellationToken = default)
    {
        var baseUrl = fromSco ? InvoiceDetailScoUrl : InvoiceDetailUrl;
        var url = $"{baseUrl}?nbmst={Uri.EscapeDataString(nbmst)}&khhdon={Uri.EscapeDataString(khhdon)}&shdon={shdon}&khmshdon={khmshdon}";
        using var response = await SendAuthenticatedGetAsync(accessToken, url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetInvoiceRelativeJsonAsync(string accessToken, string nbmst, ushort khmshdon, string khhdon, int shdon, bool fromSco = false, CancellationToken cancellationToken = default)
    {
        var baseUrl = fromSco ? InvoiceRelativeScoUrl : InvoiceRelativeUrl;
        var url = $"{baseUrl}?nbmst={Uri.EscapeDataString(nbmst)}&khmshdon={khmshdon}&khhdon={Uri.EscapeDataString(khhdon)}&shdon={shdon}";
        using var response = await SendAuthenticatedGetAsync(accessToken, url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>API export-xml: query (thường) hoặc sco-query (hóa đơn máy tính tiền). Trả về raw bytes. Khi 500 + message "Không tồn tại hồ sơ gốc" thì ném InvoiceExportNoXmlException.</summary>
    public async Task<byte[]?> GetInvoiceExportAsync(string accessToken, string nbmst, string khhdon, int shdon, ushort khmshdon, bool fromSco = false, CancellationToken cancellationToken = default)
    {
        var baseUrl = fromSco ? InvoiceScoExportXmlUrl : InvoiceExportXmlUrl;
        var url = $"{baseUrl}?nbmst={Uri.EscapeDataString(nbmst)}&khhdon={Uri.EscapeDataString(khhdon)}&shdon={shdon}&khmshdon={khmshdon}";
        using var response = await SendAuthenticatedGetAsync(accessToken, url, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var msg = TryGetMessageFromErrorBody(body);
            if (!string.IsNullOrEmpty(msg) && msg.Contains("Không tồn tại hồ sơ gốc", StringComparison.OrdinalIgnoreCase))
                throw new InvoiceExportNoXmlException(msg);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? TryGetMessageFromErrorBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString();
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
