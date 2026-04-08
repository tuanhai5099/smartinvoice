using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class EhoadonNetLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0306784030";

    public int Priority => 71;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var seller = context.SellerTaxCode?.Trim();
        var searchUrl = string.IsNullOrWhiteSpace(seller)
            ? "https://ehoadon.net/look-up-invoice"
            : $"https://{seller}.ehoadon.net/look-up-invoice";
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "ehoadon.net",
            searchUrl,
            null,
            seller);
    }
}

