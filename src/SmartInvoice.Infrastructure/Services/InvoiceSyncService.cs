using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Exceptions;
using SmartInvoice.Application.Services;
using SmartInvoice.Core;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Services;

public class InvoiceSyncService : IInvoiceSyncService
{
    private const int XmlRedownloadAfterDays = 3;

    private readonly IUnitOfWork _uow;
    private readonly IHoaDonDienTuApiClient _apiClient;
    private readonly ICompanyAppService _companyService;
    private readonly ILogger _logger;

    public InvoiceSyncService(IUnitOfWork uow, IHoaDonDienTuApiClient apiClient, ICompanyAppService companyService, ILoggerFactory loggerFactory)
    {
        _uow = uow;
        _apiClient = apiClient;
        _companyService = companyService;
        _logger = loggerFactory.CreateLogger(nameof(InvoiceSyncService));
    }

    public async Task<SyncInvoicesResult> SyncInvoicesAsync(Guid companyId, DateTime fromDate, DateTime toDate, bool includeDetail, bool isSold = true, CancellationToken cancellationToken = default)
    {
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            return new SyncInvoicesResult(false, "Công ty không tồn tại.", 0);
        // Đảm bảo có token hợp lệ: thử token hiện tại, nếu 401 thì refresh (nếu có refresh token)
        var tokenValid = await _companyService.EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!tokenValid)
        {
            var loginResult = await _companyService.LoginAndSyncProfileAsync(companyId).ConfigureAwait(false);
            if (!loginResult.Success)
                return new SyncInvoicesResult(false, "Không lấy được token: " + (loginResult.Message ?? "Lỗi không xác định."), 0);
        }
        company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company?.AccessToken == null)
            return new SyncInvoicesResult(false, "Token trống.", 0);

        var accessToken = company.AccessToken;
        var totalSynced = 0;
        const int delayBetweenRequestsMs = 500; // Sleep giữa các request để tránh API rate limit (tham khảo References/VLKCrawlData).
        try
        {
            var allItems = new List<InvoiceItemApiDto>();
            // API chỉ cho phép mỗi lần gọi 1 tháng; quý/nhiều tháng tách thành nhiều request. Bán ra và mua vào gọi tương ứng sold/purchase + sco.
            var monthIndex = 0;
            foreach (var (monthStart, monthEnd) in GetMonthRanges(fromDate, toDate))
            {
                if (monthIndex > 0)
                    await Task.Delay(delayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
                monthIndex++;

                string? state = null;
                if (isSold)
                {
                    do
                    {
                        var respQuery = await _apiClient.GetInvoicesSoldAsync(accessToken, monthStart, monthEnd, state, 50, cancellationToken).ConfigureAwait(false);
                        await Task.Delay(delayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
                        if (respQuery?.Datas != null)
                        {
                            foreach (var item in respQuery.Datas) allItems.Add(item);
                            state = respQuery.State;
                            if (string.IsNullOrEmpty(state)) break;
                        }
                        else break;
                    } while (!string.IsNullOrEmpty(state));
                    state = null;
                    do
                    {
                        var respSco = await _apiClient.GetInvoicesSoldScoAsync(accessToken, monthStart, monthEnd, state, 50, cancellationToken).ConfigureAwait(false);
                        await Task.Delay(delayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
                        if (respSco?.Datas != null)
                        {
                            foreach (var item in respSco.Datas) allItems.Add(item);
                            state = respSco.State;
                            if (string.IsNullOrEmpty(state)) break;
                        }
                        else break;
                    } while (!string.IsNullOrEmpty(state));
                }
                else
                {
                    do
                    {
                        var respQuery = await _apiClient.GetInvoicesPurchaseAsync(accessToken, monthStart, monthEnd, state, 50, cancellationToken).ConfigureAwait(false);
                        await Task.Delay(delayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
                        if (respQuery?.Datas != null)
                        {
                            foreach (var item in respQuery.Datas) allItems.Add(item);
                            state = respQuery.State;
                            if (string.IsNullOrEmpty(state)) break;
                        }
                        else break;
                    } while (!string.IsNullOrEmpty(state));
                    state = null;
                    do
                    {
                        var respSco = await _apiClient.GetInvoicesPurchaseScoAsync(accessToken, monthStart, monthEnd, state, 50, cancellationToken).ConfigureAwait(false);
                        await Task.Delay(delayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
                        if (respSco?.Datas != null)
                        {
                            foreach (var item in respSco.Datas) allItems.Add(item);
                            state = respSco.State;
                            if (string.IsNullOrEmpty(state)) break;
                        }
                        else break;
                    } while (!string.IsNullOrEmpty(state));
                }
            }

            var syncedAt = DateTime.UtcNow;
            foreach (var item in allItems)
            {
                var externalId = item.Id ?? $"{item.Nbmst}_{item.Khhdon}_{item.Shdon}_{item.Khmshdon}";
                var payloadJson = item.RawJson;
                string? lineItemsJson = null;
                if (includeDetail && item.Nbmst != null && item.Khhdon != null)
                {
                    await Task.Delay(300, cancellationToken).ConfigureAwait(false); // Sleep giữa các request chi tiết.
                    try
                    {
                        var detailJson = await _apiClient.GetInvoiceDetailJsonAsync(accessToken, item.Nbmst, item.Khhdon, item.Shdon, item.Khmshdon, fromSco: false, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(detailJson))
                        {
                            payloadJson = detailJson;
                            lineItemsJson = ExtractLineItemsJson(detailJson);
                        }
                    }
                    catch
                    {
                        try
                        {
                            var detailJson = await _apiClient.GetInvoiceDetailJsonAsync(accessToken, item.Nbmst, item.Khhdon, item.Shdon, item.Khmshdon, fromSco: true, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(detailJson))
                            {
                                payloadJson = detailJson;
                                lineItemsJson = ExtractLineItemsJson(detailJson);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch detail for invoice {ExternalId}", externalId);
                        }
                    }
                }

                var invoice = new Invoice
                {
                    Id = Guid.NewGuid(),
                    CompanyId = companyId,
                    ExternalId = externalId,
                    PayloadJson = payloadJson,
                    LineItemsJson = lineItemsJson,
                    SyncedAt = syncedAt,
                    CreatedAt = syncedAt,
                    UpdatedAt = syncedAt,
                    IsSold = isSold
                };
                FillDenormalizedFromPayload(invoice);
                await _uow.Invoices.UpsertAsync(invoice, cancellationToken).ConfigureAwait(false);
                totalSynced++;
            }

            return new SyncInvoicesResult(true, null, totalSynced);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync invoices failed for company {CompanyId}", companyId);
            return new SyncInvoicesResult(false, ex.Message, totalSynced);
        }
    }

    /// <summary>
    /// Chia khoảng fromDate–toDate thành các đoạn theo từng tháng (API chỉ chấp nhận 1 tháng/lần).
    /// Mỗi cặp (start, end) có start là 00:00:00 và end là 23:59:59 trong cùng tháng.
    /// </summary>
    private static IEnumerable<(DateTime Start, DateTime End)> GetMonthRanges(DateTime fromDate, DateTime toDate)
    {
        var current = new DateTime(fromDate.Year, fromDate.Month, 1, 0, 0, 0, fromDate.Kind);
        var end = toDate.Date;
        while (current <= end)
        {
            var monthStart = current.Year == fromDate.Year && current.Month == fromDate.Month
                ? new DateTime(fromDate.Year, fromDate.Month, fromDate.Day, 0, 0, 0, fromDate.Kind)
                : current;
            var lastDayInMonth = DateTime.DaysInMonth(current.Year, current.Month);
            var monthEnd = current.Year == toDate.Year && current.Month == toDate.Month
                ? new DateTime(toDate.Year, toDate.Month, toDate.Day, 23, 59, 59, toDate.Kind)
                : new DateTime(current.Year, current.Month, lastDayInMonth, 23, 59, 59, current.Kind);
            if (monthStart <= monthEnd)
                yield return (monthStart, monthEnd);
            current = current.AddMonths(1);
        }
    }

    private static string? ExtractLineItemsJson(string detailJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            if (doc.RootElement.TryGetProperty("hdhhdvu", out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.GetRawText();
        }
        catch { }
        return null;
    }

    public async Task<IReadOnlyList<InvoiceDisplayDto>> GetLastSyncedInvoicesAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var list = await _uow.Invoices.GetByCompanyIdAsync(companyId, cancellationToken).ConfigureAwait(false);
        var result = new List<InvoiceDisplayDto>();
        foreach (var inv in list)
        {
            var dto = ParseToDisplayDto(inv);
            if (dto != null)
                result.Add(dto);
        }
        return result;
    }

    public async Task<(IReadOnlyList<InvoiceDisplayDto> Page, int TotalCount, InvoiceSummaryDto Summary)> GetInvoicesPagedAsync(Guid companyId, InvoiceListFilterDto filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var sortBy = MapSortByToEntity(filter.SortBy, filter.IsSold);
        var coreFilter = new InvoiceListFilter(
            filter.FromDate,
            filter.ToDate,
            filter.IsSold,
            filter.Tthai,
            filter.LoaiHoaDon,
            filter.SearchText,
            filter.FilterKyHieu,
            filter.FilterSoHoaDon,
            filter.FilterMstNguoiBan,
            filter.FilterTenNguoiBan,
            filter.FilterMstLoaiTru,
            filter.FilterLoaiTruBenBan,
            sortBy,
            filter.SortDescending
        );
        var skip = (page - 1) * pageSize;
        if (skip < 0) skip = 0;
        var (pageEntities, totalCount) = await _uow.Invoices.GetPagedAsync(companyId, coreFilter, skip, pageSize, cancellationToken).ConfigureAwait(false);
        var summary = await _uow.Invoices.GetSummaryAsync(companyId, coreFilter, cancellationToken).ConfigureAwait(false);
        var pageDtos = new List<InvoiceDisplayDto>();
        foreach (var inv in pageEntities)
        {
            var dto = ParseToDisplayDto(inv);
            if (dto != null)
                pageDtos.Add(dto);
        }
        var summaryDto = new InvoiceSummaryDto(
            summary.TotalCount,
            summary.CountCoMa,
            summary.CountKhongMa,
            summary.CountMayTinhTien,
            summary.TotalChuaThue,
            summary.TotalTienThue,
            summary.TotalThanhTien
        );
        return (pageDtos, totalCount, summaryDto);
    }

    public async Task<IReadOnlyList<InvoiceDisplayDto>> GetInvoicesByIdsAsync(Guid companyId, IReadOnlyList<string> invoiceIds, CancellationToken cancellationToken = default)
    {
        if (invoiceIds == null || invoiceIds.Count == 0)
            return Array.Empty<InvoiceDisplayDto>();
        var entities = await _uow.Invoices.GetByCompanyAndExternalIdsAsync(companyId, invoiceIds, cancellationToken).ConfigureAwait(false);
        var list = new List<InvoiceDisplayDto>();
        foreach (var inv in entities)
        {
            var dto = ParseToDisplayDto(inv);
            if (dto != null)
                list.Add(dto);
        }
        return list;
    }

    /// <summary>Map tên cột UI (SortMemberPath) sang tên cột entity để ORDER BY trong DB. CounterpartyName → NguoiMua khi Bán ra (IsSold), NguoiBan khi Mua vào.</summary>
    private static string? MapSortByToEntity(string? sortBy, bool? isSold)
    {
        if (string.IsNullOrWhiteSpace(sortBy)) return null;
        return sortBy switch
        {
            "CounterpartyName" => isSold == true ? "NguoiMua" : "NguoiBan",
            "TrangThaiDisplay" => "Tthai",
            "NgayLap" or "KyHieu" or "SoHoaDon" or "Tgtcthue" or "Tgtthue" or "TongTien" or "NguoiBan" or "NguoiMua" or "Tthai" => sortBy,
            _ => null
        };
    }

    public async Task<DownloadXmlResult> DownloadInvoicesXmlAsync(Guid companyId, IReadOnlyList<InvoiceDisplayDto> invoices, string folderPath, IProgress<DownloadXmlProgress>? progress, CancellationToken cancellationToken = default, string? zipOutputDirectory = null)
    {
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            return new DownloadXmlResult(false, "Công ty không tồn tại.", 0);
        var tokenValid = await _companyService.EnsureValidTokenAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (!tokenValid)
        {
            var loginResult = await _companyService.LoginAndSyncProfileAsync(companyId).ConfigureAwait(false);
            if (!loginResult.Success)
                return new DownloadXmlResult(false, "Không lấy được token: " + (loginResult.Message ?? "Lỗi không xác định."), 0);
        }
        company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        if (company?.AccessToken == null)
            return new DownloadXmlResult(false, "Token trống.", 0);

        // folderPath = thư mục gốc XML theo công ty (Mã công ty)\XML. Lưu từng hóa đơn vào con tháng_năm (yyyy_MM).
        var total = invoices.Count;
        var downloaded = 0;
        var failedCount = 0;
        var noXmlCount = 0;
        var tempPackageFolder = Path.Combine(folderPath, "_XmlPackage_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(tempPackageFolder);
        const int delayMs = 250;
        for (var i = 0; i < invoices.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0)
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            var inv = invoices[i];
            var nbmst = inv.NbMst?.Trim();
            var khhdon = inv.KyHieu?.Trim();
            var soHoaDonDisplay = inv.SoHoaDonDisplay ?? $"{khhdon}-{inv.SoHoaDon}";
            if (string.IsNullOrEmpty(nbmst) || string.IsNullOrEmpty(khhdon))
            {
                failedCount++;
                var key = $"{SanitizeFileName(khhdon ?? "")}-{inv.SoHoaDon}";
                progress?.Report(new DownloadXmlProgress(i + 1, total, null, new DownloadXmlItemResult(key, soHoaDonDisplay, false, false, "Thiếu MST hoặc ký hiệu")));
                continue;
            }
            // Tên file XML giống tên PDF: {SoHoaDon}_{KyHieu}.xml để người dùng dễ nhận ra.
            var baseName = $"{inv.SoHoaDon}_{SanitizeFileName(khhdon)}";
            var xmlFileName = baseName + ".xml";
            var monthFolder = Path.Combine(folderPath, InvoiceFileStoragePathHelper.GetMonthYearFolderName(inv.NgayLap));
            Directory.CreateDirectory(monthFolder);

            DownloadXmlItemResult? itemResult = null;

            // Nếu đã có XML local và còn mới (< XmlRedownloadAfterDays) thì không gọi API lại, chỉ gom vào ZIP và tăng thống kê.
            try
            {
                var existingXmlPath = Path.Combine(monthFolder, xmlFileName);
                if (existingXmlPath != null && File.Exists(existingXmlPath))
                {
                    var lastWrite = File.GetLastWriteTime(existingXmlPath);
                    if ((DateTime.Now - lastWrite).TotalDays < XmlRedownloadAfterDays)
                    {
                        downloaded++;
                        itemResult = new DownloadXmlItemResult(baseName, soHoaDonDisplay, true, false, "Đã có XML, bỏ qua tải lại.");
                        try
                        {
                            var destXmlPath = Path.Combine(tempPackageFolder, xmlFileName);
                            File.Copy(existingXmlPath, destXmlPath, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to copy existing XML into temp package folder for invoice {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
                        }

                        progress?.Report(new DownloadXmlProgress(i + 1, total, xmlFileName, itemResult));
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed when checking existing XML file for invoice {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            }

            try
            {
                var bytes = await _apiClient.GetInvoiceExportAsync(company.AccessToken, nbmst, khhdon, inv.SoHoaDon, inv.Khmshdon, fromSco: inv.MayTinhTien, cancellationToken).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    var saved = await ExtractAndSaveExportAsync(Path.Combine(monthFolder, xmlFileName), bytes, cancellationToken).ConfigureAwait(false);
                    if (saved)
                    {
                        downloaded++;
                        itemResult = new DownloadXmlItemResult(baseName, soHoaDonDisplay, true, false, null);
                        try
                        {
                            var sourceXml = Path.Combine(monthFolder, xmlFileName);
                            if (File.Exists(sourceXml))
                            {
                                var destXmlPath = Path.Combine(tempPackageFolder, xmlFileName);
                                File.Copy(sourceXml, destXmlPath, overwrite: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to copy XML into temp package folder for invoice {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
                        }
                    }
                    else
                    {
                        failedCount++;
                        itemResult = new DownloadXmlItemResult(baseName, soHoaDonDisplay, false, false, "Lỗi không lưu được file");
                    }
                }
                else
                {
                    noXmlCount++;
                    itemResult = new DownloadXmlItemResult(baseName, soHoaDonDisplay, false, true, "Không có XML");
                }
            }
            catch (InvoiceExportNoXmlException ex)
            {
                noXmlCount++;
                itemResult = new DownloadXmlItemResult(baseName, soHoaDonDisplay, false, true, ex.Message ?? "Không tồn tại hồ sơ gốc của hóa đơn.");
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogWarning(ex, "Failed to download export for invoice {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
                itemResult = new DownloadXmlItemResult(baseName, soHoaDonDisplay, false, false, ex.Message);
            }

            try
            {
                if (itemResult is { Success: true } or { NoXml: true })
                {
                    var status = (short)(itemResult.Success ? 1 : 2);
                    var externalId = inv.Id;
                    var entity = await _uow.Invoices.GetByExternalIdAsync(companyId, externalId, cancellationToken).ConfigureAwait(false);
                    if (entity != null)
                    {
                        entity.XmlBaseName = baseName;
                        entity.XmlStatus = status;
                        await _uow.Invoices.UpdateAsync(entity, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update XmlStatus for invoice {Khhdon}-{Shdon}", khhdon, inv.SoHoaDon);
            }

            progress?.Report(new DownloadXmlProgress(i + 1, total, xmlFileName, itemResult));
        }

        string? zipPath = null;
        try
        {
            var xmlFiles = Directory.Exists(tempPackageFolder)
                ? Directory.GetFiles(tempPackageFolder, "*.xml", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
            if (xmlFiles.Length > 0)
            {
                var zipName = $"HoaDonXml_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                var exportZipFolder = !string.IsNullOrWhiteSpace(zipOutputDirectory)
                    ? zipOutputDirectory.Trim()
                    : Path.Combine(folderPath, "ExportXmlZip");
                Directory.CreateDirectory(exportZipFolder);
                zipPath = Path.Combine(exportZipFolder, zipName);
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tempPackageFolder, zipPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create XML zip package.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempPackageFolder))
                    Directory.Delete(tempPackageFolder, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }

        return new DownloadXmlResult(true, null, downloaded, failedCount, noXmlCount, zipPath);
    }

    /// <summary>
    /// Lưu XML xuống đĩa:
    /// - Nếu bytes là ZIP: tìm entry .xml đầu tiên và lưu vào đúng xmlFilePath.
    /// - Nếu không phải ZIP: ghi toàn bộ bytes thành XML tại xmlFilePath.
    /// </summary>
    private static async Task<bool> ExtractAndSaveExportAsync(string xmlFilePath, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4B) // PK (ZIP)
        {
            try
            {
                var destDir = Path.GetDirectoryName(xmlFilePath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);
                using var stream = new MemoryStream(bytes);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                ZipArchiveEntry? xmlEntry = null;
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        xmlEntry = entry;
                        break;
                    }
                }
                if (xmlEntry == null)
                    return false;

                await using (var entryStream = xmlEntry.Open())
                await using (var fileStream = File.Create(xmlFilePath))
                    await entryStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        var text = Encoding.UTF8.GetString(bytes);
        var dirPath = Path.GetDirectoryName(xmlFilePath);
        if (!string.IsNullOrEmpty(dirPath))
            Directory.CreateDirectory(dirPath);
        await File.WriteAllTextAsync(xmlFilePath, text, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
            name = name.Replace(c, '_');
        return name;
    }

    private static void FillDenormalizedFromPayload(Invoice inv)
    {
        try
        {
            using var doc = JsonDocument.Parse(inv.PayloadJson);
            var r = doc.RootElement;
            inv.NgayLap = TryGetDateTime(r, "tdlap");
            inv.Tthai = r.TryGetProperty("tthai", out var th) && th.ValueKind == JsonValueKind.Number ? (short)th.GetInt32() : (short)0;
            inv.Tgtcthue = TryGetDecimal(r, "tgtcthue");
            inv.Tgtthue = TryGetDecimal(r, "tgtthue");
            if (!inv.Tgtcthue.HasValue || !inv.Tgtthue.HasValue)
            {
                if (r.TryGetProperty("ttoan", out var ttoan) && ttoan.ValueKind == JsonValueKind.Object)
                {
                    if (!inv.Tgtcthue.HasValue) inv.Tgtcthue = TryGetDecimal(ttoan, "tgtcthue") ?? TryGetDecimal(ttoan, "TgTCThue");
                    if (!inv.Tgtthue.HasValue) inv.Tgtthue = TryGetDecimal(ttoan, "tgtthue") ?? TryGetDecimal(ttoan, "TgTThue");
                }
            }
            inv.TongTien = TryGetTongTien(r);
            inv.CoMa = HasCttkhacField(r, "Mã tra cứu hóa đơn");
            inv.MayTinhTien = (r.TryGetProperty("hthdon", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() == 5)
                || (r.TryGetProperty("tchat", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() == 2);
            inv.KyHieu = GetStr(r, "khhdon");
            inv.SoHoaDon = r.TryGetProperty("shdon", out var sh) ? sh.GetInt32() : 0;
            inv.NbMst = GetStr(r, "nbmst");
            inv.NguoiBan = GetStr(r, "nbten");
            inv.NguoiMua = GetStr(r, "nmten") ?? GetStr(r, "nmtnmua");
            inv.MstMua = GetStr(r, "nmmst");
            inv.Dvtte = GetStr(r, "dvtte");
        }
        catch
        {
            // best-effort: entity vẫn lưu được, chỉ thiếu denormalized
        }
    }

    private static InvoiceDisplayDto? ParseToDisplayDto(Invoice inv)
    {
        try
        {
            using var doc = JsonDocument.Parse(inv.PayloadJson);
            var r = doc.RootElement;
            var id = GetStr(r, "id") ?? inv.ExternalId;
            var khhdon = GetStr(r, "khhdon");
            var shdon = r.TryGetProperty("shdon", out var sh) ? sh.GetInt32() : 0;
            var soHoaDonDisplay = $"{khhdon}-{shdon}";
            ushort khmshdon = 0;
            if (r.TryGetProperty("khmshdon", out var km) && km.ValueKind == JsonValueKind.Number)
                khmshdon = (ushort)km.GetInt32();
            var nky = TryGetDateTime(r, "nky");
            var tdlap = TryGetDateTime(r, "tdlap");
            var nbten = GetStr(r, "nbten");
            var nbmst = GetStr(r, "nbmst");
            var nmten = GetStr(r, "nmten") ?? GetStr(r, "nmtnmua");
            var nmmst = GetStr(r, "nmmst");
            var tgtcthue = TryGetDecimal(r, "tgtcthue") ?? inv.Tgtcthue;
            var tgtthue = TryGetDecimal(r, "tgtthue") ?? inv.Tgtthue;
            var tgtttbso = TryGetTongTien(r) ?? inv.TongTien;
            var thtttoan = GetStr(r, "thtttoan");
            short tthai = 0;
            if (r.TryGetProperty("tthai", out var th)) tthai = (short)th.GetInt32();
            short ttxly = 0;
            if (r.TryGetProperty("ttxly", out var tx)) ttxly = (short)tx.GetInt32();
            var trangThaiDisplay = TthaiToDisplay(tthai);
            var coMa = HasCttkhacField(r, "Mã tra cứu hóa đơn");
            // Máy tính tiền: theo sample tonghoptumaytinhtien thì hthdon==5; một số API dùng tchat==2.
            var mayTinhTien = false;
            if (r.TryGetProperty("hthdon", out var hthdonEl) && hthdonEl.ValueKind == JsonValueKind.Number && hthdonEl.GetInt32() == 5)
                mayTinhTien = true;
            else if (r.TryGetProperty("tchat", out var tchatEl) && tchatEl.ValueKind == JsonValueKind.Number && tchatEl.GetInt32() == 2)
                mayTinhTien = true;
            var isBanRa = inv.IsSold;
            var counterpartyName = isBanRa ? nmten : nbten;
            var counterpartyMst = isBanRa ? nmmst : nbmst;
            var mhdon = GetStr(r, "mhdon");
            var dvtte = GetStr(r, "dvtte");

            // Tỷ giá: ưu tiên trường tgia trong JSON tổng (đúng theo dữ liệu cổng),
            // nếu không có thì fallback sang cttkhac["ExchangeRate"].
            decimal? exchangeRate = TryGetDecimal(r, "tgia");

            if (exchangeRate is null &&
                r.TryGetProperty("cttkhac", out var cttkhac) &&
                cttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                    var ttStr = tt.GetString();
                    if (!string.Equals(ttStr, "ExchangeRate", StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.TryGetProperty("dlieu", out var dl) && dl.ValueKind == JsonValueKind.String)
                    {
                        var s = dl.GetString();
                        if (!string.IsNullOrWhiteSpace(s) &&
                            decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                        {
                            exchangeRate = rate;
                        }
                    }
                    break;
                }
            }
            var thlap = r.TryGetProperty("thlap", out var thlapEl) && thlapEl.ValueKind == JsonValueKind.Number ? thlapEl.GetInt32() : (int?)null;
            var hthdon = r.TryGetProperty("hthdon", out var hthdonVal) && hthdonVal.ValueKind == JsonValueKind.Number ? hthdonVal.GetInt32() : (int?)null;
            return new InvoiceDisplayDto(id, khhdon, shdon, soHoaDonDisplay, nky, tdlap,
                nbten, nbmst, nmten, nmmst, tgtcthue, tgtthue, tgtttbso, thtttoan,
                tthai, ttxly, trangThaiDisplay, khmshdon, coMa, mayTinhTien, isBanRa,
                mhdon, dvtte, thlap, hthdon, counterpartyName, counterpartyMst,
                inv.XmlStatus, inv.XmlBaseName, exchangeRate);
        }
        catch
        {
            return null;
        }
    }

    private static decimal? TryGetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    /// <summary>Lấy tổng tiền: thử tgtttbso, tongtien, tongTien (API có thể dùng tên khác).</summary>
    private static decimal? TryGetTongTien(JsonElement r)
    {
        return TryGetDecimal(r, "tgtttbso")
            ?? TryGetDecimal(r, "tongtien")
            ?? TryGetDecimal(r, "tongTien");
    }

    private static bool HasCttkhacField(JsonElement root, string ttruongValue)
    {
        if (!root.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("ttruong", out var tt) && tt.GetString() is { } ttStr
                && string.Equals(ttStr, ttruongValue, StringComparison.OrdinalIgnoreCase))
            {
                if (item.TryGetProperty("dlieu", out var dl))
                {
                    var d = dl.GetString();
                    return !string.IsNullOrWhiteSpace(d);
                }
                return false;
            }
        }
        return false;
    }

    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) ? p.GetString() : null;

    private static DateTime? TryGetDateTime(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var s = p.GetString();
        return string.IsNullOrEmpty(s) ? null : (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null);
    }

    private static string TthaiToDisplay(short tthai) => tthai switch
    {
        1 => "Mới",
        2 => "Thay thế",
        3 => "Điều chỉnh",
        4 => "Đã bị thay thế",
        5 => "Đã bị điều chỉnh",
        6 => "Đã hủy",
        _ => ""
    };
}
