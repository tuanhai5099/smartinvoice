using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;
using SmartInvoice.Infrastructure.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Use case: từ payload JSON đầy đủ của hóa đơn, đọc tvandnkntt, chọn fetcher và gọi FetchPdfAsync.
/// </summary>
public sealed class InvoicePdfService : IInvoicePdfService
{
    private readonly IInvoicePdfFetcherRegistry _registry;
    private readonly IInvoiceLookupProviderRegistry _lookupRegistry;
    private readonly IUnitOfWork _uow;
    private readonly SmartInvoice.Application.Services.IInvoiceSyncService _invoiceSyncService;
    private readonly ILogger _logger;

    /// <summary>
    /// Danh sách key nhà cung cấp (msttcgp / tvanDnKntt) yêu cầu phải có XML trước khi lấy PDF.
    /// Ví dụ: eHoadon/BKAV (0101360697), iHoadon/EFY (0102519041), VDSG (0314058603).
    /// </summary>
    private static readonly HashSet<string> ProviderKeysRequireXml = new(StringComparer.OrdinalIgnoreCase)
    {
        "0101360697", // eHoadon/BKAV: cần DLHDon/@Id
        "0102519041", // iHoadon: bắt buộc upload XML lên trang kiểm tra
        "0314058603"  // VDSG: cần đọc MTCuu từ XML TTKhac/TTin/DLieu
    };

    public InvoicePdfService(
        IInvoicePdfFetcherRegistry registry,
        IInvoiceLookupProviderRegistry lookupRegistry,
        IUnitOfWork uow,
        SmartInvoice.Application.Services.IInvoiceSyncService invoiceSyncService,
        ILoggerFactory loggerFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _lookupRegistry = lookupRegistry ?? throw new ArgumentNullException(nameof(lookupRegistry));
        _uow = uow ?? throw new ArgumentNullException(nameof(uow));
        _invoiceSyncService = invoiceSyncService ?? throw new ArgumentNullException(nameof(invoiceSyncService));
        _logger = loggerFactory.CreateLogger(nameof(InvoicePdfService));
    }

    public async Task<InvoicePdfResult> GetPdfForInvoiceByExternalIdAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return new InvoicePdfResult.Failure("Mã hóa đơn trống.");
        var invoice = await _uow.Invoices.GetByExternalIdAsync(companyId, externalId, cancellationToken).ConfigureAwait(false);
        if (invoice == null || string.IsNullOrWhiteSpace(invoice.PayloadJson))
            return new InvoicePdfResult.Failure("Không tìm thấy hóa đơn hoặc dữ liệu liên quan.");

        var providerKey = GetProviderKeyFromPayload(invoice.PayloadJson);
        if (IsEasyInvoiceProvider(invoice.PayloadJson))
            providerKey = EasyInvoiceProviderKey;
        var normalizedKey = NormalizeProviderKey(providerKey);

        // Một số nhà cung cấp (như eHoadon/BKAV) cần XML (DLHDon/@Id) để lấy PDF.
        if (!string.IsNullOrEmpty(normalizedKey) && ProviderKeysRequireXml.Contains(normalizedKey))
        {
            var xmlContent = await TryGetInvoiceXmlContentAsync(companyId, invoice, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xmlContent))
            {
                // Chưa có XML local → tự tải XML cho đúng hóa đơn rồi thử lại một lần.
                try
                {
                    var displayList = await _invoiceSyncService
                        .GetInvoicesByIdsAsync(companyId, new[] { invoice.ExternalId }, cancellationToken)
                        .ConfigureAwait(false);
                    if (displayList.Count > 0)
                    {
                        // Thư mục gốc theo công ty (giống BackgroundJobService và TryGetInvoiceXmlContentAsync).
                        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
                        var companyCode = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? companyId.ToString("N")[..8];
                        var companyRoot = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCode);
                        await _invoiceSyncService
                            .DownloadInvoicesXmlAsync(companyId, displayList, companyRoot, progress: null, cancellationToken, zipOutputDirectory: companyRoot)
                            .ConfigureAwait(false);

                        // Đọc lại XML sau khi tải.
                        xmlContent = await TryGetInvoiceXmlContentAsync(companyId, invoice, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Tải XML tự động cho provider {ProviderKey} thất bại.", providerKey);
                }
            }

            if (string.IsNullOrWhiteSpace(xmlContent))
                return new InvoicePdfResult.Failure("Không tìm thấy thông tin để lấy hóa đơn.");

            var fetcher = _registry.GetFetcher(providerKey);
            _logger.LogDebug("PDF fetcher selected for XML-based provider key '{Key}'.", providerKey ?? "(none)");
            return await fetcher.FetchPdfAsync(xmlContent, cancellationToken).ConfigureAwait(false);
        }

        // Bất kể có providerKey hay không: nếu MST người bán được map sang fetcher chuyên biệt
        // (ví dụ VNPT merchant) thì ưu tiên dùng fetcher đó. Điều này xử lý được cả các hóa đơn
        // máy tính tiền KHÔNG có providerKey lẫn các hóa đơn merchant có providerKey nhưng
        // không có fetcher riêng cho NCC, chỉ map theo MST người bán.
        var sellerKey = NormalizeSellerTaxCode(invoice.NbMst);
        if (!string.IsNullOrEmpty(sellerKey) &&
            SellerTaxCodeToFetcherKey.TryGetValue(sellerKey, out var mappedFetcherKey))
        {
            var fetcher = _registry.GetFetcher(mappedFetcherKey);
            _logger.LogDebug(
                "PDF fetcher selected by seller tax code '{TaxCode}' → '{FetcherKey}' (provider key = '{ProviderKey}').",
                sellerKey,
                mappedFetcherKey,
                providerKey ?? "(none)");
            return await fetcher.FetchPdfAsync(invoice.PayloadJson, cancellationToken).ConfigureAwait(false);
        }

        // Mặc định: provider chỉ cần payload JSON (sử dụng providerKey nếu có, hoặc fallback fetcher).
        return await GetPdfForInvoiceAsync(invoice.PayloadJson, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InvoiceLookupSuggestion?> GetLookupSuggestionAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        var invoice = await _uow.Invoices.GetByExternalIdAsync(companyId, externalId, cancellationToken).ConfigureAwait(false);
        if (invoice == null || string.IsNullOrWhiteSpace(invoice.PayloadJson))
            return null;

        var providerKey = GetProviderKeyFromPayload(invoice.PayloadJson);
        if (IsEasyInvoiceProvider(invoice.PayloadJson))
            providerKey = EasyInvoiceProviderKey;
        if (string.IsNullOrWhiteSpace(providerKey))
            return null;

        var provider = _lookupRegistry.GetProvider(providerKey);
        if (provider == null)
            return null;

        return provider.GetSuggestion(invoice.PayloadJson, invoice.NbMst);
    }

    public async Task<InvoicePdfResult> GetPdfForInvoiceAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return new InvoicePdfResult.Failure("Payload hóa đơn trống.");

        var providerKey = GetProviderKeyFromPayload(payloadJson);
        if (IsEasyInvoiceProvider(payloadJson))
            providerKey = EasyInvoiceProviderKey;
        var fetcher = _registry.GetFetcher(providerKey);
        _logger.LogDebug("PDF fetcher selected for provider key '{Key}'.", providerKey ?? "(none)");
        return await fetcher.FetchPdfAsync(payloadJson, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Mã số thuế NCC EasyInvoice — dùng như key fetcher chuẩn, nhưng có thể chọn theo PortalLink thay vì cấu hình MST.</summary>
    private const string EasyInvoiceProviderKey = "0105987432";

    /// <summary>
    /// Đọc key nhà cung cấp (msttcgp = mã số thuế tổ chức cung cấp dịch vụ) từ payload JSON.
    /// Chỉ đọc ở root, property msttcgp — KHÔNG còn dùng tvanDnKntt để tránh map nhầm nhà cung cấp.
    /// </summary>
    private static string? GetProviderKeyFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            foreach (var prop in r.EnumerateObject())
            {
                if (string.Equals(prop.Name, "msttcgp", StringComparison.OrdinalIgnoreCase))
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Nhận diện nhà cung cấp EasyInvoice trực tiếp từ payload:
    /// - Tìm PortalLink trong cttkhac hoặc ttkhac (ttruong = \"PortalLink\" hoặc ttchung.PortalLink).
    /// - Nếu PortalLink chứa easyinvoice.vn hoặc easy-invoice.com thì coi là EasyInvoice, bất kể tvandnkntt là gì.
    /// </summary>
    private static bool IsEasyInvoiceProvider(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            string? portalLink = null;

            // 1) cttkhac
            if (r.TryGetProperty("cttkhac", out var cttkhac) && cttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                    var ttStr = tt.GetString();
                    if (string.IsNullOrWhiteSpace(ttStr)) continue;
                    if (!string.Equals(ttStr.Trim(), "PortalLink", StringComparison.OrdinalIgnoreCase)) continue;

                    var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                        : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        portalLink = raw.Trim();
                        break;
                    }
                }
            }

            // 2) ttkhac (kiểu 1: item.ttruong = PortalLink)
            if (portalLink == null && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
                    {
                        var ttStr = tt.GetString();
                        if (!string.IsNullOrWhiteSpace(ttStr) &&
                            string.Equals(ttStr.Trim(), "PortalLink", StringComparison.OrdinalIgnoreCase))
                        {
                            var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                                : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                            if (!string.IsNullOrWhiteSpace(raw))
                            {
                                portalLink = raw.Trim();
                                break;
                            }
                        }
                    }

                    if (portalLink != null) break;

                    // 3) ttkhac.ttchung.PortalLink
                    if (item.TryGetProperty("ttchung", out var ttchung) && ttchung.ValueKind == JsonValueKind.Object)
                    {
                        if (ttchung.TryGetProperty("PortalLink", out var p) && p.ValueKind == JsonValueKind.String)
                        {
                            var s = p.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                portalLink = s.Trim();
                                break;
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(portalLink)) return false;
            var link = portalLink.Trim();
            return link.Contains("easyinvoice.vn", StringComparison.OrdinalIgnoreCase)
                   || link.Contains("easy-invoice.com", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeProviderKey(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            return string.Empty;
        // Một số API trả "0101360697-abc" hoặc " 0101360697 "; chỉ dùng phần MST số phía trước.
        var trimmed = providerKey.Trim();
        var firstDash = trimmed.IndexOf('-');
        if (firstDash > 0)
            trimmed = trimmed[..firstDash];
        return trimmed;
    }

    /// <summary>Map MST người bán → key fetcher chuyên biệt (ví dụ VNPT merchant).</summary>
    private static readonly Dictionary<string, string> SellerTaxCodeToFetcherKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // VDSG – Viễn Thông Đông Sài Gòn: luôn dùng fetcher VDSG, không fallback Viettel.
        ["0314058603"] = "0314058603",
        // LOTTE TỔNG – trang PDF gốc lottemart-nsg-tt78
        ["0304741634"] = "VNPT-MERCHANT",
        // LOTTE MART BDG – dùng VNPT merchant portal SearchByFkey
        ["0304741634-003"] = "VNPT-MERCHANT",
        // LOTTE MART NSG – dùng VNPT merchant portal SearchByFkey
        ["0702101089"] = "VNPT-MERCHANT",
        // LOTTE MART VTU
        ["0304741634-005"] = "VNPT-MERCHANT",
        // LOTTE MART BDH
        ["0304741634-008"] = "VNPT-MERCHANT",
        // LOTTE MART CTO
        ["0304741634-007"] = "VNPT-MERCHANT",
        // LOTTE MART NTG
        ["0304741634-011"] = "VNPT-MERCHANT",
        // LOTTE MART DNI
        ["0304741634-001"] = "VNPT-MERCHANT",
        // LOTTE MART BTN
        ["0304741634-002"] = "VNPT-MERCHANT",
        // WinCommerce / WinMart – lấy PDF qua trang hoadon.winmart.vn theo MCCQT
        ["0104918404"] = "WIN-INVOICE"
    };

    private static string NormalizeSellerTaxCode(string? taxCode)
    {
        if (string.IsNullOrWhiteSpace(taxCode))
            return string.Empty;
        // Một số hệ thống có thể chèn khoảng trắng trong MST chi nhánh (vd. "0304741634 -003"),
        // nên loại bỏ toàn bộ khoảng trắng để map ổn định.
        return taxCode.Trim().Replace(" ", string.Empty);
    }


    /// <summary>
    /// Thử tìm và đọc nội dung XML của một hóa đơn từ thư mục lưu file:
    /// - companyRoot (Documents\SmartInvoice\{companyCode}\yyyy_MM\)
    /// - ExportXml (Documents\SmartInvoice\ExportXml\yyyy_MM\)
    /// XML được lưu với baseName = "{KyHieu}-{SoHoaDon}".
    /// </summary>
    private static async Task<string?> TryReadInvoiceXmlAsync(string companyRoot, Invoice invoice, CancellationToken cancellationToken)
    {
        static string GetBaseName(Invoice inv)
        {
            var kh = inv.KyHieu ?? "";
            kh = InvoiceFileStoragePathHelper.SanitizeFileName(kh);
            return $"{kh}-{inv.SoHoaDon}";
        }

        var baseName = string.IsNullOrWhiteSpace(invoice.XmlBaseName)
            ? GetBaseName(invoice)
            : invoice.XmlBaseName!;

        var monthFolder = InvoiceFileStoragePathHelper.GetMonthYearPath(companyRoot, invoice.NgayLap);
        string? xmlPath = null;

        // 1. Thư mục đúng tháng_năm + thư mục con baseName (cấu trúc chuẩn khi tải XML).
        var destDir = Path.Combine(monthFolder, baseName);
        if (Directory.Exists(destDir))
            xmlPath = Directory.EnumerateFiles(destDir, "*.xml", SearchOption.AllDirectories).FirstOrDefault();

        // 2. File .xml trực tiếp trong thư mục tháng_năm.
        if (xmlPath == null)
        {
            var rawXmlPath = Path.Combine(monthFolder, baseName + ".xml");
            if (File.Exists(rawXmlPath))
                xmlPath = rawXmlPath;
        }

        // 3. Fallback an toàn: tìm theo baseName ở toàn bộ cây thư mục gốc.
        if (xmlPath == null)
        {
            try
            {
                if (Directory.Exists(companyRoot))
                {
                    var pattern = baseName + "*.xml";
                    xmlPath = Directory.GetFiles(companyRoot, pattern, SearchOption.AllDirectories).FirstOrDefault();
                }
            }
            catch
            {
                // best-effort search; ignore I/O errors
            }
        }

        if (xmlPath == null || !File.Exists(xmlPath))
            return null;

        return await File.ReadAllTextAsync(xmlPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryGetInvoiceXmlContentAsync(Guid companyId, Invoice invoice, CancellationToken cancellationToken)
    {
        // 1. Thư mục gốc XML theo công ty: Documents\SmartInvoice\{company}\XML\
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        var companyCode = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? companyId.ToString("N")[..8];
        var companyRoot = InvoiceFileStoragePathHelper.GetCompanyXmlRootPath(companyCode);

        var xml = await TryReadInvoiceXmlAsync(companyRoot, invoice, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(xml))
            return xml;

        // 2. Fallback: thư mục ExportXml cũ (Documents\SmartInvoice\ExportXml\yyyy_MM\)
        var exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartInvoice", "ExportXml");
        return await TryReadInvoiceXmlAsync(exportRoot, invoice, cancellationToken).ConfigureAwait(false);
    }
}
