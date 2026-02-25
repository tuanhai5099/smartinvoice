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
/// Lấy HTML xem hóa đơn: gọi API detail, fill template C26TAA-31 (token), ghi file vào thư mục tạm, trả về đường dẫn file.
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
            return (null, "Công ty không tồn tại.");
        var tokenValid = await _companyService.EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!tokenValid)
            return (null, "Token hết hạn. Vui lòng đăng nhập lại công ty.");
        company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company?.AccessToken == null)
            return (null, "Không có token truy cập.");

        var nbmst = inv.NbMst ?? company.TaxCode ?? company.Username ?? "";
        var khhdon = inv.KyHieu ?? "";
        if (string.IsNullOrEmpty(nbmst) || string.IsNullOrEmpty(khhdon))
            return (null, "Thiếu MST hoặc ký hiệu hóa đơn.");

        try
        {
            var existing = await _uow.Invoices.GetByExternalIdAsync(companyId, inv.Id, cancellationToken).ConfigureAwait(false);
            var masterJson = existing?.PayloadJson;

            // Luôn gọi API detail để lấy đủ chi tiết, nhưng KHÔNG ghi đè PayloadJson (giữ nguyên JSON tổng).
            var detailJson = await _apiClient.GetInvoiceDetailJsonAsync(
                company.AccessToken,
                nbmst,
                khhdon,
                inv.SoHoaDon,
                inv.Khmshdon,
                fromSco: inv.MayTinhTien,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(detailJson))
                return (null, "API không trả về dữ liệu chi tiết.");
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
                return (null, "Không đọc được template hóa đơn.");
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get invoice detail failed for {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            return (null, "Lỗi lấy chi tiết: " + ex.Message);
        }
    }

    public async Task<(string? PrintPath, string? Error)> GetInvoicePrintHtmlPathAsync(Guid companyId, InvoiceDisplayDto inv, CancellationToken cancellationToken = default)
    {
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            return (null, "Công ty không tồn tại.");
        var tokenValid = await _companyService.EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!tokenValid)
            return (null, "Token hết hạn. Vui lòng đăng nhập lại công ty.");
        company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company?.AccessToken == null)
            return (null, "Không có token truy cập.");

        var nbmst = inv.NbMst ?? company.TaxCode ?? company.Username ?? "";
        var khhdon = inv.KyHieu ?? "";
        if (string.IsNullOrEmpty(nbmst) || string.IsNullOrEmpty(khhdon))
            return (null, "Thiếu MST hoặc ký hiệu hóa đơn.");

        try
        {
            var existing = await _uow.Invoices.GetByExternalIdAsync(companyId, inv.Id, cancellationToken).ConfigureAwait(false);
            var masterJson = existing?.PayloadJson;

            // Luôn gọi API detail để lấy đủ chi tiết, nhưng giữ nguyên PayloadJson tổng.
            var detailJson = await _apiClient.GetInvoiceDetailJsonAsync(
                company.AccessToken,
                nbmst,
                khhdon,
                inv.SoHoaDon,
                inv.Khmshdon,
                fromSco: inv.MayTinhTien,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(detailJson))
                return (null, "API không trả về dữ liệu chi tiết.");
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
                return (null, "Không đọc được template in hóa đơn.");
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get invoice print HTML failed for {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            // Lỗi dữ liệu/template: trả về trang HTML lỗi để in thay vì chỉ toast
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
                    // Fallback: trả về lỗi dạng message
                }
            }
            return (null, "Lỗi tạo trang in: " + ex.Message);
        }
    }

    public async Task<(IReadOnlyList<InvoiceRelativeItemDto> Items, string? Error)> GetInvoiceRelatedAsync(Guid companyId, InvoiceDisplayDto inv, CancellationToken cancellationToken = default)
    {
        var empty = Array.Empty<InvoiceRelativeItemDto>();

        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            return (empty, "Công ty không tồn tại.");

        var tokenValid = await _companyService.EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!tokenValid)
            return (empty, "Token hết hạn. Vui lòng đăng nhập lại công ty.");

        company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company?.AccessToken == null)
            return (empty, "Không có token truy cập.");

        var nbmst = inv.NbMst ?? company.TaxCode ?? company.Username ?? "";
        var khhdon = inv.KyHieu ?? "";
        if (string.IsNullOrWhiteSpace(nbmst) || string.IsNullOrWhiteSpace(khhdon))
            return (empty, "Thiếu MST hoặc ký hiệu hóa đơn.");

        try
        {
            var trangThai = inv.TrangThaiDisplay ?? string.Empty;
            var isAdjustment = trangThai.Contains("điều chỉnh", StringComparison.OrdinalIgnoreCase) ||
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
                return (empty, "API không trả về dữ liệu hóa đơn liên quan.");

            string? adjustmentDescription = null;
            if (isAdjustment)
            {
                // Lấy thêm detail của hóa đơn điều chỉnh để dựng nội dung điều chỉnh (từ chi tiết hàng hóa).
                var detailJson = await _apiClient.GetInvoiceDetailJsonAsync(
                    company.AccessToken,
                    nbmst,
                    khhdon,
                    inv.SoHoaDon,
                    inv.Khmshdon,
                    fromSco: inv.MayTinhTien,
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(detailJson))
                {
                    adjustmentDescription = ExtractAdjustmentDescription(detailJson);
                }
            }

            var items = ParseInvoiceRelativeItems(json, isAdjustment, adjustmentDescription);
            if (items.Count == 0)
                return (empty, "Không có hóa đơn liên quan.");

            return (items, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get invoice relative failed for {Khhdon}-{Shdon}", inv.KyHieu, inv.SoHoaDon);
            return (empty, "Lỗi lấy hóa đơn liên quan: " + ex.Message);
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
            // Trường hợp API trả về trực tiếp là một mảng các hóa đơn liên quan.
            arr = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("datas", out var datas) &&
                 datas.ValueKind == JsonValueKind.Array)
        {
            // Một số API dùng field "datas" (giống list hóa đơn bán ra).
            arr = datas;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("data", out var dataArr) &&
                 dataArr.ValueKind == JsonValueKind.Array)
        {
            // Phòng trường hợp API dùng "data" (không có "s").
            arr = dataArr;
        }
        else
        {
            // Không đúng format mong đợi.
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

        // Mô phỏng giao diện cổng GDT:
        // - Dòng đầu: "Hóa đơn đang tra cứu"
        // - Các dòng sau: "Hóa đơn có liên quan"
        // - Ô "Hóa đơn gốc" của dòng đầu: mô tả dựa trên dòng liên quan đầu tiên (nếu có).
        string hoaDonGocForFirst = string.Empty;
        if (raw.Count > 1)
        {
            var baseInv = raw[1];
            var prefix = isAdjustment ? "Điều chỉnh cho hóa đơn có ký hiệu mẫu số" : "Thay thế cho hóa đơn có ký hiệu mẫu số";
            hoaDonGocForFirst =
                $"{prefix} {baseInv.Khmshdon}, ký hiệu hóa đơn {baseInv.Khhdon}, số hóa đơn {baseInv.Shdon}";
        }

        for (var i = 0; i < raw.Count; i++)
        {
            var (khmshdon, khhdon, shdon) = raw[i];
            var index = i + 1;
            var loai = i == 0 ? "Hóa đơn đang tra cứu" : "Hóa đơn có liên quan";
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
                if (ttStr.Contains("Tên hàng", StringComparison.OrdinalIgnoreCase) ||
                    ttStr.Contains("Tên sản phẩm", StringComparison.OrdinalIgnoreCase) ||
                    ttStr.Contains("Tên hàng hóa", StringComparison.OrdinalIgnoreCase) ||
                    (ttStr.Contains("Tên", StringComparison.Ordinal) && ttStr.Trim().Length <= 10))
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
