using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ E-Invoice (einvoice.vn) cho NCC mã số 0101300842 (Thái Sơn).
/// Hiện tại ưu tiên dùng cổng E-Invoice với Mã nhận hóa đơn (MaNhanHoaDon).
/// Trong tương lai có thể bổ sung các cách khác (ví dụ QRCODE) theo thứ tự ưu tiên.
/// </summary>
public sealed class EinvoiceInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (E-Invoice).</summary>
    public string ProviderKey => "0101300842";

    private const string DefaultSearchUrl = "https://einvoice.vn/tra-cuu";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 300;
    private const int MaxCaptchaRetries = 5;

    private static readonly Regex FourUppercaseLetters = new(@"^[A-Z]{4}$", RegexOptions.Compiled);

    private readonly ICaptchaSolverService _captchaSolver;
    private readonly ILogger _logger;

    public EinvoiceInvoicePdfFetcher(ICaptchaSolverService captchaSolver, ILoggerFactory loggerFactory)
    {
        _captchaSolver = captchaSolver ?? throw new ArgumentNullException(nameof(captchaSolver));
        _logger = loggerFactory.CreateLogger(nameof(EinvoiceInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        // Ưu tiên 1: nếu payload có QRCODE chứa link tải PDF trực tiếp thì dùng luôn.
        var directUrl = GetPdfUrlFromQrCode(payloadJson);
        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            try
            {
                _logger.LogDebug("Einvoice PDF: tải PDF trực tiếp từ QRCODE URL {Url}", directUrl);
                using var client = new HttpClient();
                using var resp = await client.GetAsync(directUrl.Trim(), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    var fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"').Trim();
                    if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        fileName = "invoice.pdf";
                    return new InvoicePdfResult.Success(bytes, fileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Einvoice PDF: tải trực tiếp từ QRCODE URL thất bại, fallback sang cổng tra cứu.");
            }
        }

        // Ưu tiên 2: cổng E-Invoice với Mã nhận hóa đơn.
        var (searchUrl, maNhanHoaDon) = GetSearchUrlAndCodeFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(maNhanHoaDon))
        {
            _logger.LogWarning("Einvoice PDF: payload không có Mã TC trong cttkhac.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu Mã tra cứu (cttkhac.'Mã TC'). Không thể tải PDF từ E-Invoice.");
        }

        var url = string.IsNullOrWhiteSpace(searchUrl) ? DefaultSearchUrl : searchUrl.Trim();

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_EinvoicePdf", Guid.NewGuid().ToString("N")[..8]);
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

            _logger.LogDebug("Einvoice PDF: mở {Url}", url);

            await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Điền Mã nhận hóa đơn
            var codeInput = await page.WaitForSelectorAsync("input[name=\"MaNhanHoaDon\"]",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (codeInput == null)
                return new InvoicePdfResult.Failure("Trang E-Invoice không có ô 'Mã nhận hóa đơn' (input[name=\"MaNhanHoaDon\"]).");
            await codeInput.ClickAsync().ConfigureAwait(false);
            await codeInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await codeInput.TypeAsync(maNhanHoaDon.Trim()).ConfigureAwait(false);

            string? captchaText = null;
            var solvedAndAccepted = false;
            for (var attempt = 0; attempt < MaxCaptchaRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Chụp ảnh captcha trực tiếp từ phần tử img#CaptchaImage thành stream PNG, rồi dùng pipeline preprocess trong thư viện Captcha (không lưu file tạm).
                var captchaElement = await page.QuerySelectorAsync("#CaptchaImage").ConfigureAwait(false);
                if (captchaElement == null)
                    return new InvoicePdfResult.Failure("Không tìm thấy ảnh captcha (#CaptchaImage) trên trang E-Invoice.");

                var captchaBytes = await captchaElement.ScreenshotDataAsync(
                        new ElementScreenshotOptions { Type = ScreenshotType.Png })
                    .ConfigureAwait(false);
                await using (var ms = new MemoryStream(captchaBytes))
                {
                    captchaText = (await _captchaSolver.SolveFromStreamAsync(ms, cancellationToken).ConfigureAwait(false))
                        ?.Trim().ToUpperInvariant();
                }

                if (string.IsNullOrEmpty(captchaText) || !FourUppercaseLetters.IsMatch(captchaText))
                {
                    _logger.LogDebug("Einvoice PDF: captcha giải được không hợp lệ (attempt {Attempt}), thử lại.", attempt + 1);
                }
                else
                {
                    // Điền mã kiểm tra
                    var captchaInput = await page.WaitForSelectorAsync("#CaptchaInputText",
                        new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
                    if (captchaInput == null)
                        return new InvoicePdfResult.Failure("Trang E-Invoice không có ô 'Mã kiểm tra' (#CaptchaInputText).");
                    await captchaInput.ClickAsync().ConfigureAwait(false);
                    await captchaInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
                    await captchaInput.TypeAsync(captchaText).ConfigureAwait(false);

                    // Sau khi nhập captcha xong, nhấn Enter để submit form.
                    await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

                    // Nếu server báo "Mã kiểm tra không đúng." thì giải lại captcha mới.
                    // Lưu ý: khi captcha đúng, trang có thể navigation → context cũ bị destroy (Execution context was destroyed).
                    string? errorText = null;
                    try
                    {
                        errorText = await page.EvaluateFunctionAsync<string?>(@"() => {
                            const el = document.querySelector('div.offset-md-5.text-danger.fix-text-danger');
                            return el ? (el.textContent || '').trim() : '';
                        }").ConfigureAwait(false);
                    }
                    catch (PuppeteerException ex) when (
                        ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase))
                    {
                        // Có navigation sau khi submit → coi như captcha đã đúng, thoát vòng lặp.
                        solvedAndAccepted = true;
                        break;
                    }

                    if (string.IsNullOrEmpty(errorText) ||
                        !errorText.Contains("Mã kiểm tra không đúng.", StringComparison.OrdinalIgnoreCase))
                    {
                        solvedAndAccepted = true;
                        break;
                    }

                    _logger.LogDebug("Einvoice PDF: server báo 'Mã kiểm tra không đúng.' (attempt {Attempt}), thử lại.", attempt + 1);
                }

                // Thử lại sau một khoảng delay (server thường tự cấp captcha mới sau mỗi lần sai).
                if (attempt < MaxCaptchaRetries - 1)
                {
                    await Task.Delay(800, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!solvedAndAccepted)
                return new InvoicePdfResult.Failure("Không giải được mã captcha 4 ký tự in hoa sau vài lần thử. Vui lòng thử lại.");
            await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
            // Đợi popup chi tiết hóa đơn với nút "Tải hóa đơn"
            var downloadToggle = await page.WaitForSelectorAsync("a.btn-download-custom.dropdown-toggle",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (downloadToggle == null)
                return new InvoicePdfResult.Failure("Không tìm thấy nút 'Tải hóa đơn' trên popup E-Invoice.");

            var filesBefore = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).ToHashSet();

            // Lần click đầu tiên: mở menu và chọn "Tải hóa đơn dạng PDF".
            await downloadToggle.ClickAsync().ConfigureAwait(false);
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);

            var pdfLink = await page.WaitForSelectorAsync(
                "ul.dropdown-menu.dropdown-dl-custom li a[href*='format=pdf']",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (pdfLink == null)
                return new InvoicePdfResult.Failure("Không tìm thấy mục 'Tải hóa đơn dạng PDF' trong menu tải E-Invoice.");

            await pdfLink.ClickAsync().ConfigureAwait(false);

            // Đợi file tải về (zip hoặc pdf).
            // Nếu chưa thấy file tạm .crdownload sau vài lần poll thì click lại toggle + link PDF tối đa 3 lần.
            string? downloadedPath = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(DownloadWaitTimeoutMs);
            const int MaxDownloadClickRetries = 3;
            var clickAttempts = 1; // đã click 1 lần ở trên
            var downloadStarted = false;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filesNow = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly);
                var newFiles = filesNow
                    .Select(Path.GetFullPath)
                    .Where(f => !filesBefore.Contains(Path.GetFileName(f)))
                    .ToList();

                if (newFiles.Count > 0)
                {
                    // Nếu có file .crdownload thì coi như download đã bắt đầu.
                    if (newFiles.Any(f => Path.GetExtension(f).Equals(".crdownload", StringComparison.OrdinalIgnoreCase)))
                        downloadStarted = true;

                    // Tìm file hoàn chỉnh (không phải .crdownload)
                    downloadedPath = newFiles.FirstOrDefault(f =>
                        !Path.GetExtension(f).Equals(".crdownload", StringComparison.OrdinalIgnoreCase));
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
                }

                // Nếu chưa thấy file tạm .crdownload nào và cũng chưa có file hoàn chỉnh,
                // thử click lại toggle + link PDF thêm tối đa 2 lần nữa.
                if (!downloadStarted && downloadedPath == null && clickAttempts < MaxDownloadClickRetries)
                {
                    clickAttempts++;
                    await downloadToggle.ClickAsync().ConfigureAwait(false);
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    var pdfLinkRetry = await page.QuerySelectorAsync(
                        "ul.dropdown-menu.dropdown-dl-custom li a[href*='format=pdf']").ConfigureAwait(false);
                    if (pdfLinkRetry != null)
                        await pdfLinkRetry.ClickAsync().ConfigureAwait(false);
                }

                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(downloadedPath) || !File.Exists(downloadedPath))
                return new InvoicePdfResult.Failure("Hết thời gian chờ tải file từ E-Invoice.");

            var ext = Path.GetExtension(downloadedPath);
            byte[] pdfBytes;
            string fileName;

            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await using var fs = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

                var pdfEntry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && e.Length > 0);
                if (pdfEntry == null)
                    return new InvoicePdfResult.Failure("Trong file zip E-Invoice không có file PDF hóa đơn.");

                await using var entryStream = pdfEntry.Open();
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                pdfBytes = ms.ToArray();
                fileName = pdfEntry.Name;
            }
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                pdfBytes = await File.ReadAllBytesAsync(downloadedPath, cancellationToken).ConfigureAwait(false);
                fileName = Path.GetFileName(downloadedPath);
            }
            else
            {
                return new InvoicePdfResult.Failure($"File tải từ E-Invoice không phải zip hoặc pdf (ext={ext}).");
            }

            if (pdfBytes.Length == 0)
                return new InvoicePdfResult.Failure("File PDF E-Invoice tải về rỗng.");

            _logger.LogInformation("Einvoice PDF: đã tải {File} ({Size} bytes).", fileName, pdfBytes.Length);
            return new InvoicePdfResult.Success(pdfBytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF E-Invoice.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Einvoice PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF E-Invoice: " + ex.Message);
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

    /// <summary>
    /// Tìm đường dẫn PDF từ trường QRCODE trong JSON hóa đơn (ví dụ Thai Son có thể cung cấp sẵn link portal).
    /// - Duyệt các root candidate: r, ndhdon, hdon, data[0].
    /// - Tìm trong ttkhac/cttkhac hoặc field trực tiếp có tên chứa "qrcode".
    /// - Giá trị có thể là URL đầy đủ hoặc chuỗi chứa URL.
    /// </summary>
    private static string? GetPdfUrlFromQrCode(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var fromTtkhac = GetQrCodeFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return ExtractUrl(fromTtkhac);

                var fromCttkhac = GetQrCodeFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return ExtractUrl(fromCttkhac);

                var direct = GetQrCodeFromDirectFields(candidate);
                if (!string.IsNullOrWhiteSpace(direct)) return ExtractUrl(direct);
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

    private static bool IsQrCodeName(string name)
    {
        var n = SmartInvoice.Core.StringNormalization.NormalizeForComparison(name);
        return n.Contains("qrcode", StringComparison.Ordinal);
    }

    private static string? GetQrCodeFromTtkhac(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind == JsonValueKind.Object && outer.TryGetProperty("ttchung", out var ttchung))
            {
                var v = GetQrCodeFromTtchung(ttchung);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            else
            {
                var v = GetQrCodeFromCttkhacLikeItem(outer);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        return null;
    }

    private static string? GetQrCodeFromTtchung(JsonElement ttchung)
    {
        if (ttchung.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ttchung.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tt) || !IsQrCodeName(tt)) continue;
                var dl = TryGetDataValue(item);
                if (!string.IsNullOrWhiteSpace(dl)) return dl;
            }
        }
        else if (ttchung.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ttchung.EnumerateObject())
            {
                if (!IsQrCodeName(prop.Name)) continue;
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

    private static string? GetQrCodeFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            var v = GetQrCodeFromCttkhacLikeItem(item);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static string? GetQrCodeFromCttkhacLikeItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tt) || !IsQrCodeName(tt)) return null;
        var dl = TryGetDataValue(item);
        return string.IsNullOrWhiteSpace(dl) ? null : dl;
    }

    private static string? GetQrCodeFromDirectFields(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in r.EnumerateObject())
        {
            if (!IsQrCodeName(prop.Name)) continue;
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

    /// <summary>Trích URL đầu tiên nhìn giống http/https trong chuỗi QRCode.</summary>
    private static string? ExtractUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        // Nếu bản thân chuỗi là URL
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;

        // Tìm http/https trong chuỗi dài hơn
        var idx = s.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            idx = s.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var rest = s[idx..];
        // Cắt đến khoảng trắng đầu tiên (nếu có)
        var spaceIdx = rest.IndexOf(' ');
        if (spaceIdx > 0)
            rest = rest[..spaceIdx];

        return rest;
    }

    /// <summary>Lấy DC TC (URL tra cứu) và Mã TC (Mã nhận hóa đơn) từ cttkhac/ttkhac hoặc trường gốc payload.</summary>
    private static (string? SearchUrl, string? MaNhanHoaDon) GetSearchUrlAndCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            string? dcTc = null;
            string? maTc = null;

            static string NormalizeLabel(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                // Chuẩn hóa: lowercase + bỏ dấu + bỏ khoảng trắng/ký tự không phải chữ/số.
                var lower = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
                var sb = new System.Text.StringBuilder(lower.Length);
                foreach (var ch in lower)
                {
                    var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                    if (uc == System.Globalization.UnicodeCategory.NonSpacingMark)
                        continue; // bỏ dấu
                    if (char.IsLetterOrDigit(ch))
                        sb.Append(ch);
                }
                return sb.ToString();
            }

            static void ScanArrayForDcTcAndMaTc(JsonElement arr, ref string? dcTcRef, ref string? maTcRef)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var ttruong = item.TryGetProperty("ttruong", out var tt) ? tt.GetString() : null;
                    if (string.IsNullOrWhiteSpace(ttruong)) continue;

                    var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                        : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                    var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

                    var norm = NormalizeLabel(ttruong); // ví dụ "DC TC" → "dctc", "Mã TC" → "matc"
                    if (dcTcRef == null && (norm == "dctc" || norm.Contains("diachitracuu")))
                        dcTcRef = value;
                    else if (maTcRef == null && (norm == "matc" || norm.Contains("matracuu") || norm.Contains("manhanhoadon")))
                        maTcRef = value;

                    if (dcTcRef != null && maTcRef != null)
                        break;
                }
            }

            // 1) cttkhac
            if (r.TryGetProperty("cttkhac", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                ScanArrayForDcTcAndMaTc(arr, ref dcTc, ref maTc);
            }

            // 2) ttkhac – một số payload có thể đặt DC TC / Mã TC ở đây
            if ((dcTc == null || maTc == null) && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                ScanArrayForDcTcAndMaTc(ttkhac, ref dcTc, ref maTc);
            }

            // Fallback: đọc từ trường gốc payload (một số API trả mã tra cứu ở root)
            if (string.IsNullOrWhiteSpace(maTc))
            {
                maTc = GetStr(r, "maNhanHoaDon") ?? GetStr(r, "MaNhanHoaDon")
                    ?? GetStr(r, "matracuu") ?? GetStr(r, "MaTraCuu")
                    ?? GetStr(r, "maTc") ?? GetStr(r, "MaTC");
            }

            return (dcTc, maTc);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? GetStr(JsonElement el, string propName)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(propName, out var p)) return null;
        var s = p.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}

