using System.Text.Json;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

/// <summary>Đọc PortalLink / Fkey từ payload EasyInvoice (cttkhac, fallback ttkhac).</summary>
public static class EasyInvoicePortalParsing
{
    public static (string? PortalLink, string? Fkey) GetPortalLinkAndFkeyFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            string? portalLink = null;
            string? fkey = null;

            if (r.TryGetProperty("cttkhac", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                    var ttStr = tt.GetString();
                    if (string.IsNullOrWhiteSpace(ttStr)) continue;

                    var dlieu = item.TryGetProperty("dlieu", out var dl) ? dl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(dlieu) && item.TryGetProperty("dLieu", out var dL))
                        dlieu = dL.GetString();
                    var value = string.IsNullOrWhiteSpace(dlieu) ? null : dlieu.Trim();

                    if (string.Equals(ttStr, "PortalLink", StringComparison.OrdinalIgnoreCase))
                        portalLink = value;
                    else if (string.Equals(ttStr, "Fkey", StringComparison.OrdinalIgnoreCase))
                        fkey = value;

                    if (portalLink != null && fkey != null) break;
                }
            }

            if ((portalLink == null || fkey == null) && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

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

            return (portalLink, fkey);
        }
        catch
        {
            return (null, null);
        }
    }
}
