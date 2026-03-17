using System.Text.Json;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>Gợi ý tra cứu cho EasyInvoice (0105987432): PortalLink + Fkey trong cttkhac.</summary>
public sealed class EasyInvoiceLookupProvider : IInvoiceLookupProvider
{
    public string ProviderKey => "0105987432";

    public InvoiceLookupSuggestion? GetSuggestion(string payloadJson, string? sellerTaxCode)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            string? portalLink = null;
            string? fkey = null;

            // 1) Ưu tiên đọc từ cttkhac (PortalLink / Fkey)
            if (r.TryGetProperty("cttkhac", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                    var ttStr = tt.GetString();
                    if (string.IsNullOrWhiteSpace(ttStr)) continue;

                    var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                        : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                    var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

                    if (string.Equals(ttStr, "PortalLink", StringComparison.OrdinalIgnoreCase))
                        portalLink = value;
                    else if (string.Equals(ttStr, "Fkey", StringComparison.OrdinalIgnoreCase))
                        fkey = value;

                    if (portalLink != null && fkey != null) break;
                }
            }

            // 2) Fallback: đọc từ ttkhac (một số cấu hình EasyInvoice đẩy PortalLink/Fkey vào đây)
            if ((portalLink == null || fkey == null) && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    // Kiểu 1: giống cttkhac – có ttruong + dlieu/dLieu
                    if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
                    {
                        var ttStr = tt.GetString();
                        if (!string.IsNullOrWhiteSpace(ttStr))
                        {
                            var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                                : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                            var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

                            if (string.Equals(ttStr.Trim(), "PortalLink", StringComparison.OrdinalIgnoreCase) && portalLink == null)
                                portalLink = value;
                            else if (string.Equals(ttStr.Trim(), "Fkey", StringComparison.OrdinalIgnoreCase) && fkey == null)
                                fkey = value;
                        }
                    }

                    // Kiểu 2: PortalLink/Fkey là property trong ttchung bên trong ttkhac
                    if (item.TryGetProperty("ttchung", out var ttchung) && ttchung.ValueKind == JsonValueKind.Object)
                    {
                        if (portalLink == null && ttchung.TryGetProperty("PortalLink", out var p) && p.ValueKind == JsonValueKind.String)
                        {
                            var s = p.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) portalLink = s.Trim();
                        }
                        if (fkey == null && ttchung.TryGetProperty("Fkey", out var fk) && fk.ValueKind == JsonValueKind.String)
                        {
                            var s = fk.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) fkey = s.Trim();
                        }
                    }

                    if (portalLink != null && fkey != null) break;
                }
            }

            if (portalLink == null && fkey == null && string.IsNullOrWhiteSpace(sellerTaxCode))
                return null;

            return new InvoiceLookupSuggestion(
                ProviderKey,
                "EasyInvoice",
                portalLink,
                fkey,
                string.IsNullOrWhiteSpace(sellerTaxCode) ? null : sellerTaxCode.Trim());
        }
        catch
        {
            return null;
        }
    }
}

