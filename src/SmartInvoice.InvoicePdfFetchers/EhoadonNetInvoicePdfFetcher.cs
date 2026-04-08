using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF cho ehoadon.net (NCC 0306784030) bằng luồng tra cứu theo tệp XML.
/// URL tra cứu: https://{mst-nguoi-ban}.ehoadon.net/look-up-invoice
/// </summary>
[InvoiceProvider("0306784030", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true, RequiresXml = true)]
public sealed class EhoadonNetInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    public string ProviderKey => "0306784030";

    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 60000;
    private const int PollIntervalMs = 500;

    private readonly ILogger _logger;

    public EhoadonNetInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(EhoadonNetInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> AcquirePdfAsync(InvoiceContentContext context, CancellationToken cancellationToken = default)
    {
        var xml = context.ContentForFetcher;
        if (string.IsNullOrWhiteSpace(xml))
            return new InvoicePdfResult.Failure("Thiếu XML hóa đơn để tra cứu ehoadon.net.");

        var sellerTaxCode = context.SellerTaxCode ?? TryExtractSellerTaxCodeFromXml(xml);
        if (string.IsNullOrWhiteSpace(sellerTaxCode))
            return new InvoicePdfResult.Failure("Không xác định được MST người bán để mở portal ehoadon.net.");

        var normalizedSeller = NormalizeSellerTaxCode(sellerTaxCode);
        var lookupUrl = $"https://{normalizedSeller}.ehoadon.net/look-up-invoice";
        return await FetchByXmlAsync(xml, lookupUrl, cancellationToken).ConfigureAwait(false);
    }

    public Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default) =>
        Task.FromResult<InvoicePdfResult>(new InvoicePdfResult.Failure(
            "Nhà cung cấp ehoadon.net yêu cầu XML; không hỗ trợ payload JSON thuần."));

    private async Task<InvoicePdfResult> FetchByXmlAsync(string xmlContent, string lookupUrl, CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "SmartInvoice_EhoadonNet", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        var xmlPath = Path.Combine(tempRoot, "invoice.xml");
        await File.WriteAllTextAsync(xmlPath, xmlContent, cancellationToken).ConfigureAwait(false);
        var downloadDir = Path.Combine(tempRoot, "download");
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

            await page.Client.SendAsync("Page.setDownloadBehavior", new { behavior = "allow", downloadPath = downloadDir }).ConfigureAwait(false);
            await page.GoToAsync(lookupUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Chọn option "Tra cứu hóa đơn với tệp XML" (value=1)
            var selectedXmlMode = await page.EvaluateFunctionAsync<bool>(@"() => {
  const byValue = document.querySelector('input.lookup-einvoice[value=""1""]');
  if (byValue) { byValue.click(); return true; }
  const radios = Array.from(document.querySelectorAll('input[name=""LookupEInvoice""]'));
  for (const r of radios) {
    if ((r.value || '') === '1') { r.click(); return true; }
  }
  return false;
}").ConfigureAwait(false);
            if (!selectedXmlMode)
                return new InvoicePdfResult.Failure("Không chọn được chế độ tra cứu theo tệp XML trên ehoadon.net.");

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var fileInput = await page.QuerySelectorAsync("input[type='file']").ConfigureAwait(false);
            if (fileInput == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ô upload XML trên ehoadon.net.");
            await fileInput.UploadFileAsync(xmlPath).ConfigureAwait(false);

            // Đợi bảng kết quả xuất hiện.
            await page.WaitForSelectorAsync("#lookupResultGrid .tbl-body-result", new WaitForSelectorOptions
            {
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            var filesBefore = Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Click nút tải PDF trong hàng kết quả đầu tiên.
            var clicked = await page.EvaluateFunctionAsync<bool>(@"() => {
  const anchors = Array.from(document.querySelectorAll('#lookupResultGrid a[title]'));
  for (const a of anchors) {
    const title = (a.getAttribute('title') || '').toLowerCase();
    if (title.includes('tải về tệp pdf') || title.includes('tai ve tep pdf')) { a.click(); return true; }
  }
  const onclickAnchors = Array.from(document.querySelectorAll('#lookupResultGrid a[onclick]'));
  for (const a of onclickAnchors) {
    const oc = (a.getAttribute('onclick') || '').toLowerCase();
    if (oc.includes('downloadeinvoicepdflookup')) { a.click(); return true; }
  }
  return false;
}").ConfigureAwait(false);
            if (!clicked)
                return new InvoicePdfResult.Failure("Không tìm thấy nút tải PDF trong kết quả ehoadon.net.");

            var downloadedPath = await WaitForDownloadedFileAsync(downloadDir, filesBefore, DownloadWaitTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (downloadedPath == null)
                return new InvoicePdfResult.Failure("Hết thời gian chờ tải file PDF từ ehoadon.net.");

            var ext = Path.GetExtension(downloadedPath);
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await File.ReadAllBytesAsync(downloadedPath, cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0) return new InvoicePdfResult.Failure("File PDF ehoadon.net rỗng.");
                return new InvoicePdfResult.Success(bytes, Path.GetFileName(downloadedPath));
            }

            if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await using var fs = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                var pdfEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && e.Length > 0);
                if (pdfEntry == null)
                    return new InvoicePdfResult.Failure("ZIP tải từ ehoadon.net không chứa PDF.");
                await using var entryStream = pdfEntry.Open();
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                var bytes = ms.ToArray();
                if (bytes.Length == 0)
                    return new InvoicePdfResult.Failure("PDF trong ZIP ehoadon.net rỗng.");
                return new InvoicePdfResult.Success(bytes, string.IsNullOrWhiteSpace(pdfEntry.Name) ? "invoice-ehoadonnet.pdf" : pdfEntry.Name);
            }

            return new InvoicePdfResult.Failure($"File tải từ ehoadon.net không phải PDF/ZIP (ext={ext}).");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF ehoadon.net.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ehoadon.net PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF ehoadon.net: " + ex.Message);
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
            var files = Directory.Exists(downloadDir)
                ? Directory.GetFiles(downloadDir, "*.*", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
            var candidate = files
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
                    if (fs.Length > 0) return candidate;
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

    private static string NormalizeSellerTaxCode(string raw) => raw.Trim().Replace(" ", string.Empty);

    private static string? TryExtractSellerTaxCodeFromXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var seller = doc.Descendants().FirstOrDefault(x =>
                string.Equals(x.Name.LocalName, "nbmst", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(seller)) return seller;
            seller = doc.Descendants().FirstOrDefault(x =>
                string.Equals(x.Name.LocalName, "MST", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Parent?.Name.LocalName, "NBan", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            return string.IsNullOrWhiteSpace(seller) ? null : seller;
        }
        catch
        {
            return null;
        }
    }
}

