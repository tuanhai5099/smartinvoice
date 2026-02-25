using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SmartInvoice.Infrastructure.Services;

/// <summary>
/// Thay thế token trong template HTML bằng dữ liệu từ JSON detail API.
/// Token chung: {{MAU_SO}}, {{KY_HIEU}}, {{SO}}, {{THDON}}, {{NGAY}}, {{NGAY_DAY}}, {{NGAY_MONTH}}, {{NGAY_YEAR}}, {{MCCQT}},
/// {{BLOCK_NGUOI_BAN}}, {{BLOCK_NGUOI_MUA}}, {{DS_HANG_HOA}}, {{BANG_TONG_THUE}},
/// {{TONG_CHUA_THUE}}, {{TONG_THUE}}, {{TONG_TIEN}}, {{TONG_CHU}}, {{QRCODE_CONTENT}}, {{SIGN_BOX}}, {{DVTTE}}.
/// </summary>
public static class InvoiceTemplateTokenReplacer
{
    public static string Fill(string templateHtml, string? masterJson, string detailJson)
    {
        JsonDocument? masterDoc = null;
        try
        {
            JsonElement masterRoot = default;
            var hasMaster = !string.IsNullOrWhiteSpace(masterJson);
            if (hasMaster)
            {
                try
                {
                    masterDoc = JsonDocument.Parse(masterJson!);
                    masterRoot = masterDoc.RootElement;
                }
                catch
                {
                    hasMaster = false;
                }
            }

            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;
            // API có thể trả về: mảng [ { ... } ], hoặc { "data": [ { ... } ] }, hoặc object { ... } trực tiếp
            var r = root;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                r = root[0];
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                r = data[0];
            if (r.ValueKind != JsonValueKind.Object)
                return CreateErrorHtml();

            var ndhdonRaw = AsObject(r.TryGetProperty("ndhdon", out var ndh) ? ndh : r);
            var ndhdon = ndhdonRaw.ValueKind == JsonValueKind.Object ? ndhdonRaw : r;
            var nbanRaw = ndhdon.TryGetProperty("nban", out var nbanEl) ? AsObject(nbanEl) : ndhdon;
            var nban = nbanRaw.ValueKind == JsonValueKind.Object ? nbanRaw : ndhdon;
            var nmuaRaw = ndhdon.TryGetProperty("nmua", out var nmuaEl) ? AsObject(nmuaEl) : ndhdon;
            var nmua = nmuaRaw.ValueKind == JsonValueKind.Object ? nmuaRaw : ndhdon;
            var itemsRoot = r.TryGetProperty("hdhhdvu", out _) ? r : ndhdon;

            JsonElement ttoanEl = default;
            var hasTtoan = r.TryGetProperty("ttoan", out var ttoanRaw) || ndhdon.TryGetProperty("ttoan", out ttoanRaw);
            if (hasTtoan)
            {
                ttoanEl = AsObject(ttoanRaw);
                if (ttoanEl.ValueKind != JsonValueKind.Object) hasTtoan = false;
            }

            // Mẫu số hóa đơn: khmshdon (mẫu số hóa đơn) - ưu tiên từ JSON tổng, sau đó detail.
            string? mauSo = hasMaster ? GetStr(masterRoot, "khmshdon") : null;
            mauSo ??= GetStr(r, "khmshdon") ?? GetStr(ndhdon, "khmshdon");
            mauSo ??= "1";

            var isMau06PhieuXuatKho = mauSo.StartsWith("06", StringComparison.Ordinal);

            var blockNguoiBan = isMau06PhieuXuatKho
                ? BuildBlockNguoiBanPhieuXuatKho(r, nban, ndhdon)
                : BuildBlockNguoiBan(r, nban);
            var blockNguoiMua = isMau06PhieuXuatKho
                ? BuildBlockNguoiMuaPhieuXuatKho(r, nmua, ndhdon)
                : BuildBlockNguoiMua(r, nmua, ndhdon);
            var dsHangHoa = isMau06PhieuXuatKho
                ? BuildDsHangHoaPhieuXuatKho(itemsRoot)
                : BuildDsHangHoa(itemsRoot);
            // Bảng tổng thuế: chỉ áp dụng cho hóa đơn GTGT (mẫu 01).
            var bangTongThue = isMau06PhieuXuatKho
                ? ""
                : BuildBangTongThue(hasTtoan ? ttoanEl : default, hasMaster ? masterRoot : r);
            var (tongChuaThue, tongThue, tongTien, tongChu) = GetTongValues(r, ndhdon, hasTtoan ? ttoanEl : default);

            var so = FormatSo(r.TryGetProperty("shdon", out _) ? r : ndhdon);
            var ngay = FormatNgay(r.TryGetProperty("tdlap", out _) ? r : ndhdon);
            var (ngayDay, ngayMonth, ngayYear) = FormatNgayParts(r.TryGetProperty("tdlap", out _) ? r : ndhdon);

            // QR/mã tra cứu: ưu tiên master (payloadJson), sau đó detail.
            string qr = "";
            if (hasMaster)
                qr = GetMatracuu(masterRoot) ?? "";
            if (string.IsNullOrEmpty(qr))
                qr = GetMatracuu(r) ?? GetMatracuu(ndhdon) ?? "";

            var blockNguoiBanPrint = isMau06PhieuXuatKho
                ? BuildBlockNguoiBanPhieuXuatKho(r, nban, ndhdon)
                : BuildBlockNguoiBanPrint(r, nban);
            var blockNguoiMuaPrint = isMau06PhieuXuatKho
                ? BuildBlockNguoiMuaPhieuXuatKho(r, nmua, ndhdon)
                : BuildBlockNguoiMuaPrint(r, nmua, ndhdon);
            var (signSubject, signDate) = GetSignatureInfo(hasMaster ? masterRoot : default, r, ndhdon);

            // Khung "Signature Valid / Ký bởi / Ký ngày" chỉ hiển thị khi có chữ ký số.
            var signBoxHtml = "";
            if (!string.IsNullOrWhiteSpace(signSubject) || !string.IsNullOrWhiteSpace(signDate))
            {
                signBoxHtml = $"""
                    <div class="sign-box">
                    <span>Signature Valid</span><span class="span-sign-box">Ký bởi&nbsp;</span><span id="cks" class="span-sign-box">{E(signSubject)}</span><span></span><span class="span-sign-box">Ký ngày:&nbsp;</span><span class="span-sign-box">{E(signDate)}</span>
                    </div>
                    """;
            }

            // Mã của cơ quan thuế (MCCQT): theo sample hiện tại mhdon chính là mã của cơ quan thuế.
            string? mccqt = hasMaster ? GetStr(masterRoot, "mhdon") : null;
            mccqt ??= GetStr(r, "mhdon") ?? GetStr(ndhdon, "mhdon") ?? "";

            return templateHtml
                .Replace("{{MAU_SO}}", E(mauSo))
                .Replace("{{KY_HIEU}}", E(GetStr(r, "khhdon") ?? GetStr(ndhdon, "khhdon") ?? ""))
                .Replace("{{SO}}", E(so))
                .Replace("{{THDON}}", E(GetStr(r, "thdon") ?? GetStr(ndhdon, "thdon") ?? "HÓA ĐƠN GIÁ TRỊ GIA TĂNG"))
                .Replace("{{NGAY}}", E(ngay))
                .Replace("{{NGAY_DAY}}", E(ngayDay))
                .Replace("{{NGAY_MONTH}}", E(ngayMonth))
                .Replace("{{NGAY_YEAR}}", E(ngayYear))
                .Replace("{{MCCQT}}", E(mccqt))
                .Replace("{{BLOCK_NGUOI_BAN}}", blockNguoiBan)
                .Replace("{{BLOCK_NGUOI_MUA}}", blockNguoiMua)
                .Replace("{{BLOCK_NGUOI_BAN_PRINT}}", blockNguoiBanPrint)
                .Replace("{{BLOCK_NGUOI_MUA_PRINT}}", blockNguoiMuaPrint)
                .Replace("{{DS_HANG_HOA}}", dsHangHoa)
                .Replace("{{BANG_TONG_THUE}}", bangTongThue)
                .Replace("{{TONG_CHUA_THUE}}", E(tongChuaThue))
                .Replace("{{TONG_THUE}}", E(tongThue))
                .Replace("{{TONG_TIEN}}", E(tongTien))
                .Replace("{{TONG_CHU}}", E(tongChu))
                .Replace("{{QRCODE_CONTENT}}", E(qr))
                .Replace("{{SIGN_BOX}}", signBoxHtml)
                .Replace("{{SIGN_SUBJECT}}", E(signSubject))
                .Replace("{{SIGN_DATE}}", E(signDate))
                .Replace("{{DVTTE}}", E(GetStr(r, "dvtte") ?? GetStr(ndhdon, "dvtte") ?? ""))
                .Replace("{{FOOTER_CONVERTED_DATE}}", E(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", new CultureInfo("vi-VN"))));
        }
        catch (JsonException)
        {
            return CreateErrorHtml();
        }
        catch (InvalidOperationException)
        {
            return CreateErrorHtml();
        }
        catch (Exception)
        {
            return CreateErrorHtml();
        }
        finally
        {
            masterDoc?.Dispose();
        }
    }

    /// <summary>Trả về HTML trang lỗi khi không thể tải/điền chi tiết hóa đơn (dùng cho xem và in).</summary>
    public static string GetErrorHtml()
    {
        return """
            <html><head><meta charset="utf-8"/><title>Lỗi</title></head>
            <body style="font-family:Segoe UI;padding:24px;color:#333">
            <p style="color:#c62828;font-weight:bold">Không thể tải chi tiết hóa đơn.</p>
            <p>Dữ liệu từ cổng có thể chưa có (hóa đơn chưa có XML) hoặc không đúng định dạng. Vui lòng thử lại sau hoặc tải XML trước.</p>
            </body></html>
            """;
    }

    private static string CreateErrorHtml() => GetErrorHtml();

    private static string BuildBlockNguoiBan(JsonElement r, JsonElement nban)
    {
        var sb = new StringBuilder();
        var sellerName = GetStr(r, "nbten") ?? GetStr(nban, "ten") ?? GetStr(nban, "Ten");
        AppendDataLi(sb, "Tên người bán:", sellerName);
        AppendDataLi(sb, "Mã số thuế:", GetStr(r, "nbmst") ?? GetStr(nban, "mst") ?? GetStr(nban, "MST"));
        AppendDataLi(sb, "Mã cửa hàng:", GetStr(r, "nmch") ?? GetStr(nban, "mch") ?? "");
        AppendDataLi(sb, "Tên cửa hàng:", GetStr(r, "ntch") ?? GetStr(nban, "tch") ?? "");
        AppendDataLi(sb, "Địa chỉ:", GetStr(r, "nbdchi") ?? GetStr(nban, "dchi") ?? GetStr(nban, "DChi"));
        AppendDataLi(sb, "Điện thoại:", GetStr(r, "nbsdthoai") ?? GetStr(nban, "sdthoai") ?? GetStr(nban, "SDThoai"));
        AppendDataLi(sb, "Số tài khoản:", GetStr(r, "nbstk") ?? GetStr(nban, "stknhang") ?? GetStr(nban, "STKNHang"));
        return sb.ToString();
    }

    /// <summary>Block người xuất hàng cho Phiếu xuất kho kiêm vận chuyển nội bộ (mẫu 06).</summary>
    private static string BuildBlockNguoiBanPhieuXuatKho(JsonElement r, JsonElement nban, JsonElement ndhdon)
    {
        var sb = new StringBuilder();
        var sellerName = GetStr(r, "nbten") ?? GetStr(nban, "ten") ?? GetStr(nban, "Ten");
        AppendDataLi(sb, "Tên người xuất hàng:", sellerName);
        AppendDataLi(sb, "Mã số thuế:", GetStr(r, "nbmst") ?? GetStr(nban, "mst") ?? GetStr(nban, "MST"));
        AppendDataLi(sb, "Địa chỉ (Địa chỉ kho xuất hàng):", GetStr(r, "nbdchi") ?? GetStr(nban, "dchi") ?? GetStr(nban, "DChi"));
        AppendDataLi(sb, "Lệnh điều chuyển nội bộ:", FindCttkhacValue(ndhdon, "Lệnh điều chuyển nội bộ") ?? "");
        AppendDataLi(sb, "Tên người vận chuyển:", FindCttkhacValue(ndhdon, "Tên người vận chuyển") ?? "");
        AppendDataLi(sb, "Phương tiện vận chuyển:", FindCttkhacValue(ndhdon, "Phương tiện vận chuyển") ?? "");
        return sb.ToString();
    }

    private static string BuildBlockNguoiMua(JsonElement r, JsonElement nmua, JsonElement ndhdon)
    {
        var sb = new StringBuilder();
        var buyerName = GetStr(r, "nmten") ?? GetStr(r, "nmtnmua") ?? GetStr(nmua, "ten") ?? GetStr(nmua, "Ten");
        AppendDataLi(sb, "Tên người mua:", buyerName);
        AppendDataLi(sb, "Họ tên người mua hàng:", GetStr(r, "nmhten") ?? GetStr(nmua, "hten") ?? "");
        AppendDataLi(sb, "Mã số thuế:", GetStr(r, "nmmst") ?? GetStr(nmua, "mst") ?? GetStr(nmua, "MST"));
        AppendDataLi(sb, "Mã ĐVCQHVNSNN:", GetStr(r, "nmadvcq") ?? GetStr(nmua, "madvcq") ?? "");
        AppendDataLi(sb, "CCCD người mua:", GetStr(r, "nmcccd") ?? GetStr(nmua, "cccd") ?? "");
        AppendDataLi(sb, "Số hộ chiếu:", GetStr(r, "nmhchieu") ?? GetStr(nmua, "hchieu") ?? "");
        AppendDataLi(sb, "Địa chỉ:", GetStr(r, "nmdchi") ?? GetStr(nmua, "dchi") ?? GetStr(nmua, "DChi"));
        AppendDataLi(sb, "Hình thức thanh toán:", GetStr(r, "thtttoan") ?? GetStr(r, "httttoan") ?? GetStr(ndhdon, "httttoan"));
        AppendDataLi(sb, "Số tài khoản:", GetStr(r, "nmstk") ?? GetStr(nmua, "stk") ?? "");

        // Đơn vị tiền tệ & tỷ giá, số/ ngày bảng kê – hiển thị trên cùng một hàng cho mỗi cặp (giống mẫu Tổng cục Thuế).
        var dvtte = GetStr(r, "dvtte") ?? GetStr(ndhdon, "dvtte") ?? "";

        // Tỷ giá: luôn cố gắng parse và format theo "#,0.###" (ví dụ 26282.0 -> "26.282").
        string? tyGiaStr = null;
        var tyGiaRaw = GetStr(r, "tgia") ?? GetStr(ndhdon, "tgia");
        decimal? tyGiaVal = null;
        if (!string.IsNullOrWhiteSpace(tyGiaRaw) &&
            decimal.TryParse(tyGiaRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRaw))
        {
            tyGiaVal = parsedRaw;
        }
        else
        {
            tyGiaVal = GetDecimal(r, "tgia") ?? GetDecimal(ndhdon, "tgia");
        }
        if (tyGiaVal.HasValue)
        {
            tyGiaStr = tyGiaVal.Value.ToString("#,0.###", new CultureInfo("vi-VN"));
        }

        var soBangKe = GetStr(r, "sobke") ?? GetStr(ndhdon, "sobke") ?? GetStr(ndhdon, "sbke");
        var ngayBangKe = GetStr(r, "ngaybke") ?? GetStr(r, "ngaybk") ?? GetStr(ndhdon, "ngaybke") ?? GetStr(ndhdon, "ngaybk");

        // Hàng: Đơn vị tiền tệ | Tỷ giá
        sb.Append("<li class=\"flex-li\">");
        sb.Append("<div class=\"data-item\" style=\"width: 50%\"><div class=\"di-label\"><span>Đơn vị tiền tệ:</span></div><div class=\"di-value\"><div>");
        sb.Append(E(dvtte ?? ""));
        sb.Append("</div></div></div>");
        sb.Append("<div class=\"data-item\" style=\"width: 50%\"><div class=\"di-label\"><span>Tỷ giá:</span></div><div class=\"di-value\"><div>");
        sb.Append(E(tyGiaStr ?? ""));
        sb.Append("</div></div></div>");
        sb.Append("</li>");

        // Hàng: Số bảng kê | Ngày bảng kê
        sb.Append("<li class=\"flex-li\">");
        sb.Append("<div class=\"data-item\" style=\"width: 50%\"><div class=\"di-label\"><span>Số bảng kê:</span></div><div class=\"di-value\"><div>");
        sb.Append(E(soBangKe ?? ""));
        sb.Append("</div></div></div>");
        sb.Append("<div class=\"data-item\" style=\"width: 50%\"><div class=\"di-label\"><span>Ngày bảng kê:</span></div><div class=\"di-value\"><div>");
        sb.Append(E(ngayBangKe ?? ""));
        sb.Append("</div></div></div>");
        sb.Append("</li>");

        return sb.ToString();
    }

    /// <summary>Block người nhận hàng cho Phiếu xuất kho kiêm vận chuyển nội bộ (mẫu 06).</summary>
    private static string BuildBlockNguoiMuaPhieuXuatKho(JsonElement r, JsonElement nmua, JsonElement ndhdon)
    {
        var sb = new StringBuilder();
        var buyerName = GetStr(r, "nmten") ?? GetStr(r, "nmtnmua") ?? GetStr(nmua, "ten") ?? GetStr(nmua, "Ten");
        AppendDataLi(sb, "Tên người nhận hàng:", buyerName);
        AppendDataLi(sb, "Mã số thuế:", GetStr(r, "nmmst") ?? GetStr(nmua, "mst") ?? GetStr(nmua, "MST"));
        AppendDataLi(sb, "Địa chỉ kho nhận hàng:", GetStr(r, "nmdchi") ?? GetStr(nmua, "dchi") ?? GetStr(nmua, "DChi"));
        return sb.ToString();
    }

    /// <summary>Block người bán cho template in: thêm Mã cửa hàng, Tên cửa hàng.</summary>
    private static string BuildBlockNguoiBanPrint(JsonElement r, JsonElement nban)
    {
        var sb = new StringBuilder();
        var sellerName = GetStr(r, "nbten") ?? GetStr(nban, "ten") ?? GetStr(nban, "Ten");
        AppendDataLi(sb, "Tên người bán:", sellerName);
        AppendDataLi(sb, "Mã số thuế:", GetStr(r, "nbmst") ?? GetStr(nban, "mst") ?? GetStr(nban, "MST"));
        AppendDataLi(sb, "Mã cửa hàng:", GetStr(r, "nmch") ?? GetStr(nban, "mch") ?? "");
        AppendDataLi(sb, "Tên cửa hàng:", GetStr(r, "ntch") ?? GetStr(nban, "tch") ?? "");
        AppendDataLi(sb, "Địa chỉ:", GetStr(r, "nbdchi") ?? GetStr(nban, "dchi") ?? GetStr(nban, "DChi"));
        AppendDataLi(sb, "Điện thoại:", GetStr(r, "nbsdthoai") ?? GetStr(nban, "sdthoai") ?? GetStr(nban, "SDThoai"));
        AppendDataLi(sb, "Số tài khoản:", GetStr(r, "nbstk") ?? GetStr(nban, "stknhang") ?? GetStr(nban, "STKNHang"));
        return sb.ToString();
    }

    /// <summary>Block người mua cho template in: đủ thứ tự cổng GDT (Họ tên người mua hàng, Mã ĐVCQHVNSNN, CCCD, Hộ chiếu, Số TK).</summary>
    private static string BuildBlockNguoiMuaPrint(JsonElement r, JsonElement nmua, JsonElement ndhdon)
    {
        var sb = new StringBuilder();
        var buyerName = GetStr(r, "nmten") ?? GetStr(r, "nmtnmua") ?? GetStr(nmua, "ten") ?? GetStr(nmua, "Ten");
        AppendDataLi(sb, "Tên người mua:", buyerName);
        AppendDataLi(sb, "Họ tên người mua hàng:", GetStr(r, "nmhten") ?? GetStr(nmua, "hten") ?? "");
        AppendDataLi(sb, "Mã số thuế:", GetStr(r, "nmmst") ?? GetStr(nmua, "mst") ?? GetStr(nmua, "MST"));
        AppendDataLi(sb, "Mã ĐVCQHVNSNN:", GetStr(r, "nmadvcq") ?? GetStr(nmua, "madvcq") ?? "");
        AppendDataLi(sb, "CCCD người mua:", GetStr(r, "nmcccd") ?? GetStr(nmua, "cccd") ?? "");
        AppendDataLi(sb, "Số hộ chiếu:", GetStr(r, "nmhchieu") ?? GetStr(nmua, "hchieu") ?? "");
        AppendDataLi(sb, "Địa chỉ:", GetStr(r, "nmdchi") ?? GetStr(nmua, "dchi") ?? GetStr(nmua, "DChi"));
        AppendDataLi(sb, "Hình thức thanh toán:", GetStr(r, "thtttoan") ?? GetStr(r, "httttoan") ?? GetStr(ndhdon, "httttoan"));
        AppendDataLi(sb, "Số tài khoản:", GetStr(r, "nmstk") ?? GetStr(nmua, "stk") ?? "");

        var dvtte = GetStr(r, "dvtte") ?? GetStr(ndhdon, "dvtte") ?? "";

        // Tỷ giá (template in): format giống xem hóa đơn.
        string? tyGiaStr = null;
        var tyGiaRaw = GetStr(r, "tgia") ?? GetStr(ndhdon, "tgia");
        decimal? tyGiaVal = null;
        if (!string.IsNullOrWhiteSpace(tyGiaRaw) &&
            decimal.TryParse(tyGiaRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedRaw))
        {
            tyGiaVal = parsedRaw;
        }
        else
        {
            tyGiaVal = GetDecimal(r, "tgia") ?? GetDecimal(ndhdon, "tgia");
        }
        if (tyGiaVal.HasValue)
        {
            tyGiaStr = tyGiaVal.Value.ToString("#,0.###", new CultureInfo("vi-VN"));
        }

        var soBangKe = GetStr(r, "sobke") ?? GetStr(ndhdon, "sobke") ?? GetStr(ndhdon, "sbke");
        var ngayBangKe = GetStr(r, "ngaybke") ?? GetStr(r, "ngaybk") ?? GetStr(ndhdon, "ngaybke") ?? GetStr(ndhdon, "ngaybk");

        // Hàng: Đơn vị tiền tệ | Tỷ giá (template in)
        sb.Append("<li class=\"flex-li\">");
        sb.Append("<div class=\"data-item\" style=\"width: 50%\"><div class=\"di-label\"><span>Đơn vị tiền tệ:</span></div><div class=\"di-value\"><div>");
        sb.Append(E(dvtte ?? ""));
        sb.Append("</div></div></div>");
        sb.Append("<div class=\"data-item\" style=\"width: 50%\"><div class=\"di-label\"><span>Tỷ giá:</span></div><div class=\"di-value\"><div>");
        sb.Append(E(tyGiaStr ?? ""));
        sb.Append("</div></div></div>");
        sb.Append("</li>");

        // Hàng: Số bảng kê | Ngày bảng kê (template in)
        sb.Append("<li class=\"flex-li\">");
        sb.Append("<div class=\"data-item\" style=\"width: 50%\"><div class=\"di-label\"><span>Số bảng kê:</span></div><div class=\"di-value\"><div>");
        sb.Append(E(soBangKe ?? ""));
        sb.Append("</div></div></div>");
        sb.Append("<div class=\"data-item\" style=\"width: 50%\"><div class=\"di-label\"><span>Ngày bảng kê:</span></div><div class=\"di-value\"><div>");
        sb.Append(E(ngayBangKe ?? ""));
        sb.Append("</div></div></div>");
        sb.Append("</li>");

        return sb.ToString();
    }

    private static void AppendDataLi(StringBuilder sb, string label, string? value)
    {
        sb.Append("<li><div class=\"data-item\"><div class=\"di-label\"><span>");
        sb.Append(E(label));
        sb.Append("</span></div><div class=\"di-value\"><div>");
        sb.Append(E(value ?? ""));
        sb.Append("</div></div></div></li>");
    }

    private static string BuildDsHangHoa(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "";
        if (!root.TryGetProperty("hdhhdvu", out var arr) || arr.ValueKind != JsonValueKind.Array)
            if (!root.TryGetProperty("dshhdvu", out arr) || arr.ValueKind != JsonValueKind.Array)
                return "";
        var sb = new StringBuilder();
        var idx = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            idx++;
            var stt = item.TryGetProperty("stt", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sttVal) ? sttVal : idx;
            var tchat = GetTchatDisplayForLineItem(item);
            var thhdvu = GetProductNameFromLineItem(item);
            var dvtinh = GetStr(item, "dvtinh") ?? GetStr(item, "DVTinh") ?? "";
            var sluong = FormatNumber((GetDecimal(item, "sluong") ?? GetDecimal(item, "SLuong")) ?? 0);
            var dgia = FormatNumber((GetDecimal(item, "dgia") ?? GetDecimal(item, "DGia")) ?? 0);
            var ckhau = FormatNumber((GetDecimal(item, "stckhau") ?? GetDecimal(item, "STCKhau")) ?? 0);
            var tsuat = FormatTaxRateForLine(item);
            var thtien = FormatNumber((GetDecimal(item, "thtien") ?? GetDecimal(item, "ThTien")) ?? 0);
            sb.Append("<tr><td class=\"tx-center\">").Append(stt).Append("</td><td class=\"tx-left\"><span>").Append(E(tchat)).Append("</span></td>");
            sb.Append("<td class=\"tx-left\" style=\"max-width: 200px;word-wrap: break-word;\"></td>");
            sb.Append("<td class=\"tx-left\">").Append(E(thhdvu)).Append("</td><td class=\"tx-left\">").Append(E(dvtinh)).Append("</td>");
            sb.Append("<td class=\"tx-center\">").Append(E(sluong)).Append("</td><td class=\"tx-center\">").Append(E(dgia)).Append("</td>");
            sb.Append("<td class=\"tx-center\">").Append(E(ckhau)).Append("</td><td class=\"tx-center\">").Append(E(tsuat)).Append("</td>");
            sb.Append("<td class=\"tx-center\">").Append(E(thtien)).Append("</td></tr>");
        }
        return sb.ToString();
    }

    /// <summary>Dòng hàng hóa cho Phiếu xuất kho (mẫu 06): không hiển thị chiết khấu, thuế suất.</summary>
    private static string BuildDsHangHoaPhieuXuatKho(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "";
        if (!root.TryGetProperty("hdhhdvu", out var arr) || arr.ValueKind != JsonValueKind.Array)
            if (!root.TryGetProperty("dshhdvu", out arr) || arr.ValueKind != JsonValueKind.Array)
                return "";
        var sb = new StringBuilder();
        var idx = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            idx++;
            var stt = item.TryGetProperty("stt", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sttVal) ? sttVal : idx;
            var tchat = GetTchatDisplayForLineItem(item);
            var thhdvu = GetProductNameFromLineItem(item);
            var dvtinh = GetStr(item, "dvtinh") ?? GetStr(item, "DVTinh") ?? "";
            var sluong = FormatNumber((GetDecimal(item, "sluong") ?? GetDecimal(item, "SLuong")) ?? 0);
            var dgia = FormatNumber((GetDecimal(item, "dgia") ?? GetDecimal(item, "DGia")) ?? 0);
            var thtien = FormatNumber((GetDecimal(item, "thtien") ?? GetDecimal(item, "ThTien")) ?? 0);
            sb.Append("<tr>");
            sb.Append("<td class=\"tx-center\">").Append(stt).Append("</td>");
            sb.Append("<td>").Append(E(tchat)).Append("</td>");
            sb.Append("<td>").Append(E(thhdvu)).Append("</td>");
            sb.Append("<td>").Append(E(dvtinh)).Append("</td>");
            sb.Append("<td class=\"tx-center\">").Append(E(sluong)).Append("</td>");
            sb.Append("<td class=\"tx-center\">").Append(E(dgia)).Append("</td>");
            sb.Append("<td class=\"tx-center\">").Append(E(thtien)).Append("</td>");
            sb.Append("</tr>");
        }
        return sb.ToString();
    }

    private static string BuildBangTongThue(JsonElement? ttoan, JsonElement root)
    {
        JsonElement thtt;
        // 1) Ưu tiên ttoan.thttltsuat (cấu trúc chuẩn của nhiều NCC).
        if (ttoan.HasValue && ttoan.Value.ValueKind == JsonValueKind.Object &&
            ttoan.Value.TryGetProperty("thttltsuat", out thtt) && thtt.ValueKind == JsonValueKind.Array && thtt.GetArrayLength() > 0)
        {
            return BuildBangTongThueFromArray(thtt);
        }

        // 2) Fallback: thttltsuat ngay trên root (JSON tổng hoặc detail).
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("thttltsuat", out thtt) && thtt.ValueKind == JsonValueKind.Array && thtt.GetArrayLength() > 0)
        {
            return BuildBangTongThueFromArray(thtt);
        }

        return "";
    }

    private static string BuildBangTongThueFromArray(JsonElement thtt)
    {
        var sb = new StringBuilder();
        foreach (var l in thtt.EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Object) continue;
            var ts = FormatTaxRateForSummary(l);
            var tt = FormatNumber((GetDecimal(l, "thtien") ?? GetDecimal(l, "ThTien")) ?? 0);
            var thue = FormatNumber((GetDecimal(l, "tthue") ?? GetDecimal(l, "TThue")) ?? 0);
            sb.Append("<tr><td class=\"tx-center\">").Append(E(ts)).Append("</td><td class=\"tx-center\">").Append(E(tt)).Append("</td><td class=\"tx-center\">").Append(E(thue)).Append("</td></tr>");
        }
        return sb.ToString();
    }

    private static (string ChuaThue, string Thue, string Tien, string Chu) GetTongValues(JsonElement root, JsonElement ndhdon, JsonElement? ttoan)
    {
        decimal? tgTCThue = null, tgTThue = null, tgTTTBSo = null;
        string? tgTTTBChu = null;
        if (ttoan.HasValue)
        {
            var t = ttoan.Value;
            tgTCThue = GetDecimal(t, "tgtcthue") ?? GetDecimal(t, "TgTCThue");
            tgTThue = GetDecimal(t, "tgtthue") ?? GetDecimal(t, "TgTThue");
            tgTTTBSo = GetDecimal(t, "tgtttbso") ?? GetDecimal(t, "TgTTTBSo");
            tgTTTBChu = GetStr(t, "tgtttbchu") ?? GetStr(t, "TgTTTBChu");
        }
        if (tgTCThue == null && tgTThue == null)
        {
            tgTCThue = GetDecimal(root, "tgtcthue");
            tgTThue = GetDecimal(root, "tgtthue");
            tgTTTBSo = GetDecimal(root, "tgtttbso") ?? GetDecimal(root, "tongtien");
            tgTTTBChu = GetStr(root, "tgtttbchu");
        }
        return (
            FormatNumber(tgTCThue ?? 0),
            FormatNumber(tgTThue ?? 0),
            FormatNumber(tgTTTBSo ?? 0),
            tgTTTBChu ?? ""
        );
    }

    private static (string Subject, string Date) GetSignatureInfo(JsonElement masterRoot, JsonElement root, JsonElement ndhdon)
    {
        // Ưu tiên đọc chữ ký số từ trường nbcks trong JSON tổng (master) hoặc trong detail.
        // nbcks là một chuỗi JSON có dạng:
        // { "Subject": "...", "SigningTime": "2026-01-14T15:11:14", ... }
        var sigInfo = TryParseNbcks(masterRoot);
        if (!string.IsNullOrWhiteSpace(sigInfo.Subject) || !string.IsNullOrWhiteSpace(sigInfo.Date))
            return sigInfo;

        sigInfo = TryParseNbcks(root);
        if (!string.IsNullOrWhiteSpace(sigInfo.Subject) || !string.IsNullOrWhiteSpace(sigInfo.Date))
            return sigInfo;

        sigInfo = TryParseNbcks(ndhdon);
        if (!string.IsNullOrWhiteSpace(sigInfo.Subject) || !string.IsNullOrWhiteSpace(sigInfo.Date))
            return sigInfo;

        // Fallback: cố gắng tìm thông tin chữ ký trong các field khác (cks/chuky/sign/cert, tdky/ngayky/signdate).
        var subjectFallbackRaw = FindFirstStringByKeyHints(root, new[] { "cks", "chuky", "sign", "cert" })
                                 ?? FindFirstStringByKeyHints(ndhdon, new[] { "cks", "chuky", "sign", "cert" })
                                 ?? "";
        var subjectFallback = ExtractCompanyNameFromSubject(subjectFallbackRaw);

        var rawDate = FindFirstStringByKeyHints(root, new[] { "tdky", "ngayky", "signdate" })
                      ?? FindFirstStringByKeyHints(ndhdon, new[] { "tdky", "ngayky", "signdate" })
                      ?? "";
        if (!string.IsNullOrWhiteSpace(rawDate) &&
            DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt2))
        {
            rawDate = dt2.ToString("yyyy-MM-ddTHH:mm:ss");
        }
        return (subjectFallback, rawDate);
    }

    private static (string Subject, string Date) TryParseNbcks(JsonElement container)
    {
        try
        {
            if (container.ValueKind == JsonValueKind.Object &&
                container.TryGetProperty("nbcks", out var nbcksEl) &&
                nbcksEl.ValueKind == JsonValueKind.String)
            {
                var raw = nbcksEl.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    return ("", "");

                using var sigDoc = JsonDocument.Parse(raw);
                var sig = sigDoc.RootElement;
                var subjectRaw = GetStr(sig, "Subject") ?? "";
                var subject = ExtractCompanyNameFromSubject(subjectRaw);
                var signingTime = GetStr(sig, "SigningTime") ?? "";
                if (!string.IsNullOrWhiteSpace(signingTime) &&
                    DateTime.TryParse(signingTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                {
                    signingTime = dt.ToString("yyyy-MM-ddTHH:mm:ss");
                }
                return (subject, signingTime);
            }
        }
        catch
        {
            // ignore và fallback
        }
        return ("", "");
    }

    private static string ExtractCompanyNameFromSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "";

        // Ưu tiên phần "CN=..." trong Subject.
        var idx = subject.IndexOf("CN=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = idx + 3;
            var end = subject.IndexOf(',', start);
            var part = end >= 0 ? subject.Substring(start, end - start) : subject[start..];
            return part.Trim().Trim('"');
        }

        // Fallback: nếu không có CN= thì trả về toàn bộ chuỗi (đã trim).
        return subject.Trim().Trim('"');
    }

    private static string? FindFirstStringByKeyHints(JsonElement element, string[] keySubstrings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var hint in keySubstrings)
                    {
                        if (property.Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            property.Value.ValueKind == JsonValueKind.String)
                        {
                            return property.Value.GetString();
                        }
                    }
                    var nested = FindFirstStringByKeyHints(property.Value, keySubstrings);
                    if (!string.IsNullOrEmpty(nested))
                        return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindFirstStringByKeyHints(item, keySubstrings);
                    if (!string.IsNullOrEmpty(nested))
                        return nested;
                }
                break;
        }
        return null;
    }

    /// <summary>Lấy tên hàng hóa/dịch vụ: thhdvu, THHDVu, ten, thhddvu, name; hoặc từ ttkhac (ttruong chứa "Tên", "tên hàng", "Tên sản phẩm").</summary>
    private static string GetProductNameFromLineItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return "";
        var s = GetStr(item, "thhdvu") ?? GetStr(item, "THHDVu") ?? GetStr(item, "ten") ?? GetStr(item, "thhddvu") ?? GetStr(item, "name");
        if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        if (item.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in ttkhac.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("ttruong", out var tt)) continue;
                var ttStr = tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                if (string.IsNullOrEmpty(ttStr)) continue;
                if (ttStr.Contains("Tên hàng", StringComparison.OrdinalIgnoreCase) || ttStr.Contains("Tên sản phẩm", StringComparison.OrdinalIgnoreCase)
                    || ttStr.Contains("Tên hàng hóa", StringComparison.OrdinalIgnoreCase) || (ttStr.Contains("Tên", StringComparison.Ordinal) && ttStr.Trim().Length <= 10))
                {
                    var dlieu = (GetStr(entry, "dlieu") ?? GetStr(entry, "dLieu"))?.Trim();
                    if (!string.IsNullOrWhiteSpace(dlieu)) return dlieu;
                }
            }
        }
        return "";
    }

    /// <summary>Lấy nội dung cột "tính chất" cho 1 dòng hàng hóa: ưu tiên từ ttkhac (ttruong chứa "tính chất"/"Mã tính chất"), sau đó từ tchat.</summary>
    private static string GetTchatDisplayForLineItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return "";
        // Ưu tiên text từ ttkhac (một số API ngân hàng chỉ gửi ở đây)
        if (item.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in ttkhac.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("ttruong", out var tt)) continue;
                var ttStr = tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                if (string.IsNullOrEmpty(ttStr)) continue;
                if (ttStr.Contains("tính chất", StringComparison.OrdinalIgnoreCase) || ttStr.Contains("tinh chat", StringComparison.OrdinalIgnoreCase) || ttStr.Contains("Mã tính chất", StringComparison.OrdinalIgnoreCase))
                {
                    var dlieu = (GetStr(entry, "dlieu") ?? GetStr(entry, "dLieu"))?.Trim();
                    if (!string.IsNullOrWhiteSpace(dlieu)) return dlieu;
                }
            }
        }
        // Fallback: tchat số (1 = Hàng hóa dịch vụ, 2 = Máy tính tiền). Các giá trị khác mặc định là "Hàng hóa, dịch vụ".
        var tchatVal = GetInt(item, "tchat");
        return GetTchatDisplay(tchatVal);
    }

    private static string GetTchatDisplay(int tchat) =>
        tchat == 2 ? "Máy tính tiền" : "Hàng hóa, dịch vụ";
    private static string FormatSo(JsonElement r)
    {
        if (r.TryGetProperty("shdon", out var sh) && sh.ValueKind == JsonValueKind.Number && sh.TryGetInt32(out var so))
            return so.ToString("D8", CultureInfo.InvariantCulture);
        return GetStr(r, "shdon") ?? "";
    }
    private static string FormatNgay(JsonElement r)
    {
        var d = GetStr(r, "tdlap") ?? GetStr(r, "nlap") ?? GetStr(r, "nky");
        if (string.IsNullOrEmpty(d)) return "";
        if (DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("dd 'tháng' MM 'năm' yyyy", new CultureInfo("vi-VN"));
        return d;
    }

    private static (string Day, string Month, string Year) FormatNgayParts(JsonElement r)
    {
        var d = GetStr(r, "tdlap") ?? GetStr(r, "nlap") ?? GetStr(r, "nky");
        if (string.IsNullOrEmpty(d) || !DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return ("", "", "");
        return (dt.Day.ToString("D2", CultureInfo.InvariantCulture), dt.Month.ToString("D2", CultureInfo.InvariantCulture), dt.Year.ToString(CultureInfo.InvariantCulture));
    }
    private static string FormatNumber(decimal value) => value.ToString("#,0.###", new CultureInfo("vi-VN"));

    /// <summary>Định dạng thuế suất cho dòng hàng: 0.08 → "8%", 0.1 → "10%", 0.05 → "5%". Nếu đã là "8%" thì giữ nguyên.</summary>
    private static string FormatTaxRateForLine(JsonElement item)
    {
        var rate = GetDecimal(item, "tsuat") ?? GetDecimal(item, "TSuat");
        if (rate.HasValue)
        {
            var v = rate.Value;
            if (v > 0 && v <= 1)
                v *= 100;
            return v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }
        // Fallback: dùng chuỗi sẵn có (có thể đã là "8%")
        return GetStr(item, "tsuat") ?? GetStr(item, "TSuat") ?? GetStr(item, "ltsuat") ?? "";
    }

    /// <summary>Định dạng thuế suất cho bảng tổng thuế: giống dòng hàng (0.08 → "8%", ...).</summary>
    private static string FormatTaxRateForSummary(JsonElement row)
    {
        var rate = GetDecimal(row, "tsuat") ?? GetDecimal(row, "TSuat");
        if (rate.HasValue)
        {
            var v = rate.Value;
            if (v > 0 && v <= 1)
                v *= 100;
            return v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }
        return GetStr(row, "tsuat") ?? GetStr(row, "TSuat") ?? "";
    }
    /// <summary>HtmlEncode; null, empty hoặc chuỗi "null" → trả về rỗng (không hiển thị chữ null).</summary>
    private static string E(string? s) => string.IsNullOrEmpty(s) || string.Equals(s, "null", StringComparison.OrdinalIgnoreCase) ? "" : WebUtility.HtmlEncode(s);

    /// <summary>Trả về element dưới dạng object: nếu là mảng khác rỗng thì lấy phần tử đầu.</summary>
    private static JsonElement AsObject(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
            return el[0];
        return el;
    }

    /// <summary>Lấy chuỗi từ JSON; null / "null" → trả về null (để hiển thị rỗng, không chữ null).</summary>
    private static string? GetStr(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Null) return null;
        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            return string.Equals(s, "null", StringComparison.OrdinalIgnoreCase) ? null : s;
        }
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i.ToString(CultureInfo.InvariantCulture);
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        var raw = p.GetRawText();
        return string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase) ? null : raw;
    }
    private static int GetInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0;
    }
    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }
    /// <summary>Lấy nội dung QR / mã tra cứu từ API (matracuu, MaTraCuu, linktracuu, LinkTraCuu, qrcode, maQr hoặc trong cttkhac).</summary>
    private static string? GetMatracuu(JsonElement root)
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
            if (ttStr.Contains("tra cứu", StringComparison.OrdinalIgnoreCase) || ttStr.Contains("Mã tra cứu", StringComparison.OrdinalIgnoreCase) || ttStr.Contains("matracuu", StringComparison.OrdinalIgnoreCase) || ttStr.Contains("link", StringComparison.OrdinalIgnoreCase))
            {
                s = (GetStr(item, "dlieu") ?? GetStr(item, "dLieu"))?.Trim();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    /// <summary>Tìm giá trị trong cttkhac theo nhãn ttruong chứa đoạn text chỉ định.</summary>
    private static string? FindCttkhacValue(JsonElement root, string labelContains)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("ttruong", out var tt)) continue;
            var ttStr = tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            if (string.IsNullOrEmpty(ttStr)) continue;
            if (ttStr.IndexOf(labelContains, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var val = GetStr(item, "dlieu") ?? GetStr(item, "dLieu");
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
        }
        return null;
    }
}
