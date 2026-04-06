using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ cổng tra cứu của SES Group (MST nhà cung cấp 0315382923).
/// Quy trình:
/// - Đọc PortalLink (đường dẫn tra cứu), SecureKey (mã bí mật) và MST người mua từ JSON hóa đơn.
/// - Mở PortalLink, điền MST người mua và SecureKey vào các ô tương ứng.
/// - Bấm nút tra cứu / tìm kiếm, sau đó bấm nút tải PDF để tải file về.
/// </summary>
[InvoiceProvider("0315382923", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true)]
public sealed class SesGroupInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (SES Group).</summary>
    public string ProviderKey => "0315382923";

    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 500;

    private readonly ILogger _logger;

    public SesGroupInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(SesGroupInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var (portalLink, secureKey, buyerTaxCode) = GetPortalLinkSecureKeyAndBuyerTaxCode(payloadJson);
        if (string.IsNullOrWhiteSpace(portalLink) || string.IsNullOrWhiteSpace(secureKey))
        {
            _logger.LogWarning("SES PDF: payload thiếu PortalLink hoặc SecureKey.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu PortalLink hoặc SecureKey (ttkhac/cttkhac). Không thể tải PDF từ SES Group.");
        }

        if (string.IsNullOrWhiteSpace(buyerTaxCode))
        {
            _logger.LogWarning("SES PDF: payload không tìm thấy MST người mua (nmmst).");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu mã số thuế người mua (nmmst). Không thể tải PDF từ SES Group.");
        }

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_SesGroupPdf", Guid.NewGuid().ToString("N")[..8]);
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
                Headless = true,
                ExecutablePath = installedBrowser.GetExecutablePath(),
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            };
            browser = await Puppeteer.LaunchAsync(options).ConfigureAwait(false);
            var page = await browser.NewPageAsync().ConfigureAwait(false);

            await page.Client.SendAsync("Page.setDownloadBehavior",
                new { behavior = "allow", downloadPath = downloadDir }).ConfigureAwait(false);

            _logger.LogDebug("SES PDF: mở {Url}", portalLink);

            await page.GoToAsync(portalLink.Trim(), new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Điền MST người mua.
            var buyerInput = await FindInputByHintsAsync(
                page,
                new[] { "mst nguoi mua", "mã số thuế người mua", "buyer tax", "vatcodebuyer", "mstmua" },
                cancellationToken).ConfigureAwait(false);

            if (buyerInput == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ô nhập MST người mua trên trang SES Group.");

            await buyerInput.ClickAsync().ConfigureAwait(false);
            await buyerInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await buyerInput.TypeAsync(buyerTaxCode.Trim()).ConfigureAwait(false);

            // Điền SecureKey.
            var secureInput = await FindInputByHintsAsync(
                page,
                new[] { "securekey", "mã bí mật", "ma bi mat", "ma tra cuu", "matracuu", "secret" },
                cancellationToken).ConfigureAwait(false);

            if (secureInput == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ô nhập SecureKey trên trang SES Group.");

            await secureInput.ClickAsync().ConfigureAwait(false);
            await secureInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await secureInput.TypeAsync(secureKey.Trim()).ConfigureAwait(false);

            var filesBefore = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).ToHashSet();

            // Bấm nút tra cứu / tìm kiếm.
            var searchClicked = await page.EvaluateFunctionAsync<bool>(@"
() => {
  const buttons = Array.from(document.querySelectorAll('button, input[type=""submit""]'));
  for (const el of buttons) {
    const text = (el.textContent || el.value || '').toLowerCase();
    if (text.includes('tra cứu') || text.includes('tra cuu') ||
        text.includes('tìm kiếm') || text.includes('tim kiem') ||
        text.includes('xem hóa đơn') || text.includes('xem hoa don')) {
      (el as HTMLElement).click();
      return true;
    }
  }
  return false;
}
").ConfigureAwait(false);

            if (!searchClicked)
                _logger.LogDebug("SES PDF: không tìm thấy nút tra cứu rõ ràng, chờ xem hệ thống có tự tải PDF không.");

            // Sau khi tra cứu, tìm nút tải PDF (nếu có).
            var downloadClicked = await page.EvaluateFunctionAsync<bool>(@"
() => {
  const buttons = Array.from(document.querySelectorAll('button, a'));
  for (const el of buttons) {
    const text = (el.textContent || '').toLowerCase();
    if (text.includes('tải pdf') || text.includes('tai pdf') ||
        text.includes('tải hóa đơn') || text.includes('tai hoa don')) {
      (el as HTMLElement).click();
      return true;
    }
  }
  return false;
}
").ConfigureAwait(false);

            if (!downloadClicked)
            {
                _logger.LogDebug("SES PDF: không tìm thấy nút 'Tải PDF', chờ file tải tự động (nếu có).");
            }

            string? downloadedPath = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(DownloadWaitTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filesNow = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly);
                downloadedPath = filesNow
                    .Select(Path.GetFullPath)
                    .Where(f => !filesBefore.Contains(Path.GetFileName(f)))
                    .FirstOrDefault(f => !Path.GetExtension(f).Equals(".crdownload", StringComparison.OrdinalIgnoreCase));
                if (downloadedPath != null)
                {
                    try
                    {
                        using var fs = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (fs.Length > 0)
                            break;
                    }
                    catch (IOException)
                    {
                        downloadedPath = null;
                    }
                }
                await Task.Delay(DownloadPollIntervalMs, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(downloadedPath) || !File.Exists(downloadedPath))
                return new InvoicePdfResult.Failure("Hết thời gian chờ tải file PDF từ SES Group.");

            var ext = Path.GetExtension(downloadedPath);
            if (!ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return new InvoicePdfResult.Failure($"File tải từ SES Group không phải PDF (ext={ext}).");

            var pdfBytes = await File.ReadAllBytesAsync(downloadedPath, cancellationToken).ConfigureAwait(false);
            if (pdfBytes.Length == 0)
                return new InvoicePdfResult.Failure("File PDF SES Group tải về rỗng.");

            var fileName = Path.GetFileName(downloadedPath);
            _logger.LogInformation("SES PDF: đã tải {File} ({Size} bytes).", fileName, pdfBytes.Length);
            return new InvoicePdfResult.Success(pdfBytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF SES Group.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SES PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF SES Group: " + ex.Message);
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

    private static async Task<IElementHandle?> FindInputByHintsAsync(IPage page, string[] hints, CancellationToken cancellationToken)
    {
        var handle = await page.EvaluateFunctionHandleAsync(@"(hints) => {
  const list = hints.map(h => h.toLowerCase());
  const inputs = Array.from(document.querySelectorAll('input'));
  for (const el of inputs) {
    const ph = (el.getAttribute('placeholder') || '').toLowerCase();
    const name = (el.getAttribute('name') || '').toLowerCase();
    const id = (el.getAttribute('id') || '').toLowerCase();
    for (const h of list) {
      if (ph.includes(h) || name.includes(h) || id.includes(h)) {
        return el;
      }
    }
  }
  return null;
}", hints).ConfigureAwait(false);

        return handle as IElementHandle;
    }

    private static (string? PortalLink, string? SecureKey, string? BuyerTaxCode) GetPortalLinkSecureKeyAndBuyerTaxCode(string payloadJson)
    {
        string? portalLink = null;
        string? secureKey = null;
        string? buyerTaxCode = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
            return (null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            // MST người mua: nmmst
            if (r.TryGetProperty("nmmst", out var nmmst) && nmmst.ValueKind == JsonValueKind.String)
                buyerTaxCode = nmmst.GetString();

            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                if (portalLink == null || secureKey == null)
                {
                    var (pl, sk) = GetPortalLinkAndSecureKeyFromCttkhac(candidate);
                    if (portalLink == null && !string.IsNullOrWhiteSpace(pl)) portalLink = pl;
                    if (secureKey == null && !string.IsNullOrWhiteSpace(sk)) secureKey = sk;
                }

                if (portalLink == null || secureKey == null)
                {
                    var (pl2, sk2) = GetPortalLinkAndSecureKeyFromTtkhac(candidate);
                    if (portalLink == null && !string.IsNullOrWhiteSpace(pl2)) portalLink = pl2;
                    if (secureKey == null && !string.IsNullOrWhiteSpace(sk2)) secureKey = sk2;
                }

                if (portalLink == null || secureKey == null)
                {
                    var (pl3, sk3) = GetPortalLinkAndSecureKeyFromDirectFields(candidate);
                    if (portalLink == null && !string.IsNullOrWhiteSpace(pl3)) portalLink = pl3;
                    if (secureKey == null && !string.IsNullOrWhiteSpace(sk3)) secureKey = sk3;
                }

                if (!string.IsNullOrWhiteSpace(portalLink) && !string.IsNullOrWhiteSpace(secureKey))
                    break;
            }
        }
        catch
        {
            // ignore parse errors
        }

        return (portalLink, secureKey, buyerTaxCode);
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

    private static bool IsPortalLinkName(string name)
    {
        var n = SmartInvoice.Core.StringNormalization.NormalizeForComparison(name);
        return n.Contains("portallink", StringComparison.Ordinal) || n.Contains("portal", StringComparison.Ordinal);
    }

    private static bool IsSecureKeyName(string name)
    {
        var n = SmartInvoice.Core.StringNormalization.NormalizeForComparison(name);
        return n.Contains("securekey", StringComparison.Ordinal)
               || n.Contains("secure", StringComparison.Ordinal)
               || n.Contains("secret", StringComparison.Ordinal);
    }

    private static (string? PortalLink, string? SecureKey) GetPortalLinkAndSecureKeyFromCttkhac(JsonElement r)
    {
        string? portalLink = null;
        string? secureKey = null;
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return (null, null);
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tt)) continue;
            if (portalLink == null && IsPortalLinkName(tt))
                portalLink = TryGetDataValue(item);
            else if (secureKey == null && IsSecureKeyName(tt))
                secureKey = TryGetDataValue(item);
        }
        return (portalLink, secureKey);
    }

    private static (string? PortalLink, string? SecureKey) GetPortalLinkAndSecureKeyFromTtkhac(JsonElement r)
    {
        string? portalLink = null;
        string? secureKey = null;
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return (null, null);
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind == JsonValueKind.Object && outer.TryGetProperty("ttchung", out var ttchung))
            {
                if (portalLink == null || secureKey == null)
                {
                    var (pl, sk) = GetPortalLinkAndSecureKeyFromTtchung(ttchung);
                    if (portalLink == null && !string.IsNullOrWhiteSpace(pl)) portalLink = pl;
                    if (secureKey == null && !string.IsNullOrWhiteSpace(sk)) secureKey = sk;
                }
            }
            else
            {
                if (portalLink == null || secureKey == null)
                {
                    var (pl2, sk2) = GetPortalLinkAndSecureKeyFromCttkhacLikeItem(outer);
                    if (portalLink == null && !string.IsNullOrWhiteSpace(pl2)) portalLink = pl2;
                    if (secureKey == null && !string.IsNullOrWhiteSpace(sk2)) secureKey = sk2;
                }
            }
        }
        return (portalLink, secureKey);
    }

    private static (string? PortalLink, string? SecureKey) GetPortalLinkAndSecureKeyFromTtchung(JsonElement ttchung)
    {
        string? portalLink = null;
        string? secureKey = null;
        if (ttchung.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ttchung.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tt)) continue;
                if (portalLink == null && IsPortalLinkName(tt))
                    portalLink = TryGetDataValue(item);
                else if (secureKey == null && IsSecureKeyName(tt))
                    secureKey = TryGetDataValue(item);
            }
        }
        else if (ttchung.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ttchung.EnumerateObject())
            {
                if (portalLink == null && IsPortalLinkName(prop.Name))
                {
                    var s = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : (prop.Value.ValueKind == JsonValueKind.Object ? TryGetDataValue(prop.Value) : null);
                    if (!string.IsNullOrWhiteSpace(s)) portalLink = s;
                }
                else if (secureKey == null && IsSecureKeyName(prop.Name))
                {
                    var s = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : (prop.Value.ValueKind == JsonValueKind.Object ? TryGetDataValue(prop.Value) : null);
                    if (!string.IsNullOrWhiteSpace(s)) secureKey = s;
                }
            }
        }
        return (portalLink, secureKey);
    }

    private static (string? PortalLink, string? SecureKey) GetPortalLinkAndSecureKeyFromCttkhacLikeItem(JsonElement item)
    {
        string? portalLink = null;
        string? secureKey = null;
        if (item.ValueKind != JsonValueKind.Object) return (null, null);
        var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tt)) return (null, null);
        if (IsPortalLinkName(tt))
            portalLink = TryGetDataValue(item);
        else if (IsSecureKeyName(tt))
            secureKey = TryGetDataValue(item);
        return (portalLink, secureKey);
    }

    private static (string? PortalLink, string? SecureKey) GetPortalLinkAndSecureKeyFromDirectFields(JsonElement r)
    {
        string? portalLink = null;
        string? secureKey = null;
        if (r.ValueKind != JsonValueKind.Object) return (null, null);
        foreach (var prop in r.EnumerateObject())
        {
            if (portalLink == null && IsPortalLinkName(prop.Name))
            {
                var s = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : (prop.Value.ValueKind == JsonValueKind.Object ? TryGetDataValue(prop.Value) : null);
                if (!string.IsNullOrWhiteSpace(s)) portalLink = s;
            }
            else if (secureKey == null && IsSecureKeyName(prop.Name))
            {
                var s = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : (prop.Value.ValueKind == JsonValueKind.Object ? TryGetDataValue(prop.Value) : null);
                if (!string.IsNullOrWhiteSpace(s)) secureKey = s;
            }
        }
        return (portalLink, secureKey);
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

