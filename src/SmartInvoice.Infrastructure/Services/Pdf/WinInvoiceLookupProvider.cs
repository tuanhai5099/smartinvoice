using System.Text.Json;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>Gợi ý tra cứu cho WinInvoice (0312303803): trang tracuu.wininvoice.vn + mã tra cứu hóa đơn trong cttkhac.</summary>
public sealed class WinInvoiceLookupProvider : IInvoiceLookupProvider
{
    public string ProviderKey => "0312303803";

    private const string SearchUrl = "https://tracuu.wininvoice.vn/";

    public InvoiceLookupSuggestion? GetSuggestion(string payloadJson, string? sellerTaxCode)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            if (!r.TryGetProperty("cttkhac", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            string? privateCode = null;

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
                    privateCode = trimmedValue;
            }

            if (privateCode == null && string.IsNullOrWhiteSpace(sellerTaxCode))
                return null;

            return new InvoiceLookupSuggestion(
                ProviderKey,
                "WinInvoice",
                SearchUrl,
                privateCode,
                string.IsNullOrWhiteSpace(sellerTaxCode) ? null : sellerTaxCode.Trim());
        }
        catch
        {
            return null;
        }
    }
}

