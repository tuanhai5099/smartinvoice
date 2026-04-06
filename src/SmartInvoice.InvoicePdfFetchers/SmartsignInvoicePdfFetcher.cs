using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ cổng tra cứu Smartsign (MST nhà cung cấp 0309612872).
/// Trang: https://tracuuhd.smartsign.com.vn/
/// Quy trình:
/// - Đọc mã tra cứu (Matracuu / MaTraCuu / Mã tra cứu) từ JSON payload.
/// - Mở trang tra cứu, điền mã tra cứu vào ô đầu tiên.
/// - Bấm nút "Xem hóa đơn" và chờ file PDF tải về.
/// </summary>
[InvoiceProvider("0309612872", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true)]
public sealed class SmartsignInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (Smartsign).</summary>
    public string ProviderKey => "0309612872";

    private const string SearchPageUrl = "https://tracuuhd.smartsign.com.vn/";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 500;
    private static readonly Regex FourDigits = new(@"^\d{4}$", RegexOptions.Compiled);

    private readonly ILogger _logger;
    private readonly ICaptchaSolverService _captchaSolver;

    public SmartsignInvoicePdfFetcher(ILoggerFactory loggerFactory, ICaptchaSolverService captchaSolver)
    {
        _logger = loggerFactory.CreateLogger(nameof(SmartsignInvoicePdfFetcher));
        _captchaSolver = captchaSolver;

    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var searchCode = GetSearchCodeFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(searchCode))
        {
            _logger.LogWarning("Smartsign PDF: payload không tìm thấy mã tra cứu trong cttkhac/ttkhac.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu mã tra cứu trong thông tin khác. Không thể tải PDF từ Smartsign.");
        }

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_SmartsignPdf", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(downloadDir);

        IBrowser? browser = null;
        try
        {
            var chromiumRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SmartInvoice",
                "Chromium");
            var browserFetcher = new BrowserFetcher(new BrowserFetcherOptions { Path = chromiumRoot });
            var installedBrowser = await browserFetcher.DownloadAsync().ConfigureAwait(false);

            var options = new LaunchOptions
            {
                Headless = false,
                ExecutablePath = installedBrowser.GetExecutablePath(),
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            };
            browser = await Puppeteer.LaunchAsync(options).ConfigureAwait(false);
            var page = await browser.NewPageAsync().ConfigureAwait(false);

            await page.Client.SendAsync("Page.setDownloadBehavior",
                new { behavior = "allow", downloadPath = downloadDir }).ConfigureAwait(false);

            _logger.LogDebug("Smartsign PDF: mở {Url}", SearchPageUrl);

            await page.GoToAsync(SearchPageUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Điền mã tra cứu vào ô input đầu tiên (hoặc input có placeholder/name phù hợp).
            var searchInput = await FindSearchInputAsync(page, cancellationToken).ConfigureAwait(false);
            if (searchInput == null)
            {
                return new InvoicePdfResult.Failure("Không tìm thấy ô nhập 'Mã tra cứu' trên trang Smartsign.");
            }

            string? captchaText = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Lấy ảnh captcha từ trang (fetch img#captcha src, trả về base64)
                var base64 = await page.EvaluateFunctionAsync<string>(@"async () => {
                    const img = document.querySelector('#ContentPlaceHolder1_Image1');
                    if (!img || !img.src) return null;
                    try {
                        const r = await fetch(img.src);
                        const buf = await r.arrayBuffer();
                        const bytes = new Uint8Array(buf);
                        let binary = '';
                        for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                        return btoa(binary);
                    } catch (e) { return null; }
                }").ConfigureAwait(false);

                if (string.IsNullOrEmpty(base64))
                {
                    return new InvoicePdfResult.Failure("Không lấy được ảnh captcha từ trang EasyInvoice.");
                }

                byte[] imageBytes;
                try
                {
                    imageBytes = Convert.FromBase64String(base64);
                }
                catch
                {
                    return new InvoicePdfResult.Failure("Dữ liệu ảnh captcha không hợp lệ.");
                }

                await using var imageStream = new MemoryStream(imageBytes);
                captchaText = (await _captchaSolver.SolveFromStreamAsync(imageStream, cancellationToken).ConfigureAwait(false))?.Trim();
                if (!string.IsNullOrEmpty(captchaText) && FourDigits.IsMatch(captchaText))
                    break;
            }

            var captchaInput = await page.QuerySelectorAsync("#ContentPlaceHolder1_txtCapcha");
            await captchaInput.TypeAsync(captchaText);

          
            await searchInput.ClickAsync().ConfigureAwait(false);
            await searchInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await searchInput.TypeAsync(searchCode.Trim()).ConfigureAwait(false);

            var btnSubmit = await page.QuerySelectorAsync("#btnXML");
            await btnSubmit.ClickAsync().ConfigureAwait(false);

            var filesBefore = Directory.GetFiles(downloadDir, "*.pdf", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Bấm nút "Xem hóa đơn" / "Tra cứu".
            var clicked = await ClickViewInvoiceButtonAsync(page, cancellationToken).ConfigureAwait(false);
            if (!clicked)
            {
                _logger.LogWarning("Smartsign PDF: không tìm thấy nút 'Xem hóa đơn' hoặc 'Tra cứu'.");
                return new InvoicePdfResult.Failure("Không tìm thấy nút 'Xem hóa đơn' trên trang Smartsign.");
            }

            string? downloadedPath = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(DownloadWaitTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filesNow = Directory.GetFiles(downloadDir, "*.pdf", SearchOption.TopDirectoryOnly);
                downloadedPath = filesNow
                    .Select(Path.GetFullPath)
                    .FirstOrDefault(f => !filesBefore.Contains(Path.GetFileName(f)));
                if (downloadedPath != null)
                {
                    try
                    {
                        using var fs = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (fs.Length > 0)
                        {
                            fs.Position = 0;
                            var bytes = new byte[fs.Length];
                            _ = await fs.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
                            var fileName = Path.GetFileName(downloadedPath);
                            _logger.LogInformation("Smartsign PDF: đã tải {File} ({Size} bytes).", fileName, bytes.Length);
                            return new InvoicePdfResult.Success(bytes, fileName);
                        }
                    }
                    catch (IOException)
                    {
                        await Task.Delay(DownloadPollIntervalMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                await Task.Delay(DownloadPollIntervalMs, cancellationToken).ConfigureAwait(false);
            }

            return new InvoicePdfResult.Failure("Hết thời gian chờ tải PDF từ Smartsign. Kiểm tra mã tra cứu hoặc thử lại sau.");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Smartsign PDF: lỗi khi lấy PDF với mã tra cứu.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF Smartsign: " + ex.Message);
        }
        finally
        {
            if (browser != null)
                await browser.CloseAsync().ConfigureAwait(false);
            try
            {
                if (Directory.Exists(downloadDir))
                    Directory.Delete(downloadDir, true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private static async Task<IElementHandle?> FindSearchInputAsync(IPage page, CancellationToken cancellationToken)
    {
        // Thử theo placeholder/name trước, fallback sang input text đầu tiên.
        var handle = await page.EvaluateFunctionHandleAsync(@"() => {
  const inputs = Array.from(document.querySelectorAll('input'));
  const candidates = inputs.filter(el => {
    const type = (el.getAttribute('type') || '').toLowerCase();
    if (type && type !== 'text') return false;
    const ph = (el.getAttribute('placeholder') || '').toLowerCase();
    const name = (el.getAttribute('name') || '').toLowerCase();
    const id = (el.getAttribute('id') || '').toLowerCase();
    const lbl = (el.getAttribute('aria-label') || '').toLowerCase();
    const joined = ph + ' ' + name + ' ' + id + ' ' + lbl;
    return joined.includes('mã tra cứu') || joined.includes('ma tra cuu') || joined.includes('matracuu');
  });
  if (candidates.length > 0) return candidates[0];
  for (const el of inputs) {
    const type = (el.getAttribute('type') || '').toLowerCase();
    if (!type || type === 'text') return el;
  }
  return null;
}").ConfigureAwait(false);

        return handle as IElementHandle;
    }

    private static async Task<bool> ClickViewInvoiceButtonAsync(IPage page, CancellationToken cancellationToken)
    {
        var handle = await page.EvaluateFunctionHandleAsync(@"() => {
  const buttons = Array.from(document.querySelectorAll('button, input[type=""button""], input[type=""submit""]'));
  for (const el of buttons) {
    const text = (el.textContent || el.value || '').toLowerCase();
    if (text.includes('xem hóa đơn') || text.includes('xem hoa don') || text.includes('tra cứu') || text.includes('tra cuu')) {
      return el;
    }
  }
  return null;
}").ConfigureAwait(false);

        if (handle is IElementHandle btn)
        {
            await btn.ClickAsync().ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private static string? GetSearchCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var fromCttkhac = GetSearchCodeFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();

                var fromTtkhac = GetSearchCodeFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return fromTtkhac.Trim();

                var direct = GetSearchCodeFromDirectFields(candidate);
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

    private static bool IsSearchCodeFieldName(string name)
    {
        var norm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(name);
        return norm.Contains("matracuu", StringComparison.Ordinal)
               || norm.Contains("ma tra cuu", StringComparison.Ordinal)
               || norm.Contains("matracuuhoadon", StringComparison.Ordinal)
               || norm.Contains("keysearch", StringComparison.Ordinal);
    }

    private static string? GetSearchCodeFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
            var ttStr = tt.GetString();
            if (string.IsNullOrWhiteSpace(ttStr) || !IsSearchCodeFieldName(ttStr)) continue;

            var dlieu = item.TryGetProperty("dlieu", out var dl) ? dl.GetString() : null;
            if (string.IsNullOrWhiteSpace(dlieu) && item.TryGetProperty("dLieu", out var dL))
                dlieu = dL.GetString();
            if (!string.IsNullOrWhiteSpace(dlieu)) return dlieu;
        }
        return null;
    }

    private static string? GetSearchCodeFromTtkhac(JsonElement r)
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
                    if (string.IsNullOrWhiteSpace(tt) || !IsSearchCodeFieldName(tt)) continue;

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
                    if (!IsSearchCodeFieldName(prop.Name)) continue;
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

    private static string? GetSearchCodeFromDirectFields(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in r.EnumerateObject())
        {
            if (!IsSearchCodeFieldName(prop.Name)) continue;
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

