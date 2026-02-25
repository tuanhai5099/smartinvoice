using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>Gợi ý tra cứu cho BKAV eHoadon (0101360697): trang van.ehoadon.vn/Lookup, mã tra cứu = InvoiceGUID (id).</summary>
public sealed class EhoadonLookupProvider : IInvoiceLookupProvider
{
    public string ProviderKey => "0101360697";

    private const string LookupBaseUrl = "https://van.ehoadon.vn/Lookup";

    public InvoiceLookupSuggestion? GetSuggestion(string payloadJson, string? sellerTaxCode)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        var invoiceGuid = GetInvoiceIdFromPayload(payloadJson);
        var searchUrl = string.IsNullOrWhiteSpace(invoiceGuid)
            ? LookupBaseUrl
            : $"{LookupBaseUrl}?InvoiceGUID={Uri.EscapeDataString(invoiceGuid)}";

        return new InvoiceLookupSuggestion(
            ProviderKey,
            "BKAV eHoadon",
            searchUrl,
            string.IsNullOrWhiteSpace(invoiceGuid) ? null : invoiceGuid.Trim(),
            string.IsNullOrWhiteSpace(sellerTaxCode) ? null : sellerTaxCode.Trim());
    }

    /// <summary>Lấy InvoiceGUID từ payload JSON (trường id) hoặc XML (DLHDon/@Id).</summary>
    private static string? GetInvoiceIdFromPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var trimmed = payload.Trim();

        if (trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
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

        if (trimmed.StartsWith("<"))
        {
            try
            {
                var xdoc = System.Xml.Linq.XDocument.Parse(trimmed);
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
