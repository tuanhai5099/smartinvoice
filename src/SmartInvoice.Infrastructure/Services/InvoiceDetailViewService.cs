using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Services;

/// <summary>
/// L\u1EA5y HTML xem h\u00F3a \u0111\u01A1n: g\u1ECDi API detail, fill template C26TAA-31 (token), ghi file v\u00E0o th\u01B0 m\u1EE5c t\u1EA1m, tr\u1EA3 v\u1EC1 \u0111\u01B0\u1EDDng d\u1EABn file.
/// </summary>
public sealed class InvoiceDetailViewService : IInvoiceDetailViewService
{
    private const string TemplateInvoiceResource = "SmartInvoice.Application.Assets.InvoiceTemplate.invoice.html";
    private const string TemplateInvoiceMau06Resource = "SmartInvoice.Application.Assets.InvoiceTemplate.invoice-mau06.html";
    private const string TemplatePrintInvoiceResource = "SmartInvoice.Application.Assets.InvoiceTemplate.print-invoice.html";
    private const string TemplatePrintInvoiceMau06Resource = "SmartInvoice.Application.Assets.InvoiceTemplate.print-invoice-mau06.html";
    private const string TemplateDetailsJsResource = "SmartInvoice.Application.Assets.InvoiceTemplate.details.js";
    private const string ImageViewInvoiceBgResource = "SmartInvoice.Application.Assets.InvoiceTemplate.viewinvoice-bg.jpg";
    private const string ImageSignCheckResource = "SmartInvoice.Application.Assets.InvoiceTemplate.sign-check.jpg";

    private const string ScoUnavailableUserMessage =
        "Hi\u1EC7n t\u1EA1i h\u1EC7 th\u1ED1ng h\u00F3a \u0111\u01A1n \u0111i\u1EC7n t\u1EED kh\u00F4ng l\u1EA5y \u0111\u01B0\u1EE3c h\u00F3a \u0111\u01A1n t\u1EEB m\u00E1y t\u00EDnh ti\u1EC1n (SCO).";

    private readonly IUnitOfWork _uow;
    private readonly IHoaDonDienTuApiClient _apiClient;
    private readonly ICompanyAppService _companyService;
    private readonly ILogger _logger;

    public InvoiceDetailViewService(
        IUnitOfWork uow,
        IHoaDonDienTuApiClient apiClient,
        ICompanyAppService companyService,
        ILoggerFactory loggerFactory)
    {
        _uow = uow;
        _apiClient = apiClient;
        _companyService = companyService;
        _logger = loggerFactory.CreateLogger(nameof(InvoiceDetailViewService));
    }

    public async Task<(string? Html, string? Error)> GetInvoiceDetailHtmlAsync(Guid companyId, InvoiceDisplayDto inv, CancellationToken cancellationToken = default)
    {
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            return (null, "C\u00F4ng ty kh\u00F4ng t\u1ED3n t\u1EA1i.");
        var tokenValid = await _companyService.EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!tokenValid)
            return (null, "Token h\u1EBFt h\u1EA1n. Vui l\u00F2ng \u0111\u0103ng nh\u1EADp l\u1EA1i c\u00F4ng ty.");
        company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company?.AccessToken == null)
            return (null, "Kh\u00F4ng c\u00F3 token truy c\u1EADp.");

        var nbmst = inv.NbMst ?? company.TaxCode ?? company.Username ?? "";
        var khhdon = inv.KyHieu ?? "";
        if (string.IsNullOrEmpty(nbmst) || string.IsNullOrEmpty(khhdon))
            return (null, "Thi\u1EBFu MST ho\u1EB7c k\u00FD hi\u1EC7u h\u00F3a \u0111\u01A1n.");

        try
        {
            var existing = await _uow.Invoices.GetByExternalIdAsync(companyId, inv.Id, cancellationToken).ConfigureAwait(false);
            var masterJson = existing?.PayloadJson;

            // API detail: try sco-query / query order, flip endpoint on failure (avoids wrong-URL 500).
            var detailJson = await TryGetInvoiceDetailJsonWithFallbackAsync(
                company.AccessToken,
                nbmst,
                khhdon,
                inv.SoHoaDon,
                inv.Khmshdon,
                inv.MayTinhTien,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(detailJson) && PayloadJsonMayServeAsDetail(masterJson))
            {
                detailJson = masterJson;
                _logger.LogInformation("View invoice: using cached PayloadJson when API detail empty/failed for {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            }
            if (string.IsNullOrWhiteSpace(detailJson))
                return (null, "API kh\u00F4ng tr\u1EA3 v\u1EC1 d\u1EEF li\u1EC7u chi ti\u1EBFt.");
            if (existing != null)
            {
                existing.LineItemsJson = ExtractLineItemsJson(detailJson);
                existing.UpdatedAt = DateTime.UtcNow;
                await _uow.Invoices.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                await _uow.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var mauSo = DetectMauSo(masterJson, detailJson);
            var templateResource = mauSo.StartsWith("06", StringComparison.Ordinal) ? TemplateInvoiceMau06Resource : TemplateInvoiceResource;
            var templateHtml = await LoadEmbeddedTemplateAsync(templateResource).ConfigureAwait(false);
            if (string.IsNullOrEmpty(templateHtml))
                return (null, "Kh\u00F4ng \u0111\u1ECDc \u0111\u01B0\u1EE3c template h\u00F3a \u0111\u01A1n.");
            var filledHtml = InvoiceTemplateTokenReplacer.Fill(templateHtml, masterJson, detailJson);

            var tempDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_InvoiceView", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            var invoicePath = Path.Combine(tempDir, "invoice.html");
            await File.WriteAllTextAsync(invoicePath, filledHtml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var detailsJs = await LoadEmbeddedTemplateAsync(TemplateDetailsJsResource).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(detailsJs))
                await File.WriteAllTextAsync(Path.Combine(tempDir, "details.js"), detailsJs, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await CopyEmbeddedResourceToFileAsync(ImageViewInvoiceBgResource, Path.Combine(tempDir, "viewinvoice-bg.jpg"), cancellationToken).ConfigureAwait(false);
            await CopyEmbeddedResourceToFileAsync(ImageSignCheckResource, Path.Combine(tempDir, "sign-check.jpg"), cancellationToken).ConfigureAwait(false);
            return (invoicePath, null);
        }
        catch (OperationCanceledException ex) when (inv.MayTinhTien && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "SCO detail timeout for {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            return (null, ScoUnavailableUserMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get invoice detail failed for {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            return (null, "L\u1ED7i l\u1EA5y chi ti\u1EBFt: " + ex.Message);
        }
    }

    public async Task<(string? PrintPath, string? Error)> GetInvoicePrintHtmlPathAsync(Guid companyId, InvoiceDisplayDto inv, CancellationToken cancellationToken = default)
    {
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            return (null, "C\u00F4ng ty kh\u00F4ng t\u1ED3n t\u1EA1i.");
        var tokenValid = await _companyService.EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!tokenValid)
            return (null, "Token h\u1EBFt h\u1EA1n. Vui l\u00F2ng \u0111\u0103ng nh\u1EADp l\u1EA1i c\u00F4ng ty.");
        company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company?.AccessToken == null)
            return (null, "Kh\u00F4ng c\u00F3 token truy c\u1EADp.");

        var nbmst = inv.NbMst ?? company.TaxCode ?? company.Username ?? "";
        var khhdon = inv.KyHieu ?? "";
        if (string.IsNullOrEmpty(nbmst) || string.IsNullOrEmpty(khhdon))
            return (null, "Thi\u1EBFu MST ho\u1EB7c k\u00FD hi\u1EC7u h\u00F3a \u0111\u01A1n.");

        try
        {
            var existing = await _uow.Invoices.GetByExternalIdAsync(companyId, inv.Id, cancellationToken).ConfigureAwait(false);
            var masterJson = existing?.PayloadJson;

            var detailJson = await TryGetInvoiceDetailJsonWithFallbackAsync(
                company.AccessToken,
                nbmst,
                khhdon,
                inv.SoHoaDon,
                inv.Khmshdon,
                inv.MayTinhTien,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(detailJson) && PayloadJsonMayServeAsDetail(masterJson))
            {
                detailJson = masterJson;
                _logger.LogInformation("Print invoice: using cached PayloadJson when API detail empty/failed for {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            }
            if (string.IsNullOrWhiteSpace(detailJson))
                return (null, "API kh\u00F4ng tr\u1EA3 v\u1EC1 d\u1EEF li\u1EC7u chi ti\u1EBFt.");
            if (existing != null)
            {
                existing.LineItemsJson = ExtractLineItemsJson(detailJson);
                existing.UpdatedAt = DateTime.UtcNow;
                await _uow.Invoices.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                await _uow.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var mauSo = DetectMauSo(masterJson, detailJson);
            var templateResource = mauSo.StartsWith("06", StringComparison.Ordinal) ? TemplatePrintInvoiceMau06Resource : TemplatePrintInvoiceResource;
            var templateHtml = await LoadEmbeddedTemplateAsync(templateResource).ConfigureAwait(false);
            if (string.IsNullOrEmpty(templateHtml))
                return (null, "Kh\u00F4ng \u0111\u1ECDc \u0111\u01B0\u1EE3c template in h\u00F3a \u0111\u01A1n.");
            var filledHtml = InvoiceTemplateTokenReplacer.Fill(templateHtml, masterJson, detailJson);

            var tempDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_InvoicePrint", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            var printPath = Path.Combine(tempDir, "print-invoice.html");
            await File.WriteAllTextAsync(printPath, filledHtml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            var detailsJs = await LoadEmbeddedTemplateAsync(TemplateDetailsJsResource).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(detailsJs))
                await File.WriteAllTextAsync(Path.Combine(tempDir, "details.js"), detailsJs, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await CopyEmbeddedResourceToFileAsync(ImageViewInvoiceBgResource, Path.Combine(tempDir, "viewinvoice-bg.jpg"), cancellationToken).ConfigureAwait(false);
            await CopyEmbeddedResourceToFileAsync(ImageSignCheckResource, Path.Combine(tempDir, "sign-check.jpg"), cancellationToken).ConfigureAwait(false);

            return (printPath, null);
        }
        catch (OperationCanceledException ex) when (inv.MayTinhTien && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "SCO detail timeout for print {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            return (null, ScoUnavailableUserMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get invoice print HTML failed for {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            // L\u1ED7i d\u1EEF li\u1EC7u/template: tr\u1EA3 v\u1EC1 trang HTML l\u1ED7i \u0111\u1EC3 in thay v\u00EC ch\u1EC9 toast
            var isDataOrTemplateError = ex is JsonException or InvalidOperationException
                || ex.Message.Contains("current state of the object", StringComparison.OrdinalIgnoreCase);
            if (isDataOrTemplateError)
            {
                try
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "SmartInvoice_InvoicePrint", Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(tempDir);
                    var errorPath = Path.Combine(tempDir, "print-invoice.html");
                    await File.WriteAllTextAsync(errorPath, InvoiceTemplateTokenReplacer.GetErrorHtml(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                    return (errorPath, null);
                }
                catch
                {
                    // Fallback: tr\u1EA3 v\u1EC1 l\u1ED7i d\u1EA1ng message
                }
            }
            return (null, "L\u1ED7i t\u1EA1o trang in: " + ex.Message);
        }
    }

    public async Task<(IReadOnlyList<InvoiceRelativeItemDto> Items, string? Error)> GetInvoiceRelatedAsync(Guid companyId, InvoiceDisplayDto inv, CancellationToken cancellationToken = default)
    {
        var empty = Array.Empty<InvoiceRelativeItemDto>();

        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            return (empty, "C\u00F4ng ty kh\u00F4ng t\u1ED3n t\u1EA1i.");

        var tokenValid = await _companyService.EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!tokenValid)
            return (empty, "Token h\u1EBFt h\u1EA1n. Vui l\u00F2ng \u0111\u0103ng nh\u1EADp l\u1EA1i c\u00F4ng ty.");

        company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company?.AccessToken == null)
            return (empty, "Kh\u00F4ng c\u00F3 token truy c\u1EADp.");

        var nbmst = inv.NbMst ?? company.TaxCode ?? company.Username ?? "";
        var khhdon = inv.KyHieu ?? "";
        if (string.IsNullOrWhiteSpace(nbmst) || string.IsNullOrWhiteSpace(khhdon))
            return (empty, "Thi\u1EBFu MST ho\u1EB7c k\u00FD hi\u1EC7u h\u00F3a \u0111\u01A1n.");

        try
        {
            var trangThai = inv.TrangThaiDisplay ?? string.Empty;
            var isAdjustment = trangThai.Contains("\u0111i\u1EC1u ch\u1EC9nh", StringComparison.OrdinalIgnoreCase) ||
                               trangThai.Contains("dieu chinh", StringComparison.OrdinalIgnoreCase);

            var json = await _apiClient.GetInvoiceRelativeJsonAsync(
                company.AccessToken,
                nbmst,
                inv.Khmshdon,
                khhdon,
                inv.SoHoaDon,
                fromSco: inv.MayTinhTien,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
                return (empty, "API kh\u00F4ng tr\u1EA3 v\u1EC1 d\u1EEF li\u1EC7u h\u00F3a \u0111\u01A1n li\u00EAn quan.");

            string? adjustmentDescription = null;
            if (isAdjustment)
            {
                // L\u1EA5y th\u00EAm detail c\u1EE7a h\u00F3a \u0111\u01A1n \u0111i\u1EC1u ch\u1EC9nh \u0111\u1EC3 d\u1EF1ng n\u1ED9i dung \u0111i\u1EC1u ch\u1EC9nh (t\u1EEB chi ti\u1EBFt h\u00E0ng h\u00F3a).
                var detailJson = await TryGetInvoiceDetailJsonWithFallbackAsync(
                    company.AccessToken,
                    nbmst,
                    khhdon,
                    inv.SoHoaDon,
                    inv.Khmshdon,
                    inv.MayTinhTien,
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(detailJson))
                {
                    adjustmentDescription = ExtractAdjustmentDescription(detailJson);
                }
            }

            var items = ParseInvoiceRelativeItems(json, isAdjustment, adjustmentDescription);
            if (items.Count == 0)
                return (empty, "Kh\u00F4ng c\u00F3 h\u00F3a \u0111\u01A1n li\u00EAn quan.");

            return (items, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get invoice relative failed for {Khhdon}-{Shdon}", inv.KyHieu, inv.SoHoaDon);
            return (empty, "L\u1ED7i l\u1EA5y h\u00F3a \u0111\u01A1n li\u00EAn quan: " + ex.Message);
        }
    }

    private static bool PayloadJsonMayServeAsDetail(string? masterJson)
    {
        if (string.IsNullOrWhiteSpace(masterJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(masterJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (r.ValueKind != JsonValueKind.Object) return false;
            if (r.TryGetProperty("hdhhdvu", out var hd) && hd.ValueKind == JsonValueKind.Array && hd.GetArrayLength() > 0)
                return true;
        }
        catch
        {
            // ignored
        }
        return false;
    }

    private async Task<string?> TryGetInvoiceDetailJsonWithFallbackAsync(
        string accessToken,
        string nbmst,
        string khhdon,
        int soHoaDon,
        ushort khmshdon,
        bool scoFirst,
        CancellationToken cancellationToken)
    {
        try
        {
            var detailJson = await _apiClient
                .GetInvoiceDetailJsonAsync(accessToken, nbmst, khhdon, soHoaDon, khmshdon, scoFirst, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(detailJson))
                return detailJson;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // fallback l\u1EA7n 2
        }
        catch (Exception)
        {
            // fallback l\u1EA7n 2
        }

        try
        {
            var detailJson = await _apiClient
                .GetInvoiceDetailJsonAsync(accessToken, nbmst, khhdon, soHoaDon, khmshdon, !scoFirst, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(detailJson) ? null : detailJson;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Detail query timeout for invoice {Nbmst}-{Khhdon}-{Shdon}", nbmst, khhdon, soHoaDon);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Detail query failed for invoice {Nbmst}-{Khhdon}-{Shdon}", nbmst, khhdon, soHoaDon);
            return null;
        }
    }

    private static async Task CopyEmbeddedResourceToFileAsync(string resourceName, string destPath, CancellationToken cancellationToken)
    {
        var asm = Assembly.Load("SmartInvoice.Application");
        await using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return;
        await using var file = File.Create(destPath);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private static string? ExtractLineItemsJson(string detailJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                root = data[0];
            if (root.TryGetProperty("hdhhdvu", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.GetRawText();
        }
        catch { }
        return null;
    }

    private static IReadOnlyList<InvoiceRelativeItemDto> ParseInvoiceRelativeItems(string json, bool isAdjustment, string? adjustmentDescription)
    {
        var result = new List<InvoiceRelativeItemDto>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array)
        {
            // Tr\u01B0\u1EDDng h\u1EE3p API tr\u1EA3 v\u1EC1 tr\u1EF1c ti\u1EBFp l\u00E0 m\u1ED9t m\u1EA3ng c\u00E1c h\u00F3a \u0111\u01A1n li\u00EAn quan.
            arr = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("datas", out var datas) &&
                 datas.ValueKind == JsonValueKind.Array)
        {
            // M\u1ED9t s\u1ED1 API d\u00F9ng field "datas" (gi\u1ED1ng list h\u00F3a \u0111\u01A1n b\u00E1n ra).
            arr = datas;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("data", out var dataArr) &&
                 dataArr.ValueKind == JsonValueKind.Array)
        {
            // Ph\u1ECFng tr\u01B0\u1EDDng h\u1EE3p API d\u00F9ng "data" (kh\u00F4ng c\u00F3 "s").
            arr = dataArr;
        }
        else
        {
            // Kh\u00F4ng \u0111\u00FAng format mong \u0111\u1EE3i.
            return result;
        }

        var raw = new List<(ushort Khmshdon, string? Khhdon, int Shdon)>();
        foreach (var item in arr.EnumerateArray())
        {
            ushort khmshdon = 0;
            if (item.TryGetProperty("khmshdon", out var khmEl))
            {
                if (khmEl.ValueKind == JsonValueKind.Number)
                {
                    khmshdon = (ushort)khmEl.GetInt32();
                }
                else if (khmEl.ValueKind == JsonValueKind.String &&
                         ushort.TryParse(khmEl.GetString(), out var khmParsed))
                {
                    khmshdon = khmParsed;
                }
            }

            string? khhdon = null;
            if (item.TryGetProperty("khhdon", out var khhEl) && khhEl.ValueKind == JsonValueKind.String)
                khhdon = khhEl.GetString();

            var shdon = 0;
            if (item.TryGetProperty("shdon", out var shEl))
            {
                if (shEl.ValueKind == JsonValueKind.Number)
                {
                    shdon = shEl.GetInt32();
                }
                else if (shEl.ValueKind == JsonValueKind.String &&
                         int.TryParse(shEl.GetString(), out var shParsed))
                {
                    shdon = shParsed;
                }
            }

            raw.Add((khmshdon, khhdon, shdon));
        }

        if (raw.Count == 0)
            return result;

        // M\u00F4 ph\u1ECFng giao di\u1EC7n c\u1ED5ng GDT:
        // - D\u00F2ng \u0111\u1EA7u: "H\u00F3a \u0111\u01A1n \u0111ang tra c\u1EE9u"
        // - C\u00E1c d\u00F2ng sau: "H\u00F3a \u0111\u01A1n c\u00F3 li\u00EAn quan"
        // - \u00D4 "H\u00F3a \u0111\u01A1n g\u1ED1c" c\u1EE7a d\u00F2ng \u0111\u1EA7u: m\u00F4 t\u1EA3 d\u1EF1a tr\u00EAn d\u00F2ng li\u00EAn quan \u0111\u1EA7u ti\u00EAn (n\u1EBFu c\u00F3).
        string hoaDonGocForFirst = string.Empty;
        if (raw.Count > 1)
        {
            var baseInv = raw[1];
            var prefix = isAdjustment ? "\u0110i\u1EC1u ch\u1EC9nh cho h\u00F3a \u0111\u01A1n c\u00F3 k\u00FD hi\u1EC7u m\u1Eabu s\u1ED1" : "Thay th\u1EBF cho h\u00F3a \u0111\u01A1n c\u00F3 k\u00FD hi\u1EC7u m\u1Eabu s\u1ED1";
            hoaDonGocForFirst =
                $"{prefix} {baseInv.Khmshdon}, k\u00FD hi\u1EC7u h\u00F3a \u0111\u01A1n {baseInv.Khhdon}, s\u1ED1 h\u00F3a \u0111\u01A1n {baseInv.Shdon}";
        }

        for (var i = 0; i < raw.Count; i++)
        {
            var (khmshdon, khhdon, shdon) = raw[i];
            var index = i + 1;
            var loai = i == 0 ? "H\u00F3a \u0111\u01A1n \u0111ang tra c\u1EE9u" : "H\u00F3a \u0111\u01A1n c\u00F3 li\u00EAn quan";
            var hoaDonGoc = i == 0 ? hoaDonGocForFirst : string.Empty;
            var noiDungDieuChinh = (isAdjustment && i == 0) ? adjustmentDescription : null;
            result.Add(new InvoiceRelativeItemDto(index, loai, khmshdon, khhdon, shdon, hoaDonGoc, noiDungDieuChinh));
        }

        return result;
    }

    private static string? ExtractAdjustmentDescription(string detailJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var data) &&
                     data.ValueKind == JsonValueKind.Array &&
                     data.GetArrayLength() > 0)
                root = data[0];

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("hdhhdvu", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                if (!root.TryGetProperty("dshhdvu", out arr) || arr.ValueKind != JsonValueKind.Array)
                    return null;
            }

            var names = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = GetProductNameForAdjustment(item);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }

            if (names.Count == 0)
                return null;

            return string.Join("; ", names);
        }
        catch
        {
            return null;
        }
    }

    private static string GetProductNameForAdjustment(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return "";

        string? GetStr(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        var s = GetStr(item, "thhdvu") ?? GetStr(item, "THHDVu") ?? GetStr(item, "ten") ?? GetStr(item, "thhddvu") ?? GetStr(item, "name");
        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();

        if (item.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in ttkhac.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("ttruong", out var tt)) continue;
                if (tt.ValueKind != JsonValueKind.String) continue;
                var ttStr = tt.GetString();
                if (string.IsNullOrEmpty(ttStr)) continue;
                if (ttStr.Contains("T\u00EAn h\u00E0ng", StringComparison.OrdinalIgnoreCase) ||
                    ttStr.Contains("T\u00EAn s\u1EA3n ph\u1EA9m", StringComparison.OrdinalIgnoreCase) ||
                    ttStr.Contains("T\u00EAn h\u00E0ng h\u00F3a", StringComparison.OrdinalIgnoreCase) ||
                    (ttStr.Contains("T\u00EAn", StringComparison.Ordinal) && ttStr.Trim().Length <= 10))
                {
                    var dlieu = GetStr(entry, "dlieu") ?? GetStr(entry, "dLieu");
                    if (!string.IsNullOrWhiteSpace(dlieu))
                        return dlieu.Trim();
                }
            }
        }

        return "";
    }

    private static async Task<string?> LoadEmbeddedTemplateAsync(string resourceName)
    {
        try
        {
            var asm = Assembly.Load("SmartInvoice.Application");
            await using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string DetectMauSo(string? masterJson, string detailJson)
    {
        static string? TryDetectFromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var r = root;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    r = root[0];
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                    r = data[0];
                if (r.ValueKind != JsonValueKind.Object)
                    return null;
                if (r.TryGetProperty("khmshdon", out var khmEl))
                {
                    if (khmEl.ValueKind == JsonValueKind.String)
                        return khmEl.GetString();
                    if (khmEl.ValueKind == JsonValueKind.Number && khmEl.TryGetInt32(out var n))
                        return n.ToString(CultureInfo.InvariantCulture);
                }
                if (r.TryGetProperty("ndhdon", out var ndh) && ndh.ValueKind == JsonValueKind.Object && ndh.TryGetProperty("khmshdon", out khmEl))
                {
                    if (khmEl.ValueKind == JsonValueKind.String)
                        return khmEl.GetString();
                    if (khmEl.ValueKind == JsonValueKind.Number && khmEl.TryGetInt32(out var n2))
                        return n2.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // ignore and fallback
            }
            return null;
        }

        var fromMaster = TryDetectFromJson(masterJson);
        if (!string.IsNullOrWhiteSpace(fromMaster))
            return fromMaster!;
        var fromDetail = TryDetectFromJson(detailJson);
        return string.IsNullOrWhiteSpace(fromDetail) ? "1" : fromDetail!;
    }
}
