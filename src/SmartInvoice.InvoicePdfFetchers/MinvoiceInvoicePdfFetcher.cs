using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SmartInvoice.Application.Services;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>
/// Lấy PDF hóa đơn từ trang tra cứu M-invoice (mst nhà cung cấp 0106026495).
/// Trang: https://tracuuhoadon.minvoice.com.vn/tra-cuu-hoa-don
/// Điền: MST bên bán (nbmst trong payload) + Số bảo mật (cttkhac: ttruong chứa "Số bảo mật"/"SoBaoMat"), bấm "Tra cứu", chờ file PDF tải về.
/// </summary>
[InvoiceProvider("0106026495", InvoiceProviderMatchKind.ProviderTaxCode, MayRequireUserIntervention = true)]
public sealed class MinvoiceInvoicePdfFetcher : IKeyedInvoicePdfFetcher
{
    /// <summary>Mã số thuế nhà cung cấp dịch vụ hóa đơn (M-invoice).</summary>
    public string ProviderKey => "0106026495";

    private const string SearchPageUrl = "https://tracuuhoadon.minvoice.com.vn/tra-cuu-hoa-don";
    private const int PageLoadTimeoutMs = 45000;
    private const int DownloadWaitTimeoutMs = 30000;
    private const int DownloadPollIntervalMs = 500;

    private readonly ILogger _logger;

    public MinvoiceInvoicePdfFetcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(MinvoiceInvoicePdfFetcher));
    }

    public async Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var sellerTaxCode = GetSellerTaxCodeFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(sellerTaxCode))
        {
            _logger.LogWarning("Minvoice PDF: payload không có nbmst (MST bên bán).");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu mã số thuế bên bán (nbmst). Không thể tra cứu trên M-invoice.");
        }

        var secretCode = GetSecretCodeFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(secretCode))
        {
            _logger.LogWarning("Minvoice PDF: payload không có cttkhac với trường 'Số bảo mật'.");
            return new InvoicePdfResult.Failure("Hóa đơn thiếu 'Số bảo mật' trong cttkhac. Không thể tải PDF từ M-invoice.");
        }

        var downloadDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_MinvoicePdf", Guid.NewGuid().ToString("N")[..8]);
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

            _logger.LogDebug("Minvoice PDF: mở {Url}", SearchPageUrl);

            await page.GoToAsync(SearchPageUrl, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = PageLoadTimeoutMs
            }).ConfigureAwait(false);

            // Điền hai ô textbox: MST bên bán và Số bảo mật.
            var inputs = await page.QuerySelectorAllAsync("input").ConfigureAwait(false);
            if (inputs == null || inputs.Length < 2)
            {
                return new InvoicePdfResult.Failure("Trang tra cứu M-invoice không có đủ ô nhập liệu. Cấu trúc trang có thể đã thay đổi.");
            }

            // Giả định: input[0] = MST bên bán, input[1] = Số bảo mật.
            await inputs[0].ClickAsync().ConfigureAwait(false);
            await inputs[0].TypeAsync(sellerTaxCode).ConfigureAwait(false);

            await inputs[1].ClickAsync().ConfigureAwait(false);
            await inputs[1].TypeAsync(secretCode).ConfigureAwait(false);

            var filesBefore = Directory.GetFiles(downloadDir, "*.pdf", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Tìm nút có text "Tra cứu" và click.
            var buttons = await page.QuerySelectorAllAsync("button").ConfigureAwait(false);
            IElementHandle? traCuuButton = null;
            if (buttons != null)
            {
                foreach (var btn in buttons)
                {
                    var text = (await btn.EvaluateFunctionAsync<string>("el => (el.textContent || '').trim()").ConfigureAwait(false)) ?? string.Empty;
                    if (text.Contains("Tra cứu", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Tra cuu", StringComparison.OrdinalIgnoreCase))
                    {
                        traCuuButton = btn;
                        break;
                    }
                }
            }

            if (traCuuButton == null)
            {
                return new InvoicePdfResult.Failure("Không tìm thấy nút 'Tra cứu' trên trang M-invoice.");
            }

            await traCuuButton.ClickAsync().ConfigureAwait(false);

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
                            _logger.LogInformation("Minvoice PDF: đã tải {File} ({Size} bytes).", fileName, bytes.Length);
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

            return new InvoicePdfResult.Failure("Hết thời gian chờ tải PDF từ M-invoice. Kiểm tra MST/Số bảo mật hoặc thử lại sau.");
        }
        catch (OperationCanceledException)
        {
            return new InvoicePdfResult.Failure("Đã hủy lấy PDF.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Minvoice PDF: lỗi khi lấy PDF với MST {TaxCode}.", sellerTaxCode);
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

    private static string? GetSellerTaxCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (r.TryGetProperty("nbmst", out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lấy mã bí mật/tra cứu từ payload:
    /// - Ưu tiên cttkhac: item có ttruong chứa "Số bảo mật"/"SoBaoMat"/"So bao mat"
    ///   hoặc "Mã tra cứu"/"Ma tra cuu"/"MaTraCuu".
    /// - Sau đó thử ttkhac.ttchung (một số payload Minvoice nhét mã tra cứu ở đây).
    /// - Cuối cùng thử trực tiếp các field có tên tương đương trong root.
    /// </summary>
    private static string? GetSecretCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var fromCttkhac = GetSecretFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();

                var fromTtkhac = GetSecretFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return fromTtkhac.Trim();

                var direct = GetSecretFromDirectFields(candidate);
                if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> GetInvoiceRootCandidates(JsonElement r)
    {
        yield return r;
        if (r.ValueKind != JsonValueKind.Object) yield break;
        if (r.TryGetProperty("ndhdon", out var ndhdon) && ndhdon.ValueKind == JsonValueKind.Object)
            yield return ndhdon;
        if (r.TryGetProperty("hdon", out var hdon) && hdon.ValueKind == JsonValueKind.Object)
            yield return hdon;
        if (r.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
                yield return data;
            else if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                yield return data[0];
        }
    }

    private static bool IsSecretFieldName(string name)
    {
        var norm = SmartInvoice.Core.StringNormalization.NormalizeForComparison(name);
        return norm.Contains("sobamat", StringComparison.Ordinal)
               || norm.Contains("matracuu", StringComparison.Ordinal)
               || norm.Contains("keysearch", StringComparison.Ordinal);
    }

    private static string? GetSecretFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
            var ttStr = tt.GetString();
            if (string.IsNullOrWhiteSpace(ttStr)) continue;
            if (!IsSecretFieldName(ttStr)) continue;

            var dlieu = item.TryGetProperty("dlieu", out var dl) ? dl.GetString() : null;
            if (string.IsNullOrWhiteSpace(dlieu) && item.TryGetProperty("dLieu", out var dL))
                dlieu = dL.GetString();
            if (!string.IsNullOrWhiteSpace(dlieu)) return dlieu;
        }
        return null;
    }

    private static string? GetSecretFromTtkhac(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind != JsonValueKind.Object || !outer.TryGetProperty("ttchung", out var ttchung))
                continue;

            if (ttchung.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ttchung.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(tt) || !IsSecretFieldName(tt)) continue;

                    var dl = item.TryGetProperty("dlieu", out var dlEl) ? dlEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(dl) && item.TryGetProperty("dLieu", out var dL))
                        dl = dL.GetString();
                    if (!string.IsNullOrWhiteSpace(dl)) return dl;
                }
            }
            else if (ttchung.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in ttchung.EnumerateObject())
                {
                    if (!IsSecretFieldName(prop.Name)) continue;
                    var val = prop.Value;
                    if (val.ValueKind == JsonValueKind.String)
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                    if (val.ValueKind == JsonValueKind.Object)
                    {
                        var s = val.TryGetProperty("dlieu", out var dl) && dl.ValueKind == JsonValueKind.String
                            ? dl.GetString()
                            : (val.TryGetProperty("dLieu", out var dL) && dL.ValueKind == JsonValueKind.String ? dL.GetString() : null);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
        }
        return null;
    }

    private static string? GetSecretFromDirectFields(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in r.EnumerateObject())
        {
            if (!IsSecretFieldName(prop.Name)) continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var s = prop.Value.TryGetProperty("dlieu", out var dl) && dl.ValueKind == JsonValueKind.String
                    ? dl.GetString()
                    : (prop.Value.TryGetProperty("dLieu", out var dL) && dL.ValueKind == JsonValueKind.String ? dL.GetString() : null);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private static string NormalizeFieldName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var chars = input
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }
}

