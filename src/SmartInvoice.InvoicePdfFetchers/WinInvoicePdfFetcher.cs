using System.IO.Compression;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ WinInvoice (tracuu.wininvoice.vn) cho NCC mã số 0312303803.
/// Quy trình:
/// - Đọc private_code (Mã tra cứu hóa đơn) và cmpn_key (Mã công ty) từ cttkhac trong payload.
/// - Mở https://tracuu.wininvoice.vn/, điền 2 ô input, bấm "Xem hóa đơn".
/// - Sau khi bảng hiện ra, tìm link "Tải file hóa đơn" (a.go-link.btn-info) và click ngay trong Chromium.
/// - Dùng cơ chế download của Chromium để lấy file (zip/PDF) và trích xuất PDF.
/// </summary>
[InvoiceProvider("0104918404", InvoiceProviderMatchKind.SellerTaxCode, InvoiceLookupRegistryKey = "0312303803", MayRequireUserIntervention = true)]
[InvoiceProvider("0312303803", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true)]
public sealed class WinInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>
    /// Key logic cho Registry/Resolver. Ở đây map theo MST người bán 0104918404 (WinCommerce/WinMart),
    /// nên ProviderKey không nhất thiết là MST NCC thật mà là logical key cho fetcher.
    /// </summary>
    public string ProviderKey => "0104918404";

    private const string SearchUrl = "https://tracuu.wininvoice.vn/";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 500;

    private readonly ILogger _logger;

    public WinInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(WinInvoicePdfFetcher));
    }

    public Task<InvoicePdfResult> AcquirePdfAsync(InvoiceContentContext context, CancellationToken cancellationToken = default)
    {
        if (!InvoicePayloadJsonAccessor.TryGetInvoiceJsonForPortalFields(context, out var json))
            return Task.FromResult<InvoicePdfResult>(new InvoicePdfResult.Failure(
                "Thiếu JSON hóa đơn để đọc cttkhac WinInvoice (Mã tra cứu / Mã công ty)."));
        return FetchPdfAsync(json, cancellationToken);
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (!WinInvoiceTraCuuParsing.TryGetTraCuuCodesFromPayload(payloadJson, out var privateCode, out var companyKey))
        {
            _logger.LogWarning("WinInvoice PDF: payload không có đủ cttkhac 'Mã tra cứu hóa đơn' và 'Mã công ty'.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu Mã tra cứu hóa đơn hoặc Mã công ty (cttkhac). Không thể tải PDF từ WinInvoice.");
        }

        IBrowser? browser = null;
        string? downloadDir = null;
        try
        {
            // Khởi tạo Chromium (dùng chung thư mục với các fetcher khác).
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

            downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_WinInvoicePdf", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(downloadDir);

            await page.Client.SendAsync("Page.setDownloadBehavior",
                new { behavior = "allow", downloadPath = downloadDir }).ConfigureAwait(false);

            _logger.LogDebug("WinInvoice PDF: mở {Url}", SearchUrl);

            await page.GoToAsync(SearchUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Điền Mã tra cứu hóa đơn (private_code)
            var privateInput = await page.WaitForSelectorAsync("input[name=\"private_code\"]",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (privateInput == null)
                return new InvoicePdfResult.Failure("Trang WinInvoice không có ô 'Mã tra cứu hóa đơn' (input[name=\"private_code\"]).");
            await privateInput.ClickAsync().ConfigureAwait(false);
            await privateInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await privateInput.TypeAsync(privateCode).ConfigureAwait(false);

            // Điền Mã công ty (cmpn_key)
            var companyInput = await page.WaitForSelectorAsync("input[name=\"cmpn_key\"]",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (companyInput == null)
                return new InvoicePdfResult.Failure("Trang WinInvoice không có ô 'Mã công ty' (input[name=\"cmpn_key\"]).");
            await companyInput.ClickAsync().ConfigureAwait(false);
            await companyInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await companyInput.TypeAsync(companyKey).ConfigureAwait(false);

            // Bấm nút "Xem hóa đơn" trong form hiện tại.
            var submitButton = await page.QuerySelectorAsync("form button.btn.blue[type=\"submit\"]").ConfigureAwait(false);
            if (submitButton == null)
                submitButton = await page.QuerySelectorAsync("button.btn.blue[type=\"submit\"]").ConfigureAwait(false);
            if (submitButton == null)
                return new InvoicePdfResult.Failure("Không tìm thấy nút 'Xem hóa đơn' trên trang WinInvoice.");

            await submitButton.ClickAsync().ConfigureAwait(false);

            // Đợi bảng kết quả + cột hành động xuất hiện.
            var downloadLink = await page.WaitForSelectorAsync(
                "td.td-actions a.go-link.btn.btn-info.btn-sm",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (downloadLink == null)
                return new InvoicePdfResult.Failure("Không tìm thấy link 'Tải file hóa đơn' trên trang kết quả WinInvoice.");

            var filesBefore = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).ToHashSet();

            await downloadLink.ClickAsync().ConfigureAwait(false);

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
                return new InvoicePdfResult.Failure("Hết thời gian chờ tải file từ WinInvoice.");

            var ext = Path.GetExtension(downloadedPath);
            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await using var fs = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

                var pdfEntry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && e.Length > 0);
                if (pdfEntry == null)
                    return new InvoicePdfResult.Failure("Trong file zip WinInvoice không có file PDF hóa đơn.");

                await using var entryStream = pdfEntry.Open();
                using var pdfMs = new MemoryStream();
                await entryStream.CopyToAsync(pdfMs, cancellationToken).ConfigureAwait(false);
                var pdfBytes = pdfMs.ToArray();

                if (pdfBytes.Length == 0)
                    return new InvoicePdfResult.Failure("Đọc file PDF từ zip WinInvoice thất bại.");

                var fileName = pdfEntry.Name;
                _logger.LogInformation("WinInvoice PDF: đã tải {File} ({Size} bytes).", fileName, pdfBytes.Length);
                return new InvoicePdfResult.Success(pdfBytes, fileName);
            }

            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var pdfBytes = await File.ReadAllBytesAsync(downloadedPath, cancellationToken).ConfigureAwait(false);
                if (pdfBytes.Length == 0)
                    return new InvoicePdfResult.Failure("File PDF WinInvoice tải về rỗng.");

                var fileName = Path.GetFileName(downloadedPath);
                _logger.LogInformation("WinInvoice PDF: đã tải {File} ({Size} bytes).", fileName, pdfBytes.Length);
                return new InvoicePdfResult.Success(pdfBytes, fileName);
            }

            return new InvoicePdfResult.Failure($"File tải từ WinInvoice không phải zip hoặc pdf (ext={ext}).");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF WinInvoice.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WinInvoice PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF WinInvoice: " + ex.Message);
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
}

