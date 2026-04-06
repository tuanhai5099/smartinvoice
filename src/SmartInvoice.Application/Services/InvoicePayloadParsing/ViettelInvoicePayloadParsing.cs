using System.Text.Json;
using SmartInvoice.Core;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

/// <summary>Parse nbmst + mã số bí mật (cttkhac/ttkhac) dùng chung cho PDF Viettel và rule tra cứu.</summary>
public static class ViettelInvoicePayloadParsing
{
    public static bool TryParsePayload(string payloadJson, out string? supplierTaxCode, out string? reservationCode)
    {
        supplierTaxCode = null;
        reservationCode = null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                if (string.IsNullOrWhiteSpace(supplierTaxCode) && candidate.TryGetProperty("nbmst", out var nbmstProp))
                {
                    var nbmst = nbmstProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(nbmst))
                        supplierTaxCode = nbmst;
                }
                if (string.IsNullOrWhiteSpace(reservationCode))
                    reservationCode = GetMaSoBiMatFromPayload(candidate);
                if (!string.IsNullOrWhiteSpace(supplierTaxCode) && !string.IsNullOrWhiteSpace(reservationCode))
                    return true;
            }
            return !string.IsNullOrWhiteSpace(supplierTaxCode) && !string.IsNullOrWhiteSpace(reservationCode);
        }
        catch
        {
            return false;
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

    private static string? GetMaSoBiMatFromPayload(JsonElement r)
    {
        var fromCttkhac = GetMaSoBiMatFromCttkhac(r);
        if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();
        return GetMaSoBiMatFromTtkhacTtchung(r);
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

    private static string? TryGetDataValue(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

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
