using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class EinvoiceInvoiceLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0101300842";
    private const string DefaultSearchUrl = "https://einvoice.vn/tra-cuu";

    public int Priority => 30;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var (dcTc, maTc) = EinvoiceTraCuuParsing.GetSearchUrlAndCodeFromPayload(payload);
        var seller = context.SellerTaxCode;
        if (dcTc == null && maTc == null && string.IsNullOrWhiteSpace(seller))
            return null;
        var url = string.IsNullOrWhiteSpace(dcTc) ? DefaultSearchUrl : dcTc;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "E-Invoice",
            url,
            maTc,
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}
