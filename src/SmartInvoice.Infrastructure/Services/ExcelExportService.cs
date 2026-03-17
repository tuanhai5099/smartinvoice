using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Core;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Services;

/// <summary>
/// Xuất Excel theo key/template: sheet Tổng hợp (theo VLKCrawlData); nếu không IsSummaryOnly thì thêm sheet Chi tiết.
/// Key đăng ký dùng để chọn template/handler; hiện tại dùng chung logic theo IsSummaryOnly.
/// </summary>
public sealed class ExcelExportService : IExcelExportService
{
    private const int MaxExportRows = 50000;
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly ICompanyAppService _companyService;
    private readonly IHoaDonDienTuApiClient _apiClient;
    private readonly ILogger _logger;

    public ExcelExportService(
        IUnitOfWorkFactory uowFactory,
        ICompanyAppService companyService,
        IHoaDonDienTuApiClient apiClient,
        ILoggerFactory loggerFactory)
    {
        _uowFactory = uowFactory;
        _companyService = companyService;
        _apiClient = apiClient;
        _logger = loggerFactory.CreateLogger(nameof(ExcelExportService));
    }

    public async Task<string> ExportAsync(ExportExcelRequest request, CancellationToken cancellationToken = default)
    {
        var company = await _companyService.GetByIdAsync(request.CompanyId, cancellationToken).ConfigureAwait(false);
        var companyName = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? request.CompanyId.ToString("N")[..8];
        var companyRoot = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyName);
        var rootFolder = Path.Combine(companyRoot, "Excel");
        Directory.CreateDirectory(rootFolder);

        var typeLabel = request.IsSummaryOnly ? "TH" : "CT";
        var directionLabel = request.IsSold ? "BANRA" : "MUAVAO";
        var fileName = $"HD_{typeLabel}_{directionLabel}_{request.FromDate:ddMMyyyy}_{request.ToDate:ddMMyyyy}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        var filePath = Path.Combine(rootFolder, fileName);

        var filter = new InvoiceListFilter(
            request.FromDate,
            request.ToDate.Date.AddDays(1).AddSeconds(-1),
            request.IsSold,
            null,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "NgayLap",
            false);

        IUnitOfWork uow = _uowFactory.Create();
        try
        {
            var (invoices, _) = await uow.Invoices.GetPagedAsync(request.CompanyId, filter, 0, MaxExportRows, cancellationToken).ConfigureAwait(false);
            var list = invoices.OrderBy(i => i.NgayLap ?? i.SyncedAt).ThenBy(i => i.SoHoaDon).ToList();
            if (request.IsSold)
                list = list.OrderBy(i => i.SoHoaDon).ToList();

            var summaryRows = list.Select(inv => (inv, GetPayloadFields(inv))).ToList();
            var tokenValid = await _companyService.EnsureValidTokenAsync(request.CompanyId, cancellationToken).ConfigureAwait(false);
            if (tokenValid)
            {
                var companyEntity = await uow.Companies.GetByIdTrackedAsync(request.CompanyId, cancellationToken).ConfigureAwait(false);
                await EnrichTongHopTenCtAsync(summaryRows, companyEntity?.AccessToken, cancellationToken).ConfigureAwait(false);
            }

            // Hóa đơn ngân hàng (tên người bán hoặc người mua chứa "ngân hàng") đẩy xuống cuối sheet Tổng hợp
            summaryRows = summaryRows.OrderBy(t => IsBankInvoice(t.inv, request.IsSold)).ToList();

            using var workbook = new XLWorkbook();
            WriteTongHopSheet(workbook, summaryRows, request.IsSold);
            if (!request.IsSummaryOnly)
            {
                var listExcludeBank = list.Where(inv => !IsBankInvoice(inv, request.IsSold)).ToList();
                WriteChiTietSheet(workbook, listExcludeBank, request.IsSold);
            }

            workbook.SaveAs(filePath);
            _logger.LogInformation("Exported Excel to {Path}", filePath);
            return filePath;
        }
        finally
        {
            if (uow is IAsyncDisposable ad)
                await ad.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnrichTongHopTenCtAsync(List<(Invoice inv, PayloadFields payload)> summaryRows, string? accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return;

        for (var i = 0; i < summaryRows.Count; i++)
        {
            var (inv, payload) = summaryRows[i];
            if (!string.IsNullOrWhiteSpace(payload.TenCt)) continue;
            if (string.IsNullOrWhiteSpace(inv.NbMst) || string.IsNullOrWhiteSpace(inv.KyHieu)) continue;
            try
            {
                var detailJson = await _apiClient.GetInvoiceDetailJsonAsync(accessToken, inv.NbMst, inv.KyHieu, inv.SoHoaDon, payload.Khmshdon, fromSco: false, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(detailJson)) continue;
                var firstItemName = GetFirstItemNameFromDetailJson(detailJson);
                if (!string.IsNullOrWhiteSpace(firstItemName))
                    summaryRows[i] = (inv, payload with { TenCt = firstItemName });
            }
            catch
            {
                try
                {
                    var detailJson = await _apiClient.GetInvoiceDetailJsonAsync(accessToken, inv.NbMst, inv.KyHieu, inv.SoHoaDon, payload.Khmshdon, fromSco: true, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(detailJson)) continue;
                    var firstItemName = GetFirstItemNameFromDetailJson(detailJson);
                    if (!string.IsNullOrWhiteSpace(firstItemName))
                        summaryRows[i] = (inv, payload with { TenCt = firstItemName });
                }
                catch
                {
                    // ignore
                }
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? GetFirstItemNameFromDetailJson(string detailJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("hdhhdvu", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var first = arr.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined) return null;
            return GetStr(first, "thhdvu") ?? GetStr(first, "THHDVu") ?? GetStr(first, "ten") ?? GetStr(first, "thhddvu") ?? GetStr(first, "name");
        }
        catch
        {
            return null;
        }
    }

    private static void WriteTongHopSheet(XLWorkbook workbook, List<(Invoice inv, PayloadFields payload)> summaryRows, bool isSold)
    {
        var ws = workbook.Worksheets.Add("TongHop");
        ws.Style.Font.FontName = "Times New Roman";
        var headers = new[] { "MauSoHD", "KyHieuHD", "SoHD", "NGHD", "NGCT", "MASOTHUE", "COMPANY", "SOTIEN_NET", "SOTIEN_TAX", "SOTIEN_GRO", "THUESUATCL", "TRANGTHAIHD", "TENCT", "MATRACUU", "LINKTRACUU", "LoiTaiVe" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        var headerRange = ws.Range(1, 1, 1, 16);
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#32a8a4");
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontName = "Times New Roman";

        var numberFormat = "#,##0";
        var ngayFormat = CultureInfo.GetCultureInfo("vi-VN");
        for (var i = 0; i < summaryRows.Count; i++)
        {
            var (inv, payload) = summaryRows[i];
            var row = i + 2;
            var ngayLapStr = inv.NgayLap.HasValue ? inv.NgayLap.Value.ToString("dd/MM/yyyy", ngayFormat) : "";
            ws.Cell(row, 1).Value = payload.MauSoHdon;
            ws.Cell(row, 2).Value = inv.KyHieu ?? "";
            ws.Cell(row, 3).Value = inv.SoHoaDon;
            ws.Cell(row, 4).Value = ngayLapStr;
            ws.Cell(row, 5).Value = string.IsNullOrWhiteSpace(payload.Ngct) ? ngayLapStr : payload.Ngct;
            ws.Cell(row, 6).Value = isSold ? (inv.MstMua ?? "") : (inv.NbMst ?? "");
            var companyName = isSold ? (inv.NguoiMua ?? "") : (inv.NguoiBan ?? "");
            var displayCompanyName = ToTitleCaseCompanyName(companyName);
            ws.Cell(row, 7).Value = UnicodeToVniConverter.ToVniString(displayCompanyName);
            ws.Cell(row, 7).Style.Font.FontName = "VNI-Times";
            ws.Cell(row, 8).Value = (double)(inv.Tgtcthue ?? 0);
            ws.Cell(row, 8).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 9).Value = (double)(inv.Tgtthue ?? 0);
            ws.Cell(row, 9).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 10).Value = (double)(inv.TongTien ?? 0);
            ws.Cell(row, 10).Style.NumberFormat.Format = numberFormat;
            var rateNumeric = payload.ThueSuatNumeric;
            if (!rateNumeric.HasValue && !string.IsNullOrWhiteSpace(payload.ThueSuatCl) && payload.ThueSuatCl != "0")
            {
                var raw = payload.ThueSuatCl.Replace("%", "").Trim();
                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedInv))
                    rateNumeric = NormalizeThueSuatDecimal(parsedInv);
                else if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.GetCultureInfo("vi-VN"), out var parsedVi))
                    rateNumeric = NormalizeThueSuatDecimal(parsedVi);
            }

            var thueSuatCell = ws.Cell(row, 11);
            thueSuatCell.Style.NumberFormat.Format = "0.00%";
            if (rateNumeric.HasValue)
                thueSuatCell.Value = (double)rateNumeric.Value; // ví dụ 0.08 → hiển thị 8.00%
            else
                thueSuatCell.Clear(); // không ghi chuỗi "0.08%" nữa, chỉ để trống nếu không parse được
            ws.Cell(row, 12).Value = TthaiToDisplay(inv.Tthai);
            ws.Cell(row, 13).Value = payload.TenCt;
            ws.Cell(row, 14).Value = payload.MaTraCuu;
            var linkTraCuu = payload.LinkTraCuu;
            if (!string.IsNullOrWhiteSpace(linkTraCuu))
            {
                var escaped = linkTraCuu.Replace("\"", "\"\"");
                ws.Cell(row, 15).FormulaA1 = $"HYPERLINK(\"{escaped}\",\"{escaped}\")";
            }
            else
                ws.Cell(row, 15).Value = "";
            ws.Cell(row, 16).Value = payload.LoiTaiVe;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteChiTietSheet(XLWorkbook workbook, List<Invoice> list, bool isSold)
    {
        var detailRows = new List<DetailRow>();
        foreach (var inv in list)
        {
            var ntao = inv.NgayLap.HasValue ? inv.NgayLap.Value.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("vi-VN")) : "";
            if (string.IsNullOrEmpty(inv.LineItemsJson))
            {
                detailRows.Add(new DetailRow(inv, null, ntao, isSold));
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(inv.LineItemsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    detailRows.Add(new DetailRow(inv, null, ntao, isSold));
                    continue;
                }
                var added = false;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    detailRows.Add(new DetailRow(inv, item, ntao, isSold));
                    added = true;
                }
                if (!added)
                    detailRows.Add(new DetailRow(inv, null, ntao, isSold));
            }
            catch
            {
                detailRows.Add(new DetailRow(inv, null, ntao, isSold));
            }
        }

        if (isSold)
            detailRows = detailRows.OrderBy(r => r.Inv.SoHoaDon).ToList();
        else
            detailRows = detailRows.OrderBy(r => r.Ntao).ThenBy(r => r.Inv.SoHoaDon).ToList();

        var ws = workbook.Worksheets.Add("ChiTiet");
        ws.Style.Font.FontName = "Times New Roman";
        var headers = new[] { "MauSoHD", "KyHieuHD", "SoHD", "NGHD", "NGCT", "MASOTHUE", "COMPANY", "TENCT", "DVT", "SOLUONG", "DONGIA", "THUESUAT", "THANHTIEN", "TT_NET", "DG_TAX", "TT_TAX", "DG_GRO", "CHIETKHAU", "TT_GRO", "TRANGTHAIHD" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        var headerRange = ws.Range(1, 1, 1, 20);
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#32a8a4");
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontName = "Times New Roman";

        var numberFormat = "#,##0";
        var numberFormatDecimal = "#,##0.00";
        for (var i = 0; i < detailRows.Count; i++)
        {
            var r = detailRows[i];
            var row = i + 2;
            ws.Cell(row, 1).Value = ""; // MauSoHD
            ws.Cell(row, 2).Value = r.Inv.KyHieu ?? "";
            ws.Cell(row, 3).Value = r.Inv.SoHoaDon;
            ws.Cell(row, 4).Value = r.Ntao;
            ws.Cell(row, 5).Value = "";
            ws.Cell(row, 6).Value = r.Mst;
            var detailCompanyName = ToTitleCaseCompanyName(r.CompanyName);
            ws.Cell(row, 7).Value = UnicodeToVniConverter.ToVniString(detailCompanyName);
            ws.Cell(row, 7).Style.Font.FontName = "VNI-Times";
            ws.Cell(row, 8).Value = UnicodeToVniConverter.ToVniString(r.TenHang);
            ws.Cell(row, 8).Style.Font.FontName = "VNI-Times";
            ws.Cell(row, 9).Value = r.Dvtinh;
            ws.Cell(row, 10).Value = r.Sluong;
            ws.Cell(row, 10).Style.NumberFormat.Format = numberFormatDecimal;
            ws.Cell(row, 11).Value = r.Dgia;
            ws.Cell(row, 11).Style.NumberFormat.Format = numberFormat;
            var tsPercent = r.TsuatPercent / 100m;
            ws.Cell(row, 12).Value = (double)tsPercent;
            ws.Cell(row, 12).Style.NumberFormat.Format = "0.00%";
            ws.Cell(row, 13).Value = r.Thtien;
            ws.Cell(row, 13).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 14).Value = r.Thtien;
            ws.Cell(row, 14).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 15).Value = r.DgiaTax;
            ws.Cell(row, 15).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 16).Value = r.Tthue;
            ws.Cell(row, 16).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 17).Value = r.DgiaGro;
            ws.Cell(row, 17).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 18).Value = r.Stckhau;
            ws.Cell(row, 18).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 19).Value = r.TtGro;
            ws.Cell(row, 19).Style.NumberFormat.Format = numberFormat;
            ws.Cell(row, 20).Value = TthaiToDisplay(r.Inv.Tthai);
        }
        ws.Columns().AdjustToContents();
    }

    /// <summary>In hoa ký tự đầu mỗi từ, giữ nguyên TNHH, TM, DV, SX (viết tắt).</summary>
    private static string ToTitleCaseCompanyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var words = name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TNHH", "TM", "DV", "SX" };
        for (var i = 0; i < words.Length; i++)
        {
            if (exclude.Contains(words[i])) continue;
            if (words[i].Length > 0)
                words[i] = char.ToUpper(words[i][0], CultureInfo.GetCultureInfo("vi-VN")) + words[i][1..].ToLower(CultureInfo.GetCultureInfo("vi-VN"));
        }
        return string.Join(" ", words);
    }

    /// <summary>Hóa đơn thuộc ngân hàng: tên người bán hoặc người mua chứa "ngân hàng".</summary>
    private static bool IsBankInvoice(Invoice inv, bool _)
    {
        static bool ContainsBank(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return s.Contains("ngân hàng", StringComparison.OrdinalIgnoreCase)
                || s.Contains("ngan hang", StringComparison.OrdinalIgnoreCase);
        }
        return ContainsBank(inv.NguoiBan) || ContainsBank(inv.NguoiMua);
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

    /// <summary>Trích các trường từ PayloadJson để export giống VLK (MauSoHdon, NGCT, THUESUATCL, TENCT, MATRACUU, LINKTRACUU, LoiTaiVe).</summary>
    private static PayloadFields GetPayloadFields(Invoice inv)
    {
        var mauSoHdon = "";
        var ngct = "";
        var thueSuatCl = "0";
        decimal? thueSuatNumeric = null;
        var tenCt = "";
        var maTraCuu = "";
        var linkTraCuu = "";
        var loiTaiVe = "";
        var khmshdon = (ushort)0;
        if (string.IsNullOrWhiteSpace(inv.PayloadJson))
            return new PayloadFields { MauSoHdon = mauSoHdon, Ngct = ngct, ThueSuatCl = thueSuatCl, ThueSuatNumeric = thueSuatNumeric, TenCt = tenCt, MaTraCuu = maTraCuu, LinkTraCuu = linkTraCuu, LoiTaiVe = loiTaiVe, Khmshdon = khmshdon };
        try
        {
            using var doc = JsonDocument.Parse(inv.PayloadJson);
            var root = doc.RootElement;
            mauSoHdon = GetStr(root, "khmshdon") ?? GetStr(root, "KhmsHdon") ?? "";
            if (root.TryGetProperty("khmshdon", out var kmEl) && kmEl.ValueKind == JsonValueKind.Number && kmEl.TryGetInt32(out var kmInt))
                khmshdon = (ushort)Math.Clamp(kmInt, 0, 65535);
            ngct = GetNgay(root, "ntnhan") ?? GetNgay(root, "Ntnhan") ?? "";
            maTraCuu = GetStr(root, "matracuu") ?? GetStr(root, "MaTraCuu") ?? "";
            linkTraCuu = GetStr(root, "linktracuu") ?? GetStr(root, "LinkTraCuu") ?? "";
            loiTaiVe = GetStr(root, "errormessage") ?? GetStr(root, "ErrorMessage") ?? "";

            // Bổ sung logic giống VLK crawler / HtInvoice:
            // - Nếu chưa có, lấy DC TC (URL tra cứu) và Mã TC (mã tra cứu) từ cttkhac.
            if (string.IsNullOrWhiteSpace(maTraCuu) || string.IsNullOrWhiteSpace(linkTraCuu))
            {
                var (searchUrl, maTc) = GetSearchUrlAndCodeFromCttkhac(root);
                if (string.IsNullOrWhiteSpace(maTraCuu) && !string.IsNullOrWhiteSpace(maTc))
                    maTraCuu = maTc;
                if (string.IsNullOrWhiteSpace(linkTraCuu) && !string.IsNullOrWhiteSpace(searchUrl))
                    linkTraCuu = searchUrl;
            }

            // - Nếu vẫn chưa có, dùng logic GetMatracuu (matracuu/linktracuu/qrcode/maQr hoặc trong cttkhac) giống file in hóa đơn.
            if (string.IsNullOrWhiteSpace(maTraCuu) || string.IsNullOrWhiteSpace(linkTraCuu))
            {
                var qrOrCode = GetMatracuuFromPayload(root);
                if (!string.IsNullOrWhiteSpace(qrOrCode))
                {
                    if (string.IsNullOrWhiteSpace(linkTraCuu) && qrOrCode.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        linkTraCuu = qrOrCode;
                    if (string.IsNullOrWhiteSpace(maTraCuu) && !qrOrCode.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        maTraCuu = qrOrCode;
                }
            }

            if (root.TryGetProperty("ttoan", out var ttoan) && ttoan.TryGetProperty("thttltsuat", out var thtt) && thtt.ValueKind == JsonValueKind.Array)
            {
                var first = thtt.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Undefined)
                {
                    var rawRate = GetStr(first, "tsuat") ?? GetStr(first, "TSuat") ?? GetStr(first, "ltsuat") ?? GetStr(first, "LTSuat") ?? "0";
                    thueSuatCl = rawRate;
                    var parsed = ParseThueSuatToDecimal(first, rawRate);
                    if (parsed.HasValue)
                        thueSuatNumeric = parsed.Value;
                }
            }
            if (!thueSuatNumeric.HasValue && root.TryGetProperty("ttoan", out var ttoan2) && ttoan2.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseThueSuatToDecimal(ttoan2, GetStr(ttoan2, "ltsuat") ?? GetStr(ttoan2, "LTSuat") ?? GetStr(ttoan2, "tsuat") ?? "");
                if (parsed.HasValue)
                    thueSuatNumeric = parsed.Value;
            }

            if (root.TryGetProperty("hdhhdvu", out var hdhhdvu) && hdhhdvu.ValueKind == JsonValueKind.Array)
            {
                var first = hdhhdvu.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Undefined)
                    tenCt = GetStr(first, "thhdvu") ?? GetStr(first, "THHDVu") ?? "";
            }
            if (string.IsNullOrEmpty(tenCt) && !string.IsNullOrEmpty(inv.LineItemsJson))
            {
                try
                {
                    using var lineDoc = JsonDocument.Parse(inv.LineItemsJson);
                    if (lineDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var first = lineDoc.RootElement.EnumerateArray().FirstOrDefault();
                        if (first.ValueKind != JsonValueKind.Undefined)
                            tenCt = GetStr(first, "thhdvu") ?? GetStr(first, "THHDVu") ?? "";
                    }
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore parse errors */ }
        return new PayloadFields { MauSoHdon = mauSoHdon, Ngct = ngct, ThueSuatCl = thueSuatCl, ThueSuatNumeric = thueSuatNumeric, TenCt = tenCt, MaTraCuu = maTraCuu, LinkTraCuu = linkTraCuu, LoiTaiVe = loiTaiVe, Khmshdon = khmshdon };
    }

    /// <summary>Chuyển thuế suất từ API (8, 8%, 0.08) thành số Excel (0.08). 8% → 0.08, 10% → 0.1.</summary>
    private static decimal? ParseThueSuatToDecimal(JsonElement el, string rawStr)
    {
        // Ưu tiên đọc từ các field số (tsuat/TSuat/ltsuat/LTSuat)
        if (GetDecimal(el, "tsuat") is decimal d)
            return NormalizeThueSuatDecimal(d);
        if (GetDecimal(el, "TSuat") is decimal d2)
            return NormalizeThueSuatDecimal(d2);
        if (GetDecimal(el, "ltsuat") is decimal d3)
            return NormalizeThueSuatDecimal(d3);
        if (GetDecimal(el, "LTSuat") is decimal d4)
            return NormalizeThueSuatDecimal(d4);

        // Fallback: parse từ chuỗi rawStr (8, 8%, 8,0, 0,08, ...)
        if (string.IsNullOrWhiteSpace(rawStr)) return null;
        rawStr = rawStr.Trim().Replace("%", "");
        // 1) Thử theo InvariantCulture (dấu chấm thập phân)
        if (decimal.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedInv))
            return NormalizeThueSuatDecimal(parsedInv);
        // 2) Thử theo vi-VN (dấu phẩy thập phân)
        if (decimal.TryParse(rawStr, NumberStyles.Any, CultureInfo.GetCultureInfo("vi-VN"), out var parsedVi))
            return NormalizeThueSuatDecimal(parsedVi);
        return null;
    }

    private static decimal NormalizeThueSuatDecimal(decimal value)
    {
        if (value > 1) return value / 100m;
        return value;
    }

    private static decimal? GetDecimal(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            // 1) Thử parse theo InvariantCulture
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedInv))
                return parsedInv;
            // 2) Thử parse theo vi-VN (dấu phẩy)
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.GetCultureInfo("vi-VN"), out var parsedVi))
                return parsedVi;
        }
        return null;
    }

    private static string? GetStr(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
    }

    private static string? GetNgay(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Null || p.ValueKind == JsonValueKind.Undefined) return null;
        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return dt.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("vi-VN"));
            return s;
        }
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var unix))
            return DateTimeOffset.FromUnixTimeMilliseconds(unix).DateTime.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("vi-VN"));
        return null;
    }

    /// <summary>Lấy nội dung QR / mã tra cứu từ API (matracuu, MaTraCuu, linktracuu, LinkTraCuu, qrcode, maQr hoặc trong cttkhac) giống logic in hóa đơn.</summary>
    private static string? GetMatracuuFromPayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var s = GetStr(root, "matracuu") ?? GetStr(root, "MaTraCuu")
            ?? GetStr(root, "linktracuu") ?? GetStr(root, "LinkTraCuu")
            ?? GetStr(root, "qrcode") ?? GetStr(root, "maQr");
        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        if (!root.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("ttruong", out var tt)) continue;
            var ttStr = tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            if (string.IsNullOrEmpty(ttStr)) continue;
            if (ttStr.Contains("tra cứu", StringComparison.OrdinalIgnoreCase)
                || ttStr.Contains("Mã tra cứu", StringComparison.OrdinalIgnoreCase)
                || ttStr.Contains("matracuu", StringComparison.OrdinalIgnoreCase)
                || ttStr.Contains("link", StringComparison.OrdinalIgnoreCase))
            {
                s = (GetStr(item, "dlieu") ?? GetStr(item, "dLieu"))?.Trim();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    /// <summary>Lấy DC TC (URL tra cứu) và Mã TC (mã tra cứu) từ cttkhac (giống HtInvoice crawler).</summary>
    private static (string? SearchUrl, string? MaTraCuu) GetSearchUrlAndCodeFromCttkhac(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, null);
        if (!root.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (null, null);

        string? dcTc = null;
        string? maTc = null;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var ttruong = item.TryGetProperty("ttruong", out var tt) ? tt.GetString() : null;
            if (string.IsNullOrWhiteSpace(ttruong)) continue;

            var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

            if (string.Equals(ttruong.Trim(), "DC TC", StringComparison.OrdinalIgnoreCase))
                dcTc = value;
            else if (string.Equals(ttruong.Trim(), "Mã TC", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(ttruong.Trim(), "Ma TC", StringComparison.OrdinalIgnoreCase))
                maTc = value;
        }

        return (dcTc, maTc);
    }

    /// <summary>Lấy tên hàng từ ttkhac (ttruong chứa "Tên hàng", "Tên sản phẩm") giống VLK crawl.</summary>
    private static string? GetProductNameFromLineItemTtkhac(JsonElement item)
    {
        if (!item.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in ttkhac.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("ttruong", out var tt)) continue;
            var ttStr = tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            if (string.IsNullOrEmpty(ttStr)) continue;
            if (ttStr.Contains("Tên hàng", StringComparison.OrdinalIgnoreCase) || ttStr.Contains("Tên sản phẩm", StringComparison.OrdinalIgnoreCase)
                || ttStr.Contains("Tên hàng hóa", StringComparison.OrdinalIgnoreCase) || (ttStr.Contains("Tên", StringComparison.Ordinal) && ttStr.Trim().Length <= 10))
            {
                var dlieu = GetStr(entry, "dlieu") ?? GetStr(entry, "dLieu");
                if (!string.IsNullOrWhiteSpace(dlieu)) return dlieu.Trim();
            }
        }
        return null;
    }

    private readonly record struct PayloadFields
    {
        public string MauSoHdon { get; init; }
        public string Ngct { get; init; }
        public string ThueSuatCl { get; init; }
        /// <summary>Thuế suất dạng số Excel: 8% → 0.08, 10% → 0.1.</summary>
        public decimal? ThueSuatNumeric { get; init; }
        public string TenCt { get; init; }
        public string MaTraCuu { get; init; }
        public string LinkTraCuu { get; init; }
        public string LoiTaiVe { get; init; }
        public ushort Khmshdon { get; init; }
    }

    private readonly struct DetailRow
    {
        public Invoice Inv { get; }
        public string Ntao { get; }
        public string Mst => _isSold ? (Inv.MstMua ?? "") : (Inv.NbMst ?? "");
        public string CompanyName => _isSold ? (Inv.NguoiMua ?? "") : (Inv.NguoiBan ?? "");
        public string TenHang { get; }
        public string Dvtinh { get; }
        public decimal Sluong { get; }
        public decimal Dgia { get; }
        public decimal TsuatPercent { get; }
        public decimal Thtien { get; }
        public decimal DgiaTax { get; }
        public decimal Tthue { get; }
        public decimal DgiaGro { get; }
        public decimal Stckhau { get; }
        public decimal TtGro { get; }
        private readonly bool _isSold;

        public DetailRow(Invoice inv, JsonElement? item, string ntao, bool isSold)
        {
            Inv = inv;
            Ntao = ntao;
            _isSold = isSold;
            if (!item.HasValue)
            {
                TenHang = "";
                Dvtinh = "";
                Sluong = 0;
                Dgia = 0;
                TsuatPercent = 0;
                Thtien = 0;
                DgiaTax = 0;
                Tthue = 0;
                DgiaGro = 0;
                Stckhau = 0;
                TtGro = 0;
                return;
            }
            var el = item.Value;
            TenHang = GetStr(el, "thhdvu") ?? GetStr(el, "THHDVu") ?? GetStr(el, "ten") ?? GetStr(el, "thhddvu") ?? GetStr(el, "name") ?? GetProductNameFromLineItemTtkhac(el) ?? "";
            Dvtinh = GetStr(el, "dvtinh") ?? GetStr(el, "DVTinh") ?? "";
            Sluong = GetDecimal(el, "sluong") ?? GetDecimal(el, "SLuong") ?? 0;
            Dgia = GetDecimal(el, "dgia") ?? GetDecimal(el, "DGia") ?? 0;
            var tsuat = GetStr(el, "tsuat") ?? GetStr(el, "TSuat") ?? "0";
            TsuatPercent = decimal.TryParse(tsuat.Replace("%", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var tp) ? tp : 0;
            Thtien = GetDecimal(el, "thtien") ?? GetDecimal(el, "ThTien") ?? 0;
            Stckhau = GetDecimal(el, "stckhau") ?? GetDecimal(el, "STCKhau") ?? 0;
            var rate = TsuatPercent / 100m;
            DgiaTax = Dgia * rate;
            Tthue = Thtien * rate;
            DgiaGro = Dgia + DgiaTax;
            TtGro = Thtien + Tthue;
        }

        private static string? GetStr(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
        }

        private static decimal? GetDecimal(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return null;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
            if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return null;
        }
    }
}
