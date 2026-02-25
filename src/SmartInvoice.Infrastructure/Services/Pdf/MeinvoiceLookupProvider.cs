using System.Text.Json;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>Gợi ý tra cứu cho MISA meInvoice (0101243150): link tra cứu cố định + Mã tra cứu = transaction id trong cttkhac.</summary>
public sealed class MeinvoiceLookupProvider : IInvoiceLookupProvider
{
    public string ProviderKey => "0101243150";

    private const string LookupUrl = "https://www.meinvoice.vn/tra-cuu";

    public InvoiceLookupSuggestion? GetSuggestion(string payloadJson, string? sellerTaxCode)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        var transactionId = GetTransactionIdFromPayload(payloadJson);

        return new InvoiceLookupSuggestion(
            ProviderKey,
            "MISA meInvoice",
            LookupUrl,
            string.IsNullOrWhiteSpace(transactionId) ? null : transactionId.Trim(),
            string.IsNullOrWhiteSpace(sellerTaxCode) ? null : sellerTaxCode.Trim());
    }

    /// <summary>Lấy giá trị transaction id từ cttkhac: item có ttruong = "transaction id" (hoặc "transactionid") thì lấy dlieu.</summary>
    private static string? GetTransactionIdFromPayload(string payloadJson)
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
                var normalized = ttStr.Trim().Replace(" ", "").Replace("_", "");
                if (!string.Equals(normalized, "transactionid", StringComparison.OrdinalIgnoreCase)) continue;
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
