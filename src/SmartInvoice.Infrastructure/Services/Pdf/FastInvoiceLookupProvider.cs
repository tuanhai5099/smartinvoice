using System.Text.Json;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>Gợi ý tra cứu cho Fast e-Invoice (0100727825): trang tra cứu + mã bí mật (keysearch) trong cttkhac.</summary>
public sealed class FastInvoiceLookupProvider : IInvoiceLookupProvider
{
    public string ProviderKey => "0100727825";

    private const string LookupUrl = "https://invoice.fast.com.vn/tra-cuu-hoa-don-dien-tu/";

    public InvoiceLookupSuggestion? GetSuggestion(string payloadJson, string? sellerTaxCode)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        var keysearch = GetKeysearchFromPayload(payloadJson);

        return new InvoiceLookupSuggestion(
            ProviderKey,
            "Fast e-Invoice",
            LookupUrl,
            string.IsNullOrWhiteSpace(keysearch) ? null : keysearch.Trim(),
            string.IsNullOrWhiteSpace(sellerTaxCode) ? null : sellerTaxCode.Trim());
    }

    private static string? GetKeysearchFromPayload(string payloadJson)
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
                if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                var ttStr = tt.GetString();
                if (string.IsNullOrWhiteSpace(ttStr)) continue;
                if (!string.Equals(ttStr, "keysearch", StringComparison.OrdinalIgnoreCase)) continue;
                var dlieu = item.TryGetProperty("dlieu", out var dl) ? dl.GetString() : null;
                if (string.IsNullOrWhiteSpace(dlieu) && item.TryGetProperty("dLieu", out var dL))
                    dlieu = dL.GetString();
                return string.IsNullOrWhiteSpace(dlieu) ? null : dlieu.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
