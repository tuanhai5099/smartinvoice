using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ trang tra cứu Fast e-Invoice (invoice.fast.com.vn) cho NCC mã số 0100727825.
/// Chạy Chromium: mở trang tra cứu → chọn "Tải file PDF" → nhập mã bí mật (từ cttkhac.keysearch.dlieu) → submit → chờ tải PDF.
/// </summary>
public sealed class FastInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (Fast e-Invoice).</summary>
    public string ProviderKey => "0100727825";

    private const string SearchPageUrl = "https://invoice.fast.com.vn/tra-cuu-hoa-don-dien-tu/";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 500;

    private readonly ILogger _logger;

    public FastInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(FastInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var keysearch = GetKeysearchFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(keysearch))
        {
            _logger.LogWarning("Fast PDF: payload không có cttkhac với ttruong 'keysearch' (mã bí mật).");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu mã bí mật tra cứu (cttkhac.keysearch). Không thể tải PDF từ trang Fast.");
        }

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_FastPdf", Guid.NewGuid().ToString("N")[..8]);
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

            await page.Client.SendAsync("Page.setDownloadBehavior", new { behavior = "allow", downloadPath = downloadDir }).ConfigureAwait(false);

            _logger.LogDebug("Fast PDF: mở {Url}", SearchPageUrl);

            await page.GoToAsync(SearchPageUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Chọn "Tải file PDF" (radio type=3)
            var radioPdf = await page.WaitForSelectorAsync("form#form-invoice-search input[name=\"type\"][value=\"3\"]",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (radioPdf == null)
            {
                return new InvoicePdfResult.Failure("Trang tra cứu Fast không có lựa chọn 'Tải file PDF'. Có thể cấu trúc trang đã thay đổi.");
            }
            await radioPdf.ClickAsync().ConfigureAwait(false);
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);

            // Nhập mã bí mật vào ô "Nhập mã số..."
            var keywordInput = await page.WaitForSelectorAsync("form#form-invoice-search input[name=\"keyword\"]",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (keywordInput == null)
            {
                return new InvoicePdfResult.Failure("Trang tra cứu Fast không có ô nhập mã số.");
            }
            await keywordInput.ClickAsync().ConfigureAwait(false);
            await keywordInput.TypeAsync(keysearch).ConfigureAwait(false);

            var filesBefore = Directory.GetFiles(downloadDir, "*.pdf", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet();

            // Submit form (Enter)
            await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);

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
                            _logger.LogInformation("Fast PDF: đã tải {File} ({Size} bytes).", fileName, bytes.Length);
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

            return new InvoicePdfResult.Failure("Hết thời gian chờ tải PDF từ trang Fast. Kiểm tra mã bí mật hoặc thử lại sau.");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fast PDF: lỗi khi lấy PDF với keysearch.");
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

    /// <summary>Lấy giá trị mã bí mật từ cttkhac: item có ttruong = "keysearch" thì lấy dlieu (hoặc dLieu).</summary>
    private static string? GetKeysearchFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                var ttStr = tt.GetString();
                if (string.IsNullOrWhiteSpace(ttStr)) continue;
                if (!string.Equals(ttStr, "keysearch", StringComparison.OrdinalIgnoreCase)) continue;
                var dlieu = item.TryGetProperty("dlieu", out var dl) ? dl.GetString() : null;
                if (string.IsNullOrWhiteSpace(dlieu) && item.TryGetProperty("dLieu", out var dL))
                    dlieu = dL.GetString();
                return string.IsNullOrWhiteSpace(dlieu) ? null : dlieu.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
