using System.Text.Json;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

public static class WinInvoiceTraCuuParsing
{
    public static string? TryGetPrivateLookupCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var ttruong = item.TryGetProperty("ttruong", out var tt) ? tt.GetString() : null;
                if (string.IsNullOrWhiteSpace(ttruong)) continue;
                var value = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                    : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (string.Equals(ttruong, "Mã tra cứu hóa đơn", StringComparison.OrdinalIgnoreCase))
                    return value.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
