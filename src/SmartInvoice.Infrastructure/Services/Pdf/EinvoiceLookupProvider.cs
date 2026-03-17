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

            string? dcTc = null;
            string? maTc = null;

            static string NormalizeLabel(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                var lower = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
                var sb = new System.Text.StringBuilder(lower.Length);
                foreach (var ch in lower)
                {
                    var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                    if (uc == System.Globalization.UnicodeCategory.NonSpacingMark)
                        continue;
                    if (char.IsLetterOrDigit(ch))
                        sb.Append(ch);
                }
                return sb.ToString();
            }

            static void ScanArrayForDcTcAndMaTc(JsonElement arr, ref string? dcTcRef, ref string? maTcRef)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var ttruong = item.TryGetProperty("ttruong", out var tt) ? tt.GetString() : null;
                    if (string.IsNullOrWhiteSpace(ttruong)) continue;

                    var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                        : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                    var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

                    var norm = NormalizeLabel(ttruong);
                    if (dcTcRef == null && (norm == "dctc" || norm.Contains("diachitracuu")))
                        dcTcRef = value;
                    else if (maTcRef == null && (norm == "matc" || norm.Contains("matracuu") || norm.Contains("manhanhoadon")))
                        maTcRef = value;

                    if (dcTcRef != null && maTcRef != null)
                        break;
                }
            }

            // 1) cttkhac
            if (r.TryGetProperty("cttkhac", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                ScanArrayForDcTcAndMaTc(arr, ref dcTc, ref maTc);
            }

            // 2) ttkhac (nếu NCC để DC TC / Mã TC ở đây)
            if ((dcTc == null || maTc == null) && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                ScanArrayForDcTcAndMaTc(ttkhac, ref dcTc, ref maTc);
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

