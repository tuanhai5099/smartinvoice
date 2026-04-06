using System.Text.Json;
using SmartInvoice.Core;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

public static class FastInvoiceKeysearchParsing
{
    public static string? GetKeysearchFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var fromCttkhac = GetKeysearchFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();

                var fromTtchung = GetKeysearchFromTtkhacTtchung(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtchung)) return fromTtchung.Trim();

                var direct = GetKeysearchFromDirectFields(candidate);
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

    private static string? GetKeysearchFromCttkhac(JsonElement r)
    {
        if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
            {
                var ttStr = tt.GetString();
                if (!string.IsNullOrWhiteSpace(ttStr))
                {
                    var normalized = StringNormalization.NormalizeForComparison(ttStr);
                    if (normalized.Contains("keysearch", StringComparison.Ordinal))
                    {
                        var dlieu = TryGetDataValue(item);
                        if (!string.IsNullOrWhiteSpace(dlieu)) return dlieu;
                    }
                }
            }

            foreach (var prop in item.EnumerateObject())
            {
                var nameNorm = StringNormalization.NormalizeForComparison(prop.Name);
                if (!(nameNorm.Contains("keysearch", StringComparison.Ordinal) || nameNorm.Contains("reservationcode", StringComparison.Ordinal)))
                    continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var s = TryGetDataValue(prop.Value);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        return null;
    }

    private static string? GetKeysearchFromTtkhacTtchung(JsonElement r)
    {
        if (!r.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array) return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind != JsonValueKind.Object || !outer.TryGetProperty("ttchung", out var ttchung)) continue;
            var v = GetKeysearchFromTtchungElement(ttchung);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    private static string? GetKeysearchFromTtchungElement(JsonElement ttchung)
    {
        if (ttchung.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ttchung.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("ttruong", out var t) || t.ValueKind != JsonValueKind.String) continue;
                var ttStr = t.GetString();
                if (string.IsNullOrWhiteSpace(ttStr)) continue;
                var normalized = StringNormalization.NormalizeForComparison(ttStr);
                if (!normalized.Contains("keysearch", StringComparison.Ordinal)) continue;
                var dl = TryGetDataValue(item);
                if (!string.IsNullOrWhiteSpace(dl)) return dl;
            }
        }
        else if (ttchung.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ttchung.EnumerateObject())
            {
                var nameNorm = StringNormalization.NormalizeForComparison(prop.Name);
                if (!(nameNorm.Contains("keysearch", StringComparison.Ordinal) || nameNorm.Contains("reservationcode", StringComparison.Ordinal)))
                    continue;

                var val = prop.Value;
                if (val.ValueKind == JsonValueKind.String)
                {
                    var s = val.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (val.ValueKind == JsonValueKind.Object)
                {
                    var s = TryGetDataValue(val);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        return null;
    }

    private static string? GetKeysearchFromDirectFields(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in r.EnumerateObject())
        {
            var nameNorm = StringNormalization.NormalizeForComparison(prop.Name);
            if (!(nameNorm.Contains("keysearch", StringComparison.Ordinal) || nameNorm.Contains("reservationcode", StringComparison.Ordinal)))
                continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var s = TryGetDataValue(prop.Value);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
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
