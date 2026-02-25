using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ HTInvoice (laphoadon.htinvoice.vn) cho NCC mã số 0315638251.
/// Quy trình:
/// - Đọc DC TC (địa chỉ tra cứu) và Mã TC (mã tra cứu) từ cttkhac trong payload.
/// - Mở trang tra cứu (mặc định https://laphoadon.htinvoice.vn/TraCuu nếu DC TC trống).
/// - Tìm iframe reCAPTCHA, di chuyển chuột giả lập đường đi rồi click vào div.rc-anchor-content.
/// - Điền mã tra cứu vào ô txtMaTraCuu, thực thi hàm TimHoaDon() trong JavaScript context.
/// - Đợi nút Tải FILE PDF (#btnTaiFilePDF), click để tải file, đọc PDF (hoặc zip chứa PDF) và trả về bytes.
/// </summary>
public sealed class HtInvoiceInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (HTInvoice).</summary>
    public string ProviderKey => "0315638251";

    private const string DefaultSearchUrl = "https://laphoadon.htinvoice.vn/TraCuu";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 300;

    private readonly ILogger _logger;
    private static readonly Random MouseRandom = new();

    public HtInvoiceInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(HtInvoiceInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var (searchUrl, maTraCuu) = GetSearchUrlAndCodeFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(maTraCuu))
        {
            _logger.LogWarning("HTInvoice PDF: payload không có Mã TC trong cttkhac.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu Mã tra cứu (cttkhac.'Mã TC'). Không thể tải PDF từ HTInvoice.");
        }

        var url = string.IsNullOrWhiteSpace(searchUrl) ? DefaultSearchUrl : searchUrl.Trim();

        IBrowser? browser = null;
        string? downloadDir = null;
        try
        {
            // Ưu tiên dùng Microsoft Edge cài sẵn, fallback sang Chromium tải về nếu không tìm thấy.
            string? edgePath = null;
            var edgeCandidates = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            };
            foreach (var candidate in edgeCandidates)
            {
                if (File.Exists(candidate))
                {
                    edgePath = candidate;
                    break;
                }
            }

            string executablePath;
            if (!string.IsNullOrEmpty(edgePath))
            {
                executablePath = edgePath;
            }
            else
            {
                var chromiumRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SmartInvoice",
                    "Chromium");
                var browserFetcher = new BrowserFetcher(new BrowserFetcherOptions { Path = chromiumRoot });
                var installedBrowser = await browserFetcher.DownloadAsync().ConfigureAwait(false);
                executablePath = installedBrowser.GetExecutablePath();
            }

            var options = new LaunchOptions
            {
                Headless = false,
                ExecutablePath = executablePath,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            };
            browser = await Puppeteer.LaunchAsync(options).ConfigureAwait(false);
            var page = await browser.NewPageAsync().ConfigureAwait(false);

            downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_HtInvoicePdf", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(downloadDir);

            await page.Client.SendAsync("Page.setDownloadBehavior",
                new { behavior = "allow", downloadPath = downloadDir }).ConfigureAwait(false);

            _logger.LogDebug("HTInvoice PDF: mở {Url}", url);

            await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Xử lý reCAPTCHA: tìm iframe và click vào .rc-anchor-content với hover + click (giả lập chuột người dùng).
            await TrySolveRecaptchaAsync(page, cancellationToken).ConfigureAwait(false);

            // Điền mã tra cứu vào ô txtMaTraCuu
            var codeInput = await page.WaitForSelectorAsync("input#txtMaTraCuu, input[name=\"txtMaTraCuu\"]",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (codeInput == null)
                return new InvoicePdfResult.Failure("Trang HTInvoice không có ô 'Mã tra cứu' (txtMaTraCuu).");

            await codeInput.ClickAsync().ConfigureAwait(false);
            await codeInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await codeInput.TypeAsync(maTraCuu.Trim()).ConfigureAwait(false);

            // Thực thi hàm TimHoaDon() trong JavaScript context
            try
            {
                await page.EvaluateExpressionAsync("TimHoaDon()").ConfigureAwait(false);
            }
            catch (PuppeteerException ex)
            {
                _logger.LogWarning(ex, "HTInvoice PDF: lỗi khi gọi TimHoaDon().");
                return new InvoicePdfResult.Failure("Lỗi gọi hàm TimHoaDon() trên trang HTInvoice.");
            }

            // Đợi kết quả tra cứu render và nút Tải FILE PDF xuất hiện
            var downloadButton = await page.WaitForSelectorAsync("input#btnTaiFilePDF",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (downloadButton == null)
                return new InvoicePdfResult.Failure("Không tìm thấy nút 'Tải FILE PDF' (#btnTaiFilePDF) trên trang HTInvoice.");

            var filesBefore = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).ToHashSet();

            await downloadButton.ClickAsync().ConfigureAwait(false);

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
                return new InvoicePdfResult.Failure("Hết thời gian chờ tải file từ HTInvoice.");

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
                    return new InvoicePdfResult.Failure("Trong file zip HTInvoice không có file PDF hóa đơn.");

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
                return new InvoicePdfResult.Failure($"File tải từ HTInvoice không phải zip hoặc pdf (ext={ext}).");
            }

            if (pdfBytes.Length == 0)
                return new InvoicePdfResult.Failure("File PDF HTInvoice tải về rỗng.");

            _logger.LogInformation("HTInvoice PDF: đã tải {File} ({Size} bytes).", fileName, pdfBytes.Length);
            return new InvoicePdfResult.Success(pdfBytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF HTInvoice.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTInvoice PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF HTInvoice: " + ex.Message);
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

    /// <summary>Lấy DC TC (URL tra cứu) và Mã TC (mã tra cứu) từ cttkhac.</summary>
    private static (string? SearchUrl, string? MaTraCuu) GetSearchUrlAndCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return (null, null);

            string? dcTc = null;
            string? maTc = null;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var ttruong = item.TryGetProperty("ttruong", out var tt) ? tt.GetString() : null;
                if (string.IsNullOrWhiteSpace(ttruong)) continue;

                var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                    : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

                if (string.Equals(ttruong.Trim(), "DC TC", StringComparison.OrdinalIgnoreCase))
                    dcTc = value;
                else if (string.Equals(ttruong.Trim(), "Mã TC", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(ttruong.Trim(), "Ma TC", StringComparison.OrdinalIgnoreCase))
                    maTc = value;
            }

            return (dcTc, maTc);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Cố gắng click vào checkbox reCAPTCHA bằng cách hover + click vào div.rc-anchor-content.
    /// Nếu không tìm thấy iframe hoặc phần tử, chỉ bỏ qua để tránh chặn toàn bộ flow.
    /// </summary>
    private static async Task TrySolveRecaptchaAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var iframeElement = await page.WaitForSelectorAsync("iframe[src*=\"recaptcha\"]",
                new WaitForSelectorOptions { Timeout = 15000 }).ConfigureAwait(false);
            if (iframeElement == null)
                return;

            var frame = await iframeElement.ContentFrameAsync().ConfigureAwait(false);
            if (frame == null)
                return;

            var anchor = await frame.WaitForSelectorAsync("div.rc-anchor-content",
                new WaitForSelectorOptions { Timeout = 15000 }).ConfigureAwait(false);
            if (anchor == null)
                return;

            // Hover + click vào vùng reCAPTCHA với một chút delay/random để giống hành vi người dùng.
            await anchor.HoverAsync().ConfigureAwait(false);
            await Task.Delay(MouseRandom.Next(120, 260), cancellationToken).ConfigureAwait(false);
            await anchor.ClickAsync().ConfigureAwait(false);

            // Đợi một chút cho reCAPTCHA xử lý.
            await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Không để lỗi reCAPTCHA chặn toàn bộ flow; trang có thể có reCAPTCHA nhẹ hoặc không bắt buộc.
        }
    }
}

