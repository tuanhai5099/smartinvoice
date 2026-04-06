using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class EasyInvoiceLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0105987432";

    public int Priority => 0;

    public bool CanHandle(InvoiceLookupResolutionHint hint) => hint.IsEasyInvoice;

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var (portalLink, fkey) = EasyInvoicePortalParsing.GetPortalLinkAndFkeyFromPayload(payload);
        var seller = context.SellerTaxCode;
        if (portalLink == null && fkey == null && string.IsNullOrWhiteSpace(seller))
            return null;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "EasyInvoice",
            portalLink,
            fkey,
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}
