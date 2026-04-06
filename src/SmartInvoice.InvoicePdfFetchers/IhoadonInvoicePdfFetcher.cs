using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ nhà cung cấp iHoadon (EFY, MST 0102519041) bằng cách:
/// 1) Dùng XML hóa đơn (đã tải về) để up lên trang kiểm tra.
/// 2) Mở https://ihoadon.vn/kiem-tra/?lang=vn, chọn radio "Từ file", upload XML.
/// 3) ChỞ popup hiển thị và bấm nút "Tải PDF" để tải file về.
/// </summary>
[InvoiceProvider("0102519041", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true)]
public sealed class IhoadonInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (iHoadon).</summary>
    public string ProviderKey => "0102519041";

    private const string SearchPageUrl = "https://ihoadon.vn/kiem-tra/?lang=vn";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 500;

    private readonly ILogger _logger;

    public IhoadonInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(IhoadonInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string xmlContentOrPayload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(xmlContentOrPayload))
            return new InvoicePdfResult.Failure("Dữ liệu XML hóa đơn trống. Không thể tải PDF iHoadon.");

        var trimmed = xmlContentOrPayload.Trim();
        if (!trimmed.StartsWith("<", StringComparison.Ordinal))
            return new InvoicePdfResult.Failure("Nhà cung cấp iHoadon yêu cầu XML đầy đủ của hóa đơn. Không tìm thấy XML tương ứng.");

        // Ghi XML ra file tạm để upload lên form.
        var tempRoot = Path.Combine(Path.GetTempPath(), "SmartInvoice_Ihoadon", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        var xmlPath = Path.Combine(tempRoot, "invoice.xml");
        await File.WriteAllTextAsync(xmlPath, trimmed, cancellationToken).ConfigureAwait(false);

        var downloadDir = Path.Combine(tempRoot, "Download");
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

            _logger.LogDebug("iHoadon PDF: mở {Url}", SearchPageUrl);

            await page.GoToAsync(SearchPageUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            var radioSelected = await page.EvaluateFunctionAsync<bool>(@"
() => {
  const labels = Array.from(document.querySelectorAll('label'));
  for (const lb of labels) {
    const text = (lb.textContent || '').trim().toLowerCase();
    if (text.includes('từ file') || text.includes('tu file')) {
      // Nếu label liên kết với input qua for/id
      const forId = lb.getAttribute('for');
      if (forId) {
        const input = document.getElementById(forId);
        if (input && (input.type === 'radio' || input.type === 'checkbox')) {
          input.click();
          return true;
        }
      }
      // Hoặc input con bên trong label
      const inputInside = lb.querySelector('input[type=""radio""]');
      if (inputInside) {
        (inputInside as HTMLInputElement).click();
        return true;
      }
    }
  }
  // Thử radio có value hoặc name gợi ý
  const radios = Array.from(document.querySelectorAll('input[type=""radio""]'));
  for (const r of radios) {
    const val = (r as HTMLInputElement).value?.toLowerCase() || '';
    const name = (r as HTMLInputElement).name?.toLowerCase() || '';
    if (val.includes('file') || name.includes('file')) {
      (r as HTMLInputElement).click();
      return true;
    }
  }
  return false;
}
").ConfigureAwait(false);

            if (!radioSelected)
                return new InvoicePdfResult.Failure("Không tìm thấy lựa chọn 'Từ file' trên trang iHoadon. Cấu trúc trang có thể đã thay đổi.");

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            // 2) Tìm input[type=file] và upload XML.
            var fileInput = await page.QuerySelectorAsync("input[type='file']").ConfigureAwait(false);
            if (fileInput == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ô chọn file XML trên trang iHoadon.");

            await fileInput.UploadFileAsync(xmlPath).ConfigureAwait(false);
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            var filesBefore = Directory.GetFiles(downloadDir, "*.pdf", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 3) Tìm nút 'Tải PDF' trong popup và click.
            var clickedDownload = await page.EvaluateFunctionAsync<bool>(@"
() => {
  const buttons = Array.from(document.querySelectorAll('button, a'));
  for (const el of buttons) {
    const text = (el.textContent || '').trim().toLowerCase();
    if (text.includes('tải pdf') || text.includes('tai pdf')) {
      (el as HTMLElement).click();
      return true;
    }
  }
  return false;
}
").ConfigureAwait(false);

            if (!clickedDownload)
            {
                // Có thể trang tự động tải PDF sau khi upload, không có nút riêng.
                _logger.LogDebug("iHoadon PDF: không tìm thấy nút 'Tải PDF', chờ file tải tự động.");
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
                            _logger.LogInformation("iHoadon PDF: đã tải {File} ({Size} bytes).", fileName, bytes.Length);
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

            return new InvoicePdfResult.Failure("Hết thời gian chờ tải PDF từ trang iHoadon. Kiểm tra file XML hoặc thử lại sau.");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "iHoadon PDF: lỗi khi lấy PDF từ XML.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF: " + ex.Message);
        }
        finally
        {
            if (browser != null)
                await browser.CloseAsync().ConfigureAwait(false);
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}

