using System.Text.Json;
using System.Xml.Linq;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

/// <summary>Lấy id hóa đơn / InvoiceGUID từ JSON hoặc XML eHoadon.</summary>
public static class EhoadonInvoiceIdParsing
{
    public static string? GetInvoiceIdFromPayload(string payloadOrXml)
    {
        if (string.IsNullOrWhiteSpace(payloadOrXml)) return null;
        var trimmed = payloadOrXml.Trim();

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
                if (r.TryGetProperty("id", out var idProp))
                {
                    var id = idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : idProp.ToString();
                    return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            try
            {
                var xdoc = XDocument.Parse(trimmed);
                var dlhDon = xdoc.Root?
                    .Descendants()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "DLHDon", StringComparison.OrdinalIgnoreCase));
                var idAttr = dlhDon?.Attribute("Id")?.Value;
                return string.IsNullOrWhiteSpace(idAttr) ? null : idAttr.Trim();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
