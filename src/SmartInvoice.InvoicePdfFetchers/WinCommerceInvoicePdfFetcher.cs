using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn cho WinCommerce (MST người bán 0104918404) thông qua trang hoadon.winmart.vn.
/// Quy trình:
/// - Đọc mã cơ quan thuế (MCCQT) từ payload JSON (ttkhac/cttkhac/field trực tiếp).
/// - Mở https://hoadon.winmart.vn/.
/// - Điền MCCQT vào ô "Mã cơ quan thuế", bấm Tìm kiếm.
/// - Đợi kết quả, tìm nút Download PDF và bấm để tải file PDF về.
/// </summary>
public sealed class WinCommerceInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Key fetcher dùng cho map theo MST người bán (không phải msttcgp).</summary>
    public string ProviderKey => "WIN-INVOICE";

    private const string SearchUrl = "https://hoadon.winmart.vn/";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 500;

    private readonly ILogger _logger;

    public WinCommerceInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(WinCommerceInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var mccqt = GetMccqtFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(mccqt))
        {
            _logger.LogWarning("WinCommerce PDF: payload không tìm thấy trường MCCQT trong ttkhac/cttkhac.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu mã cơ quan thuế (MCCQT). Không thể tải PDF từ hoadon.winmart.vn.");
        }

        IBrowser? browser = null;
        string? downloadDir = null;
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

            downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_WinCommercePdf", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(downloadDir);

            await page.Client.SendAsync("Page.setDownloadBehavior",
                new { behavior = "allow", downloadPath = downloadDir }).ConfigureAwait(false);

            _logger.LogDebug("WinCommerce PDF: mở {Url}", SearchUrl);

            await page.GoToAsync(SearchUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Tìm ô nhập Mã cơ quan thuế: thử theo name/id/placeholder.
            var codeInput = await page.EvaluateFunctionHandleAsync(@"() => {
  const inputs = Array.from(document.querySelectorAll('input'));
  for (const el of inputs) {
    const ph = (el.getAttribute('placeholder') || '').toLowerCase();
    const name = (el.getAttribute('name') || '').toLowerCase();
    const id = (el.getAttribute('id') || '').toLowerCase();
    if (ph.includes('mã cơ quan thuế') || ph.includes('ma co quan thue') ||
        name.includes('mccqt') || id.includes('mccqt')) {
      return el;
    }
  }
  return null;
}").ConfigureAwait(false) as IElementHandle;

            if (codeInput == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ô nhập 'Mã cơ quan thuế' trên trang hoadon.winmart.vn.");

            await codeInput.ClickAsync().ConfigureAwait(false);
            await codeInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await codeInput.TypeAsync(mccqt.Trim()).ConfigureAwait(false);

            // Tìm và bấm nút Tìm kiếm.
            var searchClicked = await page.EvaluateFunctionAsync<bool>(@"
() => {
  const buttons = Array.from(document.querySelectorAll('button, input[type=""submit""]'));
  for (const el of buttons) {
    const text = (el.textContent || el.value || '').toLowerCase();
    if (text.includes('tìm kiếm') || text.includes('tim kiem') || text.includes('tìm')) {
      (el as HTMLElement).click();
      return true;
    }
  }
  return false;
}
").ConfigureAwait(false);

            if (!searchClicked)
                return new InvoicePdfResult.Failure("Không tìm thấy nút 'Tìm kiếm' trên trang hoadon.winmart.vn.");

            var filesBefore = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).ToHashSet();

            // Đợi kết quả và nút Download PDF, nếu có.
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
                _logger.LogDebug("WinCommerce PDF: không tìm thấy nút 'Tải PDF', chờ file tải tự động (nếu có).");
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
                return new InvoicePdfResult.Failure("Hết thời gian chờ tải file từ hoadon.winmart.vn.");

            var ext = Path.GetExtension(downloadedPath);
            if (!ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return new InvoicePdfResult.Failure($"File tải từ hoadon.winmart.vn không phải PDF (ext={ext}).");

            var pdfBytes = await File.ReadAllBytesAsync(downloadedPath, cancellationToken).ConfigureAwait(false);
            if (pdfBytes.Length == 0)
                return new InvoicePdfResult.Failure("File PDF WinCommerce tải về rỗng.");

            var fileName = Path.GetFileName(downloadedPath);
            _logger.LogInformation("WinCommerce PDF: đã tải {File} ({Size} bytes).", fileName, pdfBytes.Length);
            return new InvoicePdfResult.Success(pdfBytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF WinCommerce.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WinCommerce PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF WinCommerce: " + ex.Message);
        }
        finally
        {
            if (browser != null)
                await browser.CloseAsync().ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrEmpty(downloadDir) && Directory.Exists(downloadDir))
                    Directory.Delete(downloadDir, true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    /// <summary>
    /// Lấy mã cơ quan thuế (MCCQT) từ JSON:
    /// - Duyệt các root candidate: r, ndhdon, hdon, data[0].
    /// - Tìm trong ttkhac/cttkhac hoặc field trực tiếp có tên chứa "MCCQT" / "MaCoQuanThue".
    /// </summary>
    private static string? GetMccqtFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var fromTtkhac = GetMccqtFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return fromTtkhac.Trim();

                var fromCttkhac = GetMccqtFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();

                var direct = GetMccqtFromDirectFields(candidate);
                if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
            }
        }
        catch
        {
            // ignore
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

    private static bool IsMccqtName(string name)
    {
        var n = SmartInvoice.Core.StringNormalization.NormalizeForComparison(name);
        return n.Contains("mccqt", StringComparison.Ordinal) ||
               n.Contains("macơquanthuế", StringComparison.Ordinal) ||
               n.Contains("macoquanthu", StringComparison.Ordinal);
    }

    private static string? GetMccqtFromTtkhac(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind == JsonValueKind.Object && outer.TryGetProperty("ttchung", out var ttchung))
            {
                var v = GetMccqtFromTtchung(ttchung);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            else
            {
                var v = GetMccqtFromCttkhacLikeItem(outer);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        return null;
    }

    private static string? GetMccqtFromTtchung(JsonElement ttchung)
    {
        if (ttchung.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ttchung.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tt) || !IsMccqtName(tt)) continue;
                var dl = TryGetDataValue(item);
                if (!string.IsNullOrWhiteSpace(dl)) return dl;
            }
        }
        else if (ttchung.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ttchung.EnumerateObject())
            {
                if (!IsMccqtName(prop.Name)) continue;
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
        }
        return null;
    }

    private static string? GetMccqtFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            var v = GetMccqtFromCttkhacLikeItem(item);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static string? GetMccqtFromCttkhacLikeItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tt) || !IsMccqtName(tt)) return null;
        var dl = TryGetDataValue(item);
        return string.IsNullOrWhiteSpace(dl) ? null : dl;
    }

    private static string? GetMccqtFromDirectFields(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in r.EnumerateObject())
        {
            if (!IsMccqtName(prop.Name)) continue;
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

