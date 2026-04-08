using System.IO.Compression;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn cho Thế Giới Di Động (NCC 0306731335).
/// Trang tra cứu: https://hddt.thegioididong.com/
/// Input:
/// - phone: số điện thoại người mua
/// - billNum: số hóa đơn hoặc mã tra cứu
/// Sau khi captcha + tra cứu, click "Tải HĐ chuyển đổi", tải file zip và bóc PDF.
/// </summary>
[InvoiceProvider("0306731335", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true)]
public sealed class ThegioididongInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    public string ProviderKey => "0306731335";

    private const string SearchUrl = "https://hddt.thegioididong.com/";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 60000;
    private const int PollIntervalMs = 500;

    private readonly ILogger _logger;
    private readonly ICaptchaSolverService _captchaSolver;

    public ThegioididongInvoicePdfFetcher(ILoggerFactory loggerFactory, ICaptchaSolverService captchaSolver)
    {
        _logger = loggerFactory.CreateLogger(nameof(ThegioididongInvoicePdfFetcher));
        _captchaSolver = captchaSolver;
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var (buyerPhone, billNumberOrLookupCode) = ThegioididongTraCuuParsing.GetLookupInputs(payloadJson);
        if (string.IsNullOrWhiteSpace(buyerPhone))
            return new InvoicePdfResult.Failure("Không tìm thấy số điện thoại người mua để tra cứu TGDĐ.");
        if (string.IsNullOrWhiteSpace(billNumberOrLookupCode))
            return new InvoicePdfResult.Failure("Không tìm thấy số hóa đơn/mã tra cứu để tra cứu TGDĐ.");

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_TGDD", Guid.NewGuid().ToString("N")[..8]);
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

            browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = false,
                ExecutablePath = installedBrowser.GetExecutablePath(),
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            }).ConfigureAwait(false);

            var page = await browser.NewPageAsync().ConfigureAwait(false);
            await page.Client.SendAsync("Page.setDownloadBehavior", new
            {
                behavior = "allow",
                downloadPath = downloadDir
            }).ConfigureAwait(false);

            await page.GoToAsync(SearchUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Điền 2 ô input chính.
            var phoneInput = await page.WaitForSelectorAsync("input#phone", new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            var billInput = await page.WaitForSelectorAsync("input#billNum", new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (phoneInput == null || billInput == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ô nhập phone/billNum trên trang TGDĐ.");

            await phoneInput.ClickAsync().ConfigureAwait(false);
            await phoneInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await phoneInput.TypeAsync(buyerPhone.Trim()).ConfigureAwait(false);

            await billInput.ClickAsync().ConfigureAwait(false);
            await billInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await billInput.TypeAsync(billNumberOrLookupCode.Trim()).ConfigureAwait(false);

            // Captcha.
            var captchaImg = await page.WaitForSelectorAsync(".captcha img", new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (captchaImg == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ảnh captcha của TGDĐ.");

            var captchaPath = Path.Combine(downloadDir, "tgdd_captcha.png");
            await captchaImg.ScreenshotAsync(captchaPath, new ElementScreenshotOptions { Type = ScreenshotType.Png }).ConfigureAwait(false);
            await using var captchaStream = new FileStream(captchaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var captchaText = (await _captchaSolver.SolveFromStreamAsync(captchaStream, cancellationToken).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrWhiteSpace(captchaText))
                return new InvoicePdfResult.Failure("Không giải được captcha TGDĐ.");

            var captchaInput = await page.WaitForSelectorAsync("input#Captcha", new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (captchaInput == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ô nhập captcha TGDĐ.");
            await captchaInput.ClickAsync().ConfigureAwait(false);
            await captchaInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await captchaInput.TypeAsync(captchaText).ConfigureAwait(false);

            var filesBefore = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var submitButton = await page.QuerySelectorAsync("button#btViewbill").ConfigureAwait(false);
            if (submitButton != null)
                await submitButton.ClickAsync().ConfigureAwait(false);
            else
                await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);

            // Đợi bảng kết quả + link "Tải HĐ chuyển đổi".
            var downloadHandle = await page.WaitForFunctionAsync(@"() => {
                const anchors = Array.from(document.querySelectorAll('td a[href]'));
                if (!anchors.length) return false;
                return anchors.some(a => (a.textContent || '').toLowerCase().includes('tải hđ chuyển đổi') ||
                                         (a.textContent || '').toLowerCase().includes('tai hd chuyen doi'));
            }", new WaitForFunctionOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (downloadHandle == null)
                return new InvoicePdfResult.Failure("Không tìm thấy link 'Tải HĐ chuyển đổi' sau khi tra cứu TGDĐ.");

            var linkHandle = await page.EvaluateFunctionHandleAsync(@"() => {
                const anchors = Array.from(document.querySelectorAll('td a[href]'));
                for (const a of anchors) {
                    const t = (a.textContent || '').toLowerCase();
                    if (t.includes('tải hđ chuyển đổi') || t.includes('tai hd chuyen doi')) return a;
                }
                return anchors.length >= 2 ? anchors[1] : (anchors.length > 0 ? anchors[0] : null);
            }").ConfigureAwait(false);
            var link = linkHandle as IElementHandle;
            if (link == null)
                return new InvoicePdfResult.Failure("Không xác định được link tải HĐ chuyển đổi.");

            await link.ClickAsync().ConfigureAwait(false);

            // Chờ file tải về (zip/pdf).
            var downloadedPath = await WaitForDownloadedFileAsync(downloadDir, filesBefore, DownloadWaitTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (downloadedPath == null)
                return new InvoicePdfResult.Failure("Hết thời gian chờ tải file từ TGDĐ.");

            var ext = Path.GetExtension(downloadedPath);
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await File.ReadAllBytesAsync(downloadedPath, cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0) return new InvoicePdfResult.Failure("File PDF TGDĐ rỗng.");
                return new InvoicePdfResult.Success(bytes, Path.GetFileName(downloadedPath));
            }

            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await using var fs = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                var pdfEntry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && e.Length > 0);
                if (pdfEntry == null)
                    return new InvoicePdfResult.Failure("File ZIP TGDĐ không chứa PDF.");

                await using var entryStream = pdfEntry.Open();
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                var bytes = ms.ToArray();
                if (bytes.Length == 0)
                    return new InvoicePdfResult.Failure("PDF trong ZIP TGDĐ rỗng.");
                return new InvoicePdfResult.Success(bytes, string.IsNullOrWhiteSpace(pdfEntry.Name) ? "invoice-tgdd.pdf" : pdfEntry.Name);
            }

            return new InvoicePdfResult.Failure($"File tải từ TGDĐ không phải ZIP/PDF (ext={ext}).");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF TGDĐ.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TGDD PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF TGDĐ: " + ex.Message);
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
                // best effort
            }
        }
    }

    private static async Task<string?> WaitForDownloadedFileAsync(
        string downloadDir,
        HashSet<string?> filesBefore,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filesNow = Directory.Exists(downloadDir)
                ? Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
            var candidate = filesNow
                .Select(Path.GetFullPath)
                .FirstOrDefault(f =>
                {
                    var name = Path.GetFileName(f);
                    return !filesBefore.Contains(name) && !name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase);
                });
            if (candidate != null)
            {
                try
                {
                    using var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (fs.Length > 0)
                        return candidate;
                }
                catch (IOException)
                {
                    // keep waiting
                }
            }
            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}

