using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class HtInvoiceLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0315638251";
    private const string DefaultSearchUrl = "https://laphoadon.htinvoice.vn/TraCuu";

    public int Priority => 50;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var (dcTc, maTc) = HtInvoiceTraCuuParsing.GetSearchUrlAndCodeFromPayload(payload);
        var seller = context.SellerTaxCode;
        if (dcTc == null && maTc == null && string.IsNullOrWhiteSpace(seller))
            return null;
        var url = string.IsNullOrWhiteSpace(dcTc) ? DefaultSearchUrl : dcTc;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "HTInvoice",
            url,
            maTc,
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}
