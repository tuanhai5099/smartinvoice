using System.Text.Json;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>Gợi ý tra cứu cho E-Invoice (0101300842): DC TC + Mã TC trong cttkhac.</summary>
public sealed class EinvoiceLookupProvider : IInvoiceLookupProvider
{
    public string ProviderKey => "0101300842";

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

            if (dcTc == null && maTc == null && string.IsNullOrWhiteSpace(sellerTaxCode))
                return null;

            var url = string.IsNullOrWhiteSpace(dcTc) ? "https://einvoice.vn/tra-cuu" : dcTc;

            return new InvoiceLookupSuggestion(
                ProviderKey,
                "E-Invoice",
                url,
                maTc,
                string.IsNullOrWhiteSpace(sellerTaxCode) ? null : sellerTaxCode.Trim());
        }
        catch
        {
            return null;
        }
    }
}

