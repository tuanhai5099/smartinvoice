using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class EhoadonInvoiceLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0101360697";
    private const string LookupBaseUrl = "https://van.ehoadon.vn/Lookup";

    public int Priority => 20;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var invoiceGuid = EhoadonInvoiceIdParsing.GetInvoiceIdFromPayload(context.ContentForFetcher)
                          ?? EhoadonInvoiceIdParsing.GetInvoiceIdFromPayload(context.InvoiceJsonPayload);
        var searchUrl = string.IsNullOrWhiteSpace(invoiceGuid)
            ? LookupBaseUrl
            : $"{LookupBaseUrl}?InvoiceGUID={Uri.EscapeDataString(invoiceGuid)}";
        var seller = context.SellerTaxCode;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "BKAV eHoadon",
            searchUrl,
            string.IsNullOrWhiteSpace(invoiceGuid) ? null : invoiceGuid.Trim(),
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}
