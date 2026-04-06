using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup;

internal static class LookupRuleKeys
{
    public static string? NormalizeTaxSegment(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        var dash = t.IndexOf('-');
        if (dash > 0) t = t[..dash];
        return t;
    }

    public static bool MatchesAny(InvoiceLookupResolutionHint h, params string[] keys)
    {
        foreach (var key in keys)
        {
            var nk = NormalizeTaxSegment(key);
            if (string.IsNullOrEmpty(nk)) continue;
            if (string.Equals(NormalizeTaxSegment(h.FetcherRegistryKeyUsed), nk, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(NormalizeTaxSegment(h.LookupRegistryKey), nk, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(NormalizeTaxSegment(h.ProviderTaxCodeFromPayload), nk, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
