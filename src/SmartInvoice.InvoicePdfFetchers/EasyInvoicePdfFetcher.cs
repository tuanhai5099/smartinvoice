using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ EasyInvoice (NCC 0105987432): mở PortalLink từ cttkhac, điền Fkey, giải captcha 4 số,
/// bấm Tra cứu, đợi modal → bấm "Tải PDF & đính kèm" → tải zip → giải nén lấy file PDF.
/// </summary>
public sealed class EasyInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (EasyInvoice).</summary>
    public string ProviderKey => "0105987432";

    private const int PageLoadTimeoutMs = 45000;
    /// <summary>Chờ navigation sau "Tra cứu" — rút ngắn để tránh chờ lâu khi trang không reload (AJAX).</summary>
    private const int NavigationWaitTimeoutMs = 5000;
    private const int DownloadWaitTimeoutMs = 30000;
    /// <summary>Poll thư mục tải zip — giảm để phát hiện file sớm hơn.</summary>
    private const int DownloadPollIntervalMs = 200;
    private const int MaxCaptchaRetries = 3;

    private static readonly Regex FourDigits = new(@"^\d{4}$", RegexOptions.Compiled);

    private readonly ICaptchaSolverService _captchaSolver;
    private readonly ILogger _logger;

    public EasyInvoicePdfFetcher(ICaptchaSolverService captchaSolver, ILoggerFactory loggerFactory)
    {
        _captchaSolver = captchaSolver ?? throw new ArgumentNullException(nameof(captchaSolver));
        _logger = loggerFactory.CreateLogger(nameof(EasyInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var (portalLink, fkey) = GetPortalLinkAndFkeyFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(portalLink) || string.IsNullOrWhiteSpace(fkey))
        {
            _logger.LogWarning("EasyInvoice PDF: payload thiếu cttkhac PortalLink hoặc Fkey.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu link tra cứu hoặc FKey (cttkhac.PortalLink / cttkhac.Fkey). Không thể tải PDF từ EasyInvoice.");
        }

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_EasyInvoicePdf", Guid.NewGuid().ToString("N")[..8]);
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

            _logger.LogDebug("EasyInvoice PDF: mở {Url}", portalLink);

            await page.GoToAsync(portalLink.Trim(), new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Điền FKey vào #iFkey
            var fkeyInput = await page.WaitForSelectorAsync("#iFkey", new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (fkeyInput == null)
                return new InvoicePdfResult.Failure("Trang EasyInvoice không có ô FKey (#iFkey).");
            await fkeyInput.ClickAsync().ConfigureAwait(false);
            await fkeyInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await fkeyInput.TypeAsync(fkey.Trim()).ConfigureAwait(false);

            string? captchaText = null;
            for (var attempt = 0; attempt < MaxCaptchaRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Lấy ảnh captcha từ trang (fetch img#captcha src, trả về base64)
                var base64 = await page.EvaluateFunctionAsync<string>(@"async () => {
                    const img = document.querySelector('#captcha');
                    if (!img || !img.src) return null;
                    try {
                        const r = await fetch(img.src);
                        const buf = await r.arrayBuffer();
                        const bytes = new Uint8Array(buf);
                        let binary = '';
                        for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                        return btoa(binary);
                    } catch (e) { return null; }
                }").ConfigureAwait(false);

                if (string.IsNullOrEmpty(base64))
                {
                    return new InvoicePdfResult.Failure("Không lấy được ảnh captcha từ trang EasyInvoice.");
                }

                byte[] imageBytes;
                try
                {
                    imageBytes = Convert.FromBase64String(base64);
                }
                catch
                {
                    return new InvoicePdfResult.Failure("Dữ liệu ảnh captcha không hợp lệ.");
                }

                await using var imageStream = new MemoryStream(imageBytes);
                captchaText = (await _captchaSolver.SolveFromStreamAsync(imageStream, cancellationToken).ConfigureAwait(false))?.Trim();
                if (!string.IsNullOrEmpty(captchaText) && FourDigits.IsMatch(captchaText))
                    break;

                _logger.LogDebug("EasyInvoice PDF: captcha giải được không phải 4 số (attempt {Attempt}), thử lại.", attempt + 1);
                if (attempt < MaxCaptchaRetries - 1)
                {
                    await page.ReloadAsync().ConfigureAwait(false);
                    await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                    // Sau reload cần điền lại FKey
                    var fkeyAgain = await page.QuerySelectorAsync("#iFkey").ConfigureAwait(false);
                    if (fkeyAgain != null)
                    {
                        await fkeyAgain.ClickAsync().ConfigureAwait(false);
                        await fkeyAgain.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
                        await fkeyAgain.TypeAsync(fkey.Trim()).ConfigureAwait(false);
                    }
                }
            }

            if (string.IsNullOrEmpty(captchaText) || !FourDigits.IsMatch(captchaText))
            {
                return new InvoicePdfResult.Failure("Không giải được mã captcha 4 số sau vài lần thử. Vui lòng thử lại.");
            }

            // Điền mã xác thực vào #Capcha (ghi chú: trang dùng id "Capcha")
            var capchaInput = await page.WaitForSelectorAsync("#Capcha", new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (capchaInput == null)
                return new InvoicePdfResult.Failure("Trang EasyInvoice không có ô mã xác thực (#Capcha).");
            await capchaInput.ClickAsync().ConfigureAwait(false);
            await capchaInput.EvaluateFunctionAsync("el => el.value = ''").ConfigureAwait(false);
            await capchaInput.TypeAsync(captchaText).ConfigureAwait(false);

            // Bấm "Tra cứu" — có thể gây navigation (reload), cần chờ xong rồi mới query DOM để tránh "Execution context was destroyed"
            var btnSubmit = await page.WaitForSelectorAsync("#btnSubmit", new WaitForSelectorOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            if (btnSubmit == null)
                return new InvoicePdfResult.Failure("Trang EasyInvoice không có nút Tra cứu (#btnSubmit).");

            var navTask = page.WaitForNavigationAsync(new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = NavigationWaitTimeoutMs
            });
            await btnSubmit.ClickAsync().ConfigureAwait(false);
            try
            {
                await navTask.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Không có navigation (trang cập nhật AJAX), tiếp tục bình thường
            }

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);

            // Cố gắng tìm nút "Tải PDF & đính kèm" tối đa 3 lần, mỗi lần nghỉ 80 ms
            const int downloadButtonRetries = 3;
            const int downloadButtonRetryDelayMs = 80;
            IElementHandle? btnDownload = null;
            for (var attempt = 0; attempt < downloadButtonRetries; attempt++)
            {
                btnDownload = await page.QuerySelectorAsync("button[name=\"downloadPdfAndFileAttach\"], button[value=\"downloadPdfAndFileAttach\"]").ConfigureAwait(false);
                if (btnDownload != null) break;
                if (attempt < downloadButtonRetries - 1)
                    await Task.Delay(downloadButtonRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            if (btnDownload == null)
                return new InvoicePdfResult.Failure("Không tìm thấy nút Tải PDF & đính kèm trong modal.");

            var filesBefore = Directory.GetFiles(downloadDir, "*.zip", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToHashSet();

            // Lấy attribute onclick của nút (hàm gọi tải PDF) và thực thi trong context JavaScript thay vì click
            var onclickValue = await btnDownload.EvaluateFunctionAsync<string>("el => el.getAttribute('onclick') || ''").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(onclickValue))
                return new InvoicePdfResult.Failure("Nút Tải PDF & đính kèm không có attribute onclick.");
            await page.EvaluateFunctionAsync("(code) => eval(code)", onclickValue).ConfigureAwait(false);

            // Đợi file zip tải về
            string? zipPath = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(DownloadWaitTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var zips = Directory.GetFiles(downloadDir, "*.zip", SearchOption.TopDirectoryOnly);
                zipPath = zips.Select(Path.GetFullPath).FirstOrDefault(f => !filesBefore.Contains(Path.GetFileName(f)));
                if (zipPath != null)
                {
                    try
                    {
                        using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            if (fs.Length > 0) break;
                        }
                    }
                    catch (IOException)
                    {
                        zipPath = null;
                    }
                }
                await Task.Delay(DownloadPollIntervalMs, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
                return new InvoicePdfResult.Failure("Hết thời gian chờ tải file zip từ EasyInvoice.");

            // Giải nén zip, lấy file PDF đầu tiên
            byte[]? pdfBytes = null;
            string? pdfFileName = null;
            await using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var pdfEntry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && e.Length > 0);
                if (pdfEntry == null)
                    return new InvoicePdfResult.Failure("Trong file zip không có file PDF hóa đơn.");

                pdfFileName = pdfEntry.Name;
                await using var entryStream = pdfEntry.Open();
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                pdfBytes = ms.ToArray();
            }

            if (pdfBytes == null || pdfBytes.Length == 0)
                return new InvoicePdfResult.Failure("Đọc file PDF từ zip thất bại.");

            _logger.LogInformation("EasyInvoice PDF: đã tải {File} ({Size} bytes).", pdfFileName, pdfBytes.Length);
            return new InvoicePdfResult.Success(pdfBytes, pdfFileName);
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EasyInvoice PDF: lỗi khi lấy PDF.");
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

    /// <summary>Lấy PortalLink và Fkey từ payload: ưu tiên cttkhac (ttruong = "PortalLink" / "Fkey"), fallback ttkhac nếu NCC đẩy vào đó.</summary>
    private static (string? PortalLink, string? Fkey) GetPortalLinkAndFkeyFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            string? portalLink = null;
            string? fkey = null;

            // 1) Ưu tiên đọc từ cttkhac (PortalLink / Fkey) – giống cấu hình cũ
            if (r.TryGetProperty("cttkhac", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                    var ttStr = tt.GetString();
                    if (string.IsNullOrWhiteSpace(ttStr)) continue;

                    var dlieu = item.TryGetProperty("dlieu", out var dl) ? dl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(dlieu) && item.TryGetProperty("dLieu", out var dL))
                        dlieu = dL.GetString();
                    var value = string.IsNullOrWhiteSpace(dlieu) ? null : dlieu.Trim();

                    if (string.Equals(ttStr, "PortalLink", StringComparison.OrdinalIgnoreCase))
                        portalLink = value;
                    else if (string.Equals(ttStr, "Fkey", StringComparison.OrdinalIgnoreCase))
                        fkey = value;

                    if (portalLink != null && fkey != null) break;
                }
            }

            // 2) Fallback: một số payload EasyInvoice có thể đẩy PortalLink/Fkey trong ttkhac
            if ((portalLink == null || fkey == null) && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    // Kiểu 1: giống cttkhac – có ttruong + dlieu/dLieu
                    if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
                    {
                        var ttStr = tt.GetString();
                        if (!string.IsNullOrWhiteSpace(ttStr))
                        {
                            var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                                : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

                            if (string.Equals(ttStr.Trim(), "PortalLink", StringComparison.OrdinalIgnoreCase) && portalLink == null)
                                portalLink = value;
                            else if (string.Equals(ttStr.Trim(), "Fkey", StringComparison.OrdinalIgnoreCase) && fkey == null)
                                fkey = value;
                        }
                    }

                    // Kiểu 2: PortalLink/Fkey là property trong ttchung bên trong ttkhac
                    if (item.TryGetProperty("ttchung", out var ttchung) && ttchung.ValueKind == JsonValueKind.Object)
                    {
                        if (portalLink == null && ttchung.TryGetProperty("PortalLink", out var p) && p.ValueKind == JsonValueKind.String)
                        {
                            var s = p.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) portalLink = s.Trim();
                        }
                        if (fkey == null && ttchung.TryGetProperty("Fkey", out var fk) && fk.ValueKind == JsonValueKind.String)
                        {
                            var s = fk.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) fkey = s.Trim();
                        }
                    }

                    if (portalLink != null && fkey != null) break;
                }
            }

            return (portalLink, fkey);
        }
        catch
        {
            return (null, null);
        }
    }
}
