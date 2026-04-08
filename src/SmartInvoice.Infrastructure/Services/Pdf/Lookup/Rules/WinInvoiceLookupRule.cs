using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class WinInvoiceLookupRule : ILookupResolutionRule
{
    private const string LookupDisplayKey = "0312303803";
    private const string SearchUrl = "https://tracuu.wininvoice.vn/";

    public int Priority => 10;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, "0104918404", LookupDisplayKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;
        WinInvoiceTraCuuParsing.ExtractFromCttkhac(payload, out var privateCode, out var companyKey);
        var seller = context.SellerTaxCode;
        if (privateCode == null && string.IsNullOrWhiteSpace(seller))
            return null;
        return new InvoiceLookupSuggestion(
            LookupDisplayKey,
            "WinInvoice",
            SearchUrl,
            privateCode,
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim(),
            null,
            false,
            companyKey);
    }
}
