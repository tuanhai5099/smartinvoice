using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class ThegioididongLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0306731335";
    private const string SearchUrl = "https://hddt.thegioididong.com/";

    public int Priority => 70;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;

        var (_, invoiceCode) = ThegioididongTraCuuParsing.GetLookupInputs(payload);
        var seller = context.SellerTaxCode;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "thegioididong",
            SearchUrl,
            string.IsNullOrWhiteSpace(invoiceCode) ? null : invoiceCode.Trim(),
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}

