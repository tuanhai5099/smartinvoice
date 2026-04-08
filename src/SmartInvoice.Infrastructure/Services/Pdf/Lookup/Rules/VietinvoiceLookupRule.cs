using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class VietinvoiceLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0106870211";
    private const string SearchUrl = "https://tracuuhoadon.vietinvoice.vn/";

    public int Priority => 65;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;

        var lookupCode = VietinvoiceTraCuuParsing.GetLookupCodeFromPayload(payload);
        var seller = context.SellerTaxCode;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "Vietinvoice",
            SearchUrl,
            string.IsNullOrWhiteSpace(lookupCode) ? null : lookupCode.Trim(),
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}

