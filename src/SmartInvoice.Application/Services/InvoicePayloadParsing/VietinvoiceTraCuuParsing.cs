using System.Text.Json;
using SmartInvoice.Core;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

public static class VietinvoiceTraCuuParsing
{
    public static string? GetLookupCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                var fromCttkhac = GetCodeFromCttkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac.Trim();

                var fromTtkhac = GetCodeFromTtkhac(candidate);
                if (!string.IsNullOrWhiteSpace(fromTtkhac)) return fromTtkhac.Trim();

                var direct = GetCodeFromDirectFields(candidate);
                if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
            }
        }
        catch
        {
            // ignored
        }

        return null;
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

    private static string? GetCodeFromCttkhac(JsonElement root)
    {
        if (!root.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var label = item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            if (!IsLookupCodeLabel(label)) continue;

            var value = item.TryGetProperty("dlieu", out var dl) && dl.ValueKind == JsonValueKind.String ? dl.GetString()
                : (item.TryGetProperty("dLieu", out var dL) && dL.ValueKind == JsonValueKind.String ? dL.GetString() : null);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static string? GetCodeFromTtkhac(JsonElement root)
    {
        if (!root.TryGetProperty("ttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            if (item.TryGetProperty("ttchung", out var ttchung) && ttchung.ValueKind == JsonValueKind.Array)
            {
                foreach (var sub in ttchung.EnumerateArray())
                {
                    if (sub.ValueKind != JsonValueKind.Object) continue;
                    var label = sub.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                    if (!IsLookupCodeLabel(label)) continue;
                    var value = sub.TryGetProperty("dlieu", out var dl) && dl.ValueKind == JsonValueKind.String ? dl.GetString()
                        : (sub.TryGetProperty("dLieu", out var dL) && dL.ValueKind == JsonValueKind.String ? dL.GetString() : null);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            // ttkhac có thể cùng cấu trúc với cttkhac
            var topLabel = item.TryGetProperty("ttruong", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (!IsLookupCodeLabel(topLabel)) continue;
            var topValue = item.TryGetProperty("dlieu", out var dl2) && dl2.ValueKind == JsonValueKind.String ? dl2.GetString()
                : (item.TryGetProperty("dLieu", out var dL2) && dL2.ValueKind == JsonValueKind.String ? dL2.GetString() : null);
            if (!string.IsNullOrWhiteSpace(topValue))
                return topValue;
        }
        return null;
    }

    private static string? GetCodeFromDirectFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in root.EnumerateObject())
        {
            if (!IsLookupCodeLabel(prop.Name)) continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        return null;
    }

    private static bool IsLookupCodeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var normalized = StringNormalization.NormalizeForComparison(label);
        return normalized.Contains("matracuu", StringComparison.Ordinal)
               || normalized.Contains("mtracuu", StringComparison.Ordinal)
               || normalized.Contains("tracuuma", StringComparison.Ordinal);
    }
}

