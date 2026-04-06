using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class FastInvoiceLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0100727825";
    private const string SearchPageUrl = "https://invoice.fast.com.vn/tra-cuu-hoa-don-dien-tu/";

    public int Priority => 40;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var keysearch = FastInvoiceKeysearchParsing.GetKeysearchFromPayload(payload);
        var seller = context.SellerTaxCode;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "Fast e-Invoice",
            SearchPageUrl,
            string.IsNullOrWhiteSpace(keysearch) ? null : keysearch.Trim(),
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}
