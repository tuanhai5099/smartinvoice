using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ cổng Vietinvoice (NCC 0106870211).
/// Trang tra cứu: https://tracuuhoadon.vietinvoice.vn/
/// Mã tra cứu lấy từ payload (cttkhac/ttkhac/direct fields).
/// </summary>
[InvoiceProvider("0106870211", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true)]
public sealed class VietinvoiceInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    public string ProviderKey => "0106870211";

    private const string SearchPageUrl = "https://tracuuhoadon.vietinvoice.vn/";
    private const int PageLoadTimeoutMs = 45000;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public VietinvoiceInvoicePdfFetcher(HttpClient httpClient, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = loggerFactory.CreateLogger(nameof(VietinvoiceInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var lookupCode = VietinvoiceTraCuuParsing.GetLookupCodeFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(lookupCode))
        {
            _logger.LogWarning("Vietinvoice PDF: payload không có trường 'Mã tra cứu'.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu Mã tra cứu. Không thể tải PDF từ Vietinvoice.");
        }

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
            await page.GoToAsync(SearchPageUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            var input = await page.WaitForSelectorAsync("input.search-input", new WaitForSelectorOptions
            {
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);
            if (input == null)
                return new InvoicePdfResult.Failure("Không tìm thấy ô nhập mã tra cứu trên trang Vietinvoice.");

            await input.ClickAsync().ConfigureAwait(false);
            await input.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await input.TypeAsync(lookupCode.Trim()).ConfigureAwait(false);

            var button = await page.QuerySelectorAsync("button.btn.btn-success").ConfigureAwait(false);
            if (button != null)
                await button.ClickAsync().ConfigureAwait(false);
            else
                await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);

            var iframe = await page.WaitForSelectorAsync("div.modal-body iframe[src]", new WaitForSelectorOptions
            {
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);
            if (iframe == null)
                return new InvoicePdfResult.Failure("Không tìm thấy iframe PDF sau khi tra cứu Vietinvoice.");

            var src = await iframe.EvaluateFunctionAsync<string?>("el => el.getAttribute('src')").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(src))
                return new InvoicePdfResult.Failure("Iframe PDF Vietinvoice không có src.");

            if (!Uri.TryCreate(src.Trim(), UriKind.Absolute, out var pdfUri))
            {
                if (!Uri.TryCreate(new Uri(SearchPageUrl), src.Trim(), out pdfUri))
                    return new InvoicePdfResult.Failure("URL PDF Vietinvoice không hợp lệ.");
            }

            _logger.LogDebug("Vietinvoice PDF: download {Url}", pdfUri);
            using var response = await _httpClient.GetAsync(pdfUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return new InvoicePdfResult.Failure("Phản hồi PDF từ Vietinvoice rỗng.");

            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"').Trim();
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var fromUri = Path.GetFileName(pdfUri.LocalPath);
                fileName = string.IsNullOrWhiteSpace(fromUri) ? $"vietinvoice-{lookupCode.Trim()}.pdf" : fromUri;
            }

            return new InvoicePdfResult.Success(bytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF Vietinvoice.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vietinvoice PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF Vietinvoice: " + ex.Message);
        }
        finally
        {
            if (browser != null)
                await browser.CloseAsync().ConfigureAwait(false);
        }
    }
}

