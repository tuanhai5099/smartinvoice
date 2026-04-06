using System.Text.Json;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Nhận diện payload đặc biệt (EasyInvoice) để tra cứu khớp với fetcher dù resolver chỉ nhìn msttcgp.
/// </summary>
internal static class InvoicePayloadRouting
{
    public const string EasyInvoiceProviderKey = "0105987432";

    /// <summary>
    /// PortalLink chứa easyinvoice.vn / easy-invoice.com (giống logic cũ trong InvoicePdfService).
    /// </summary>
    public static bool IsEasyInvoiceProvider(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            string? portalLink = null;

            if (r.TryGetProperty("cttkhac", out var cttkhac) && cttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                    var ttStr = tt.GetString();
                    if (string.IsNullOrWhiteSpace(ttStr)) continue;
                    if (!string.Equals(ttStr.Trim(), "PortalLink", StringComparison.OrdinalIgnoreCase)) continue;

                    var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                        : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        portalLink = raw.Trim();
                        break;
                    }
                }
            }

            if (portalLink == null && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
                    {
                        var ttStr = tt.GetString();
                        if (!string.IsNullOrWhiteSpace(ttStr) &&
                            string.Equals(ttStr.Trim(), "PortalLink", StringComparison.OrdinalIgnoreCase))
                        {
                            var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                                : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                            if (!string.IsNullOrWhiteSpace(raw))
                            {
                                portalLink = raw.Trim();
                                break;
                            }
                        }
                    }

                    if (portalLink != null) break;

                    if (item.TryGetProperty("ttchung", out var ttchung) && ttchung.ValueKind == JsonValueKind.Object)
                    {
                        if (ttchung.TryGetProperty("PortalLink", out var p) && p.ValueKind == JsonValueKind.String)
                        {
                            var s = p.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                portalLink = s.Trim();
                                break;
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(portalLink)) return false;
            var link = portalLink.Trim();
            return link.Contains("easyinvoice.vn", StringComparison.OrdinalIgnoreCase)
                   || link.Contains("easy-invoice.com", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
