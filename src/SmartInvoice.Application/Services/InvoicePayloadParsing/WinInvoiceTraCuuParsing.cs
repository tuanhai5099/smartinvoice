using System.Text.Json;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

/// <summary>
/// Đọc mã tra cứu WinInvoice từ JSON hóa đơn (cttkhac) — dùng chung cho PDF fetcher và popup gợi ý tra cứu.
/// </summary>
public static class WinInvoiceTraCuuParsing
{
    public static string? TryGetPrivateLookupCodeFromPayload(string payloadJson)
    {
        ExtractFromCttkhac(payloadJson, out var privateCode, out _);
        return privateCode;
    }

    /// <summary>
    /// Đủ cả &quot;Mã tra cứu hóa đơn&quot; và &quot;Mã công ty&quot; (điều kiện tải PDF).
    /// </summary>
    public static bool TryGetTraCuuCodesFromPayload(
        string payloadJson,
        out string? privateLookupCode,
        out string? companyKey)
    {
        ExtractFromCttkhac(payloadJson, out privateLookupCode, out companyKey);
        return !string.IsNullOrWhiteSpace(privateLookupCode) && !string.IsNullOrWhiteSpace(companyKey);
    }

    /// <summary>Gán cả hai mã từ cttkhac nếu có (có thể chỉ có một trong hai).</summary>
    public static void ExtractFromCttkhac(string payloadJson, out string? privateLookupCode, out string? companyKey)
    {
        privateLookupCode = null;
        companyKey = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var ttruong = item.TryGetProperty("ttruong", out var tt) ? tt.GetString() : null;
                if (string.IsNullOrWhiteSpace(ttruong)) continue;

                var value = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                    : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                if (string.IsNullOrWhiteSpace(value)) continue;

                var trimmedValue = value.Trim();

                if (string.Equals(ttruong, "Mã tra cứu hóa đơn", StringComparison.OrdinalIgnoreCase))
                    privateLookupCode = trimmedValue;
                else if (string.Equals(ttruong, "Mã công ty", StringComparison.OrdinalIgnoreCase))
                    companyKey = trimmedValue;
            }
        }
        catch
        {
            // ignored
        }
    }
}
