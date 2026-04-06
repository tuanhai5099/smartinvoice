using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SmartInvoice.Application.Services.InvoicePayloadParsing;

public static class EinvoiceTraCuuParsing
{
    public static (string? SearchUrl, string? MaNhanHoaDon) GetSearchUrlAndCodeFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
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
                var lower = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
                var sb = new StringBuilder(lower.Length);
                foreach (var ch in lower)
                {
                    var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                    if (uc == UnicodeCategory.NonSpacingMark)
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

            if (r.TryGetProperty("cttkhac", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                ScanArrayForDcTcAndMaTc(arr, ref dcTc, ref maTc);
            }

            if ((dcTc == null || maTc == null) && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                ScanArrayForDcTcAndMaTc(ttkhac, ref dcTc, ref maTc);
            }

            if (string.IsNullOrWhiteSpace(maTc))
            {
                maTc = GetStr(r, "maNhanHoaDon") ?? GetStr(r, "MaNhanHoaDon")
                    ?? GetStr(r, "matracuu") ?? GetStr(r, "MaTraCuu")
                    ?? GetStr(r, "maTc") ?? GetStr(r, "MaTC");
            }

            return (dcTc, maTc);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? GetStr(JsonElement el, string propName)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(propName, out var p)) return null;
        var s = p.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
