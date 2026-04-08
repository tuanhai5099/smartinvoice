using System.Text.Json;
using SmartInvoice.Core;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

public static class ThegioididongTraCuuParsing
{
    public static (string? BuyerPhone, string? InvoiceNumberOrLookupCode) GetLookupInputs(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            string? buyerPhone = null;
            string? invoiceCode = null;
            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                buyerPhone ??= GetBuyerPhoneFromObject(candidate);
                invoiceCode ??= GetInvoiceCodeFromObject(candidate);

                if (!string.IsNullOrWhiteSpace(invoiceCode) && !string.IsNullOrWhiteSpace(buyerPhone))
                    break;
            }

            return (buyerPhone?.Trim(), invoiceCode?.Trim());
        }
        catch
        {
            return (null, null);
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

    private static string? GetBuyerPhoneFromObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // 1) Field trực tiếp thường gặp
        foreach (var key in new[] { "nmsdthoai", "nmdt", "nmphone", "buyerPhone", "phone" })
        {
            if (root.TryGetProperty(key, out var v))
            {
                var s = CoerceToString(v);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }

        // 2) Khối người mua
        if (root.TryGetProperty("nmua", out var nmua) && nmua.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in nmua.EnumerateObject())
            {
                if (!IsPhoneFieldName(p.Name)) continue;
                var s = CoerceToString(p.Value);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }

        // 3) cttkhac / ttkhac
        var fromCttkhac = GetValueFromNameValueArray(root, "cttkhac", IsPhoneFieldName);
        if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac;

        if (root.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
        {
            foreach (var outer in ttkhac.EnumerateArray())
            {
                if (outer.ValueKind != JsonValueKind.Object) continue;
                var fromOuter = GetValueFromNameValueArray(outer, "ttchung", IsPhoneFieldName);
                if (!string.IsNullOrWhiteSpace(fromOuter)) return fromOuter;
            }
        }

        return null;
    }

    private static string? GetInvoiceCodeFromObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // 1) shdon/sohoadon
        foreach (var key in new[] { "shdon", "sohoadon", "soHoaDon", "maTraCuu", "matracuu" })
        {
            if (!root.TryGetProperty(key, out var v)) continue;
            var s = CoerceToString(v);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }

        // 2) cttkhac / ttkhac
        var fromCttkhac = GetValueFromNameValueArray(root, "cttkhac", IsInvoiceLookupFieldName);
        if (!string.IsNullOrWhiteSpace(fromCttkhac)) return fromCttkhac;

        if (root.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
        {
            foreach (var outer in ttkhac.EnumerateArray())
            {
                if (outer.ValueKind != JsonValueKind.Object) continue;
                var fromOuter = GetValueFromNameValueArray(outer, "ttchung", IsInvoiceLookupFieldName);
                if (!string.IsNullOrWhiteSpace(fromOuter)) return fromOuter;
            }
        }

        return null;
    }

    private static string? GetValueFromNameValueArray(JsonElement root, string arrayName, Func<string, bool> isFieldName)
    {
        if (!root.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var label = item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
            if (string.IsNullOrWhiteSpace(label) || !isFieldName(label)) continue;
            var value = item.TryGetProperty("dlieu", out var dl) ? CoerceToString(dl)
                : (item.TryGetProperty("dLieu", out var dL) ? CoerceToString(dL) : null);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static bool IsPhoneFieldName(string name)
    {
        var n = StringNormalization.NormalizeForComparison(name);
        return n.Contains("dienthoai", StringComparison.Ordinal)
               || n.Contains("sodienthoai", StringComparison.Ordinal)
               || n.Contains("sdthoai", StringComparison.Ordinal)
               || n == "phone";
    }

    private static bool IsInvoiceLookupFieldName(string name)
    {
        var n = StringNormalization.NormalizeForComparison(name);
        return n.Contains("matracuu", StringComparison.Ordinal)
               || n.Contains("sohoadon", StringComparison.Ordinal)
               || n.Contains("shdon", StringComparison.Ordinal);
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

