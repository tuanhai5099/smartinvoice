using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Xml.Linq;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ cổng eHoadon (van.ehoadon.vn) cho NCC BKAV (mã số thuế 101360697).
/// Mở trang Lookup?InvoiceGUID={id}, click dropdown Download → LinkDownPDF, chờ file tải và trả về bytes.
/// Dùng Puppeteer Sharp (Chromium headless) để scrape vì trang dùng JS và dropdown.
/// </summary>
public sealed class EhoadonInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (BKAV).</summary>
    public string ProviderKey => "0101360697";

    private const string LookupBaseUrl = "https://van.ehoadon.vn/Lookup";
    // Trang eHoadon đôi khi tải khá chậm → tăng timeout và dùng DOMContentLoaded thay vì Networkidle0.
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 500;

    private readonly ILogger _logger;

    public EhoadonInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(EhoadonInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var invoiceId = GetInvoiceIdFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(invoiceId))
        {
            _logger.LogWarning("Ehoadon PDF: payload không có trường id.");
            return new InvoicePdfResult.Failure("Payload hóa đơn thiếu mã id (InvoiceGUID).");
        }

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_EhoadonPdf", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(downloadDir);

        IBrowser? browser = null;
        try
        {
            // Đảm bảo luôn có Chromium để chạy (tự tải về nếu chưa có), tránh lỗi "chrome.exe not found".
            var chromiumRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SmartInvoice",
                "Chromium");
            var browserFetcher = new BrowserFetcher(new BrowserFetcherOptions
            {
                Path = chromiumRoot
            });
            var installedBrowser = await browserFetcher.DownloadAsync().ConfigureAwait(false);

            var options = new LaunchOptions
            {
                Headless = true,
                ExecutablePath = installedBrowser.GetExecutablePath(),
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            };
            browser = await Puppeteer.LaunchAsync(options).ConfigureAwait(false);
            var page = await browser.NewPageAsync().ConfigureAwait(false);

            await page.Client.SendAsync("Page.setDownloadBehavior", new { behavior = "allow", downloadPath = downloadDir }).ConfigureAwait(false);

            var url = $"{LookupBaseUrl}?InvoiceGUID={Uri.EscapeDataString(invoiceId)}";
            _logger.LogDebug("Ehoadon PDF: mở {Url}", url);

            // Dùng DOMContentLoaded + timeout dài hơn để tránh Navigation timeout 15000ms.
            await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Đợi nút Download render xong thay vì query ngay lập tức.
            var btnDownload = await page.WaitForSelectorAsync("#btnDownload",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (btnDownload == null)
            {
                return new InvoicePdfResult.Failure("Trang tra cứu không có nút Download. Có thể hóa đơn chưa có PDF hoặc trang thay đổi.");
            }

            await btnDownload.ClickAsync().ConfigureAwait(false);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            // Sau khi mở dropdown Download, đợi link PDF xuất hiện.
            var linkPdf = await page.WaitForSelectorAsync("#LinkDownPDF",
                new WaitForSelectorOptions { Timeout = DownloadWaitTimeoutMs }).ConfigureAwait(false);
            if (linkPdf == null)
            {
                return new InvoicePdfResult.Failure("Không tìm thấy link Download PDF trên trang.");
            }

            var filesBefore = Directory.GetFiles(downloadDir, "*.pdf", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet();

            await linkPdf.ClickAsync().ConfigureAwait(false);

            string? downloadedPath = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(DownloadWaitTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filesNow = Directory.GetFiles(downloadDir, "*.pdf", SearchOption.TopDirectoryOnly);
                downloadedPath = filesNow.Select(Path.GetFullPath).FirstOrDefault(f => !filesBefore.Contains(Path.GetFileName(f)));
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
                            _logger.LogInformation("Ehoadon PDF: đã tải {File} ({Size} bytes).", fileName, bytes.Length);
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

            return new InvoicePdfResult.Failure("Hết thời gian chờ tải PDF. Có thể trang chưa phát sinh file hoặc cần đăng nhập.");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ehoadon PDF: lỗi khi lấy PDF cho InvoiceGUID={Id}", invoiceId);
            return new InvoicePdfResult.Failure("Lỗi lấy PDF: " + ex.Message);
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

    private static string? GetInvoiceIdFromPayload(string payloadOrXml)
    {
        if (string.IsNullOrWhiteSpace(payloadOrXml)) return null;
        var trimmed = payloadOrXml.Trim();

        // Trường hợp eHoadon: nhận vào XML, lấy DLHDon/@Id làm InvoiceGUID.
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            try
            {
                var xdoc = XDocument.Parse(trimmed);
                // XML mẫu: <HDon><DLHDon Id="...">...</DLHDon>...</HDon>
                var dlhDon = xdoc.Root?
                    .Descendants()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "DLHDon", StringComparison.OrdinalIgnoreCase));
                var idAttr = dlhDon?.Attribute("Id")?.Value;
                return string.IsNullOrWhiteSpace(idAttr) ? null : idAttr.Trim();
            }
            catch
            {
                return null;
            }
        }

        // Payload không phải XML hợp lệ → không lấy được InvoiceGUID.
        return null;
    }
}
