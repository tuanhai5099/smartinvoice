using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;
using SmartInvoice.Captcha.Vnpt;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ cổng VNPT/SmartCA dành cho merchant (siêu thị, cửa hàng) – dựa trên MST người bán + MCCQT.
/// Trường hợp đầu tiên: LOTTE MART BDG (MST 0304741634-003) tại
/// https://lottemart-bdg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey.
///
/// Quy trình:
/// - Đọc MCCQT (mhdon) từ payload JSON – dùng làm mã tra cứu hóa đơn (FKey).
/// - Xác định cấu hình merchant theo Mã số thuế người bán (nbmst); hiện tại hỗ trợ 0304741634-003.
/// - Mở URL SearchByFkey, điền FKey vào #strFkey.
/// - Chụp ảnh captcha ở /Captcha/Show (element img.captcha_img), preprocess trong Captcha service, gửi solver tối đa 3 lần.
/// - Điền captcha vào input#captch, nhấn Enter để submit form.
/// - Đợi bảng kết quả xuất hiện, tìm dòng đầu tiên và link "Tải file pdf" trong cột Tải file.
/// - Lấy href, chuyển thành URL tuyệt đối rồi tải bytes PDF trả về.
/// </summary>
[InvoiceProvider("VNPT-MERCHANT", InvoiceProviderMatchKind.SellerTaxCode, MayRequireUserIntervention = true)]
// MST NCC trên payload (msttcgp) – Tập đoàn VNPT; map sang cùng fetcher merchant (subdomain theo nbmst trong ResolveMerchantConfig).
[InvoiceProvider("0100684378", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true)]
public sealed class MerchantVnptInvoiceFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>MST nhà cung cấp dịch vụ VNPT – dùng để mapping registry theo tvandnkntt khi có.</summary>
    public string ProviderKey => "VNPT-MERCHANT";

    private const int PageLoadTimeoutMs = 45000;
    private const int CaptchaMaxRetries = 3;
    private const int AfterSubmitDelayMs = 2000;

    private readonly ILogger _logger;

    public MerchantVnptInvoiceFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(MerchantVnptInvoiceFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new InvoicePdfResult.Failure("Payload hóa đơn trống.");

        string? mccqt = null;
        string? sellerTaxCode = null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            mccqt = TryGetString(r, "mhdon");
            sellerTaxCode = TryGetString(r, "nbmst");
        }
        catch (JsonException)
        {
            return new InvoicePdfResult.Failure("Payload JSON không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(mccqt))
            return new InvoicePdfResult.Failure("Payload không có MCCQT (mhdon) – không thể tra cứu trên VNPT.");

        var merchantConfig = ResolveMerchantConfig(sellerTaxCode);
        if (merchantConfig == null)
        {
            _logger.LogWarning("Merchant VNPT PDF: MST người bán '{TaxCode}' chưa được cấu hình.", sellerTaxCode ?? "(null)");
            return new InvoicePdfResult.Failure("Nhà cung cấp này chưa được cấu hình cho VNPT merchant fetcher.");
        }

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_MerchantVnptPdf", Guid.NewGuid().ToString("N")[..8]);
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
                Headless = false,
                ExecutablePath = installedBrowser.GetExecutablePath(),
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            };

            browser = await Puppeteer.LaunchAsync(options).ConfigureAwait(false);
            var page = await browser.NewPageAsync().ConfigureAwait(false);

            _logger.LogDebug("Merchant VNPT PDF: mở {Url}", merchantConfig.SearchUrl);

            await page.GoToAsync(merchantConfig.SearchUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Điền mã tra cứu (FKey) = MCCQT.
            var fkeyInput = await page.WaitForSelectorAsync("input#strFkey",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (fkeyInput == null)
                return new InvoicePdfResult.Failure("Trang VNPT không có ô nhập mã nhận hóa đơn (input#strFkey).");

            await fkeyInput.ClickAsync().ConfigureAwait(false);
            await fkeyInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await fkeyInput.TypeAsync(mccqt.Trim()).ConfigureAwait(false);

            // Giải captcha tối đa 3 lần.
            string? solvedCaptcha = null;
            for (var attempt = 0; attempt < CaptchaMaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var captchaImg = await page.QuerySelectorAsync("img.captcha_img").ConfigureAwait(false);
                if (captchaImg == null)
                    return new InvoicePdfResult.Failure("Không tìm thấy ảnh captcha (img.captcha_img) trên trang VNPT.");

                var captchaPath = Path.Combine(downloadDir, $"vnpt_captcha_{attempt + 1}.png");
                await captchaImg.ScreenshotAsync(
                    captchaPath,
                    new ElementScreenshotOptions { Type = ScreenshotType.Png }).ConfigureAwait(false);

                // VNPT merchant: luôn preprocess Contrast trước khi giải captcha, không dùng các hiệu ứng khác.
                solvedCaptcha = VnptCaptchaHelper.SolveFromFileWithContrast(captchaPath)?.Trim();

                // Captcha VNPT dạng 4 ký tự (thường là số). Cắt gọn lại cho an toàn.
                if (!string.IsNullOrEmpty(solvedCaptcha) && solvedCaptcha.Length > 4)
                {
                    var filtered = new string(solvedCaptcha.Where(char.IsLetterOrDigit).ToArray());
                    if (filtered.Length >= 4)
                        solvedCaptcha = filtered[..4];
                    else
                        solvedCaptcha = solvedCaptcha[..4];
                }

                if (!string.IsNullOrWhiteSpace(solvedCaptcha))
                {
                    var captchaInput = await page.WaitForSelectorAsync("input#captch",
                        new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
                    if (captchaInput == null)
                        return new InvoicePdfResult.Failure("Trang VNPT không có ô captcha (input#captch).");

                    await captchaInput.ClickAsync().ConfigureAwait(false);
                    await captchaInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
                    await captchaInput.TypeAsync(solvedCaptcha).ConfigureAwait(false);

                    await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
                    await Task.Delay(AfterSubmitDelayMs, cancellationToken).ConfigureAwait(false);

                    // Nếu có thông báo lỗi captcha sai, thử lại.
                    string? errorText = null;
                    try
                    {
                        errorText = await page.EvaluateFunctionAsync<string?>(@"() => {
                            const el1 = document.querySelector('.field-validation-error, .text-danger');
                            const el2 = document.querySelector('.errorbox');
                            const t1 = el1 ? (el1.textContent || '').trim() : '';
                            const t2 = el2 ? (el2.textContent || '').trim() : '';
                            return (t1 + ' ' + t2).trim();
                        }").ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore – nếu context bị destroy vì navigation thì coi như captcha đúng.
                    }

                    if (string.IsNullOrEmpty(errorText) ||
                        (!errorText.Contains("sai", StringComparison.OrdinalIgnoreCase) &&
                         !errorText.Contains("không chính xác", StringComparison.OrdinalIgnoreCase)))
                    {
                        break;
                    }
                }

                if (attempt < CaptchaMaxRetries - 1)
                    await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            }

            // Đợi bảng kết quả.
            var pdfLink = await page.WaitForSelectorAsync(
                "div.look-up-records__table table.table-main tbody tr:first-child td a[title*='Tải file pdf']",
                new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (pdfLink == null)
                return new InvoicePdfResult.Failure("Không tìm thấy link 'Tải file pdf' trong bảng kết quả VNPT.");

            var href = await pdfLink.EvaluateFunctionAsync<string?>("el => el.getAttribute('href')").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(href))
                return new InvoicePdfResult.Failure("Link 'Tải file pdf' không có href.");

            _logger.LogInformation("Merchant VNPT PDF: click link tải file trong browser (tránh ERR_ABORTED khi server trả Content-Disposition: attachment)");

            // Server trả PDF với Content-Disposition: attachment → navigation bị ERR_ABORTED. Dùng download path + click link rồi đợi file.
            var cdp = await page.CreateCDPSessionAsync().ConfigureAwait(false);
            await cdp.SendAsync("Page.setDownloadBehavior", new Dictionary<string, object>
            {
                ["behavior"] = "allow",
                ["downloadPath"] = Path.GetFullPath(downloadDir)
            }).ConfigureAwait(false);

            await pdfLink.ClickAsync().ConfigureAwait(false);

            var pdfPath = await WaitForDownloadedPdfAsync(downloadDir, timeoutMs: 60000, cancellationToken).ConfigureAwait(false);
            if (pdfPath == null)
                return new InvoicePdfResult.Failure("Không nhận được file PDF sau khi click link tải từ cổng VNPT.");

            byte[] pdfBytes;
            try
            {
                pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Merchant VNPT PDF: lỗi đọc file {Path}", pdfPath);
                return new InvoicePdfResult.Failure("Lỗi đọc file PDF: " + ex.Message);
            }

            if (pdfBytes.Length == 0)
                return new InvoicePdfResult.Failure("File PDF tải từ VNPT rỗng.");

            var fileName = "invoice-vnpt.pdf";
            return new InvoicePdfResult.Success(pdfBytes, fileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF VNPT.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Merchant VNPT PDF: lỗi khi lấy PDF.");
            return new InvoicePdfResult.Failure("Lỗi lấy PDF VNPT merchant: " + ex.Message);
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

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>Đợi file PDF xuất hiện trong thư mục download (sau khi click link). Trả về đường dẫn file hoặc null nếu timeout.</summary>
    private static async Task<string?> WaitForDownloadedPdfAsync(string downloadDir, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string[]? lastPdfFiles = Array.Empty<string>();
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pdfFiles = Directory.Exists(downloadDir)
                ? Directory.GetFiles(downloadDir, "*.pdf")
                : Array.Empty<string>();
            if (pdfFiles.Length > 0)
            {
                var path = pdfFiles[0];
                var len = new FileInfo(path).Length;
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                if (new FileInfo(path).Length == len)
                    return path;
            }
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static MerchantConfig? ResolveMerchantConfig(string? sellerTaxCode)
    {
        if (string.IsNullOrWhiteSpace(sellerTaxCode))
            return null;

        var normalized = sellerTaxCode.Trim().Replace(" ", string.Empty);
        // LOTTE MART BDG: 0304741634-003
        if (string.Equals(normalized, "0304741634-003", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "https://lottemart-bdg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // LOTTE TỔNG (Lotte Tong): 0304741634 – trang lấy PDF gốc lottemart-nsg-tt78
        if (string.Equals(normalized, "0304741634", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "http://lottemart-nsg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // LOTTE MART NSG: 0702101089
        if (string.Equals(normalized, "0702101089", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "https://lottemart-nsg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // LOTTE MART VTU: 0304741634-005
        if (string.Equals(normalized, "0304741634-005", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "https://lottemart-vtu-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // LOTTE MART BDH: 0304741634-008
        if (string.Equals(normalized, "0304741634-008", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "https://lottemart-bdh-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // LOTTE MART CTO: 0304741634-007
        if (string.Equals(normalized, "0304741634-007", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "https://lottemart-cto-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // LOTTE MART NTG: 0304741634-011
        if (string.Equals(normalized, "0304741634-011", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "https://lottemart-ntg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // LOTTE MART DNI: 0304741634-001
        if (string.Equals(normalized, "0304741634-001", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "https://lottemart-dni-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // LOTTE MART BTN: 0304741634-002
        if (string.Equals(normalized, "0304741634-002", StringComparison.OrdinalIgnoreCase))
        {
            return new MerchantConfig(
                normalized,
                "https://lottemart-btn-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey");
        }

        // Sau này chỉ cần thêm các case khác (hoặc đọc từ config).
        return null;
    }

    private sealed record MerchantConfig(string SellerTaxCode, string SearchUrl);
}

