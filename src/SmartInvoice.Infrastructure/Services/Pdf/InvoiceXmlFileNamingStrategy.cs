using System.Text.Json;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Strategy sinh tên XML chuẩn để mọi luồng (UI/background/PDF) dùng cùng một quy ước.
/// </summary>
public interface IInvoiceXmlFileNamingStrategy
{
    string BuildBaseName(Invoice invoice);
    string BuildBaseName(InvoiceDisplayDto invoice);
}

public sealed class StandardInvoiceXmlFileNamingStrategy : IInvoiceXmlFileNamingStrategy
{
    public string BuildBaseName(Invoice invoice)
    {
        var khmshdon = TryReadKhmshdonFromPayload(invoice.PayloadJson);
        return InvoiceFileStoragePathHelper.BuildXmlBaseName(invoice.KyHieu, khmshdon, invoice.SoHoaDon);
    }

    public string BuildBaseName(InvoiceDisplayDto invoice) =>
        InvoiceFileStoragePathHelper.BuildXmlBaseName(invoice.KyHieu, invoice.Khmshdon, invoice.SoHoaDon);

    private static ushort? TryReadKhmshdonFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in EnumerateCandidates(r))
            {
                if (!candidate.TryGetProperty("khmshdon", out var formCodeProp))
                    continue;

                if (formCodeProp.ValueKind == JsonValueKind.Number)
                    return (ushort)formCodeProp.GetInt32();

                if (formCodeProp.ValueKind == JsonValueKind.String &&
                    ushort.TryParse(formCodeProp.GetString()?.Trim(), out var parsed))
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // best-effort
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateCandidates(JsonElement root)
    {
        yield return root;
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        if (root.TryGetProperty("ndhdon", out var ndhdon) && ndhdon.ValueKind == JsonValueKind.Object)
            yield return ndhdon;
        if (root.TryGetProperty("hdon", out var hdon) && hdon.ValueKind == JsonValueKind.Object)
            yield return hdon;
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
                yield return data;
            else if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && data[0].ValueKind == JsonValueKind.Object)
                yield return data[0];
        }
    }
}
