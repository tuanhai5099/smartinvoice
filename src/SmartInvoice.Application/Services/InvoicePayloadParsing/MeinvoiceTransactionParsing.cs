using System.Text.Json;
using SmartInvoice.Core;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

public static class MeinvoiceTransactionParsing
{
    public static string? GetTransactionIdFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var fromCttkhac = GetFromNameValueArray(candidate, "cttkhac");
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac;

                var fromTtkhac = GetFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return fromTtkhac;

                var direct = GetFromDirectFields(candidate);
                if (!string.IsNullOrWhiteSpace(direct)) return direct;
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

    private static string? GetFromNameValueArray(JsonElement root, string arrayName)
    {
        if (!root.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
            var ttStr = tt.GetString();
            if (string.IsNullOrWhiteSpace(ttStr) || !IsTransactionIdFieldName(ttStr)) continue;
            var value = item.TryGetProperty("dlieu", out var dl) ? CoerceToString(dl)
                : (item.TryGetProperty("dLieu", out var dL) ? CoerceToString(dL) : null);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    private static string? GetFromTtkhac(JsonElement root)
    {
        if (!root.TryGetProperty("ttkhac", out var ttkhac) || ttkhac.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var outer in ttkhac.EnumerateArray())
        {
            if (outer.ValueKind != JsonValueKind.Object) continue;

            // Cấu trúc dạng cttkhac-like
            var fromOuter = GetFromNameValueArray(outer, "ttchung");
            if (!string.IsNullOrWhiteSpace(fromOuter))
                return fromOuter;

            // Trường trực tiếp trong outer object
            foreach (var prop in outer.EnumerateObject())
            {
                if (!IsTransactionIdFieldName(prop.Name)) continue;
                var s = CoerceToString(prop.Value);
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }
        return null;
    }

    private static string? GetFromDirectFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in root.EnumerateObject())
        {
            if (!IsTransactionIdFieldName(prop.Name)) continue;
            var s = CoerceToString(prop.Value);
            if (!string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }
        return null;
    }

    private static bool IsTransactionIdFieldName(string name)
    {
        var normalized = StringNormalization.NormalizeForComparison(name);
        return normalized.Equals("transactionid", StringComparison.Ordinal)
               || normalized.Equals("transaction", StringComparison.Ordinal)
               || normalized.Contains("magiaodich", StringComparison.Ordinal)
               || normalized.Contains("magd", StringComparison.Ordinal);
    }

    private static string? CoerceToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}
