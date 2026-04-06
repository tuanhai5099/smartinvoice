using System.Text.Json;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

public static class HtInvoiceTraCuuParsing
{
    public static (string? SearchUrl, string? MaTraCuu) GetSearchUrlAndCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
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
        catch
        {
            return (null, null);
        }
    }
}
