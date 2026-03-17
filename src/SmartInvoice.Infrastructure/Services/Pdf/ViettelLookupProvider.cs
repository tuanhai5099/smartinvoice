using System.Text.Json;
using SmartInvoice.Application.Services;
using SmartInvoice.Core;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>Gợi ý tra cứu cho Viettel (0100109106): portal vinvoice.viettel.vn + mã số bí mật trong cttkhac hoặc ttkhac&gt;ttchung.</summary>
public sealed class ViettelLookupProvider : IInvoiceLookupProvider
{
    public string ProviderKey => "0100109106";

    private const string LookupUrl = "https://vinvoice.viettel.vn";

    public InvoiceLookupSuggestion? GetSuggestion(string payloadJson, string? sellerTaxCode)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        var reservationCode = GetReservationCodeFromPayload(payloadJson);

        return new InvoiceLookupSuggestion(
            ProviderKey,
            "Viettel",
            LookupUrl,
            string.IsNullOrWhiteSpace(reservationCode) ? null : reservationCode.Trim(),
            string.IsNullOrWhiteSpace(sellerTaxCode) ? null : sellerTaxCode.Trim());
    }

    private static string? GetReservationCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var fromCttkhac = GetMaSoBiMatFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();
                var fromTtchung = GetMaSoBiMatFromTtkhacTtchung(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtchung)) return fromTtchung.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Trả về các node có thể chứa cttkhac/ttkhac: r, ndhdon, hdon, data (hoặc data[0]).</summary>
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

    private static string? GetMaSoBiMatFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var tt = item.TryGetProperty("ttruong", out var t) ? t.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(tt) || !IsMaSoBiMatTtruong(tt)) continue;
            var dl = TryGetDataValue(item);
            if (!string.IsNullOrWhiteSpace(dl)) return dl.Trim();
            break;
        }
        return null;
    }

    /// <summary>Đọc mã số bí mật từ ttkhac[].ttchung (một số payload Viettel MST 0100109106 đặt ở đây).</summary>
    private static string? GetMaSoBiMatFromTtkhacTtchung(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind != JsonValueKind.Object || !outer.TryGetProperty("ttchung", out var ttchung)) continue;
            var v = GetMaSoBiMatFromTtchungElement(ttchung);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static string? GetMaSoBiMatFromTtchungElement(JsonElement ttchung)
    {
        if (ttchung.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ttchung.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!IsMaSoBiMatTtruong(item.TryGetProperty("ttruong", out var t) ? t.GetString()?.Trim() : null)) continue;
                var dl = TryGetDataValue(item);
                if (!string.IsNullOrWhiteSpace(dl)) return dl.Trim();
            }
        }
        else if (ttchung.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ttchung.EnumerateObject())
            {
                if (!IsMaSoBiMatFieldName(prop.Name)) continue;
                var val = prop.Value;
                var s = val.ValueKind == JsonValueKind.String ? val.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(s) && val.ValueKind == JsonValueKind.Object)
                    s = TryGetDataValue(val)?.Trim();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private const string MaSoBiMatCanonical = "masobimat";

    private static bool IsMaSoBiMatTtruong(string? ttruong)
    {
        var n = StringNormalization.NormalizeForComparison(ttruong);
        return n == MaSoBiMatCanonical || n.Contains(MaSoBiMatCanonical, StringComparison.Ordinal);
    }

    private static bool IsMaSoBiMatFieldName(string name)
    {
        var n = StringNormalization.NormalizeForComparison(name);
        return n == MaSoBiMatCanonical || n.Contains(MaSoBiMatCanonical, StringComparison.Ordinal)
            || n == "reservationcode" || n.Contains("reservationcode", StringComparison.Ordinal);
    }

    /// <summary>
    /// Lấy giá trị dữ liệu thực sự từ một object Viettel: ưu tiên dlieu/dLieu, sau đó thử các property string khác (giatri, value, ...).
    /// Dùng chung cho cả cttkhac và ttkhac.ttchung để tăng khả năng bắt được "Mã số bí mật".
    /// </summary>
    private static string? TryGetDataValue(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

        // 1) Ưu tiên dlieu / dLieu (theo chuẩn cttkhac)
        if (obj.TryGetProperty("dlieu", out var d) && d.ValueKind == JsonValueKind.String)
        {
            var s = d.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        if (obj.TryGetProperty("dLieu", out var dL) && dL.ValueKind == JsonValueKind.String)
        {
            var s = dL.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }

        // 2) Thử một số key phổ biến khác: giatri, value
        if (obj.TryGetProperty("giatri", out var gt) && gt.ValueKind == JsonValueKind.String)
        {
            var s = gt.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        if (obj.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }

        // 3) Fallback: lấy property string đầu tiên khác ttruong
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, "ttruong", StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }

        return null;
    }
}
