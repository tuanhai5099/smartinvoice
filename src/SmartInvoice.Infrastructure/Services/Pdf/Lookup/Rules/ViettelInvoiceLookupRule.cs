using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class ViettelInvoiceLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0100109106";
    private const string BaseUrl = "https://vinvoice.viettel.vn";

    public int Priority => 70;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;
        ViettelInvoicePayloadParsing.TryParsePayload(payload, out _, out var reservationCode);
        var seller = context.SellerTaxCode;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "Viettel",
            BaseUrl,
            string.IsNullOrWhiteSpace(reservationCode) ? null : reservationCode.Trim(),
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}
