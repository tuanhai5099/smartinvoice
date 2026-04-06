using SmartInvoice.Application.Services;
using SmartInvoice.Application.Services.InvoicePayloadParsing;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

public sealed class MeinvoiceInvoiceLookupRule : ILookupResolutionRule
{
    private const string ProviderKey = "0101243150";

    public int Priority => 60;

    public bool CanHandle(InvoiceLookupResolutionHint hint) =>
        !hint.IsEasyInvoice && LookupRuleKeys.MatchesAny(hint, ProviderKey);

    public InvoiceLookupSuggestion? Build(InvoiceContentContext context)
    {
        var payload = context.InvoiceJsonPayload;
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var transactionId = MeinvoiceTransactionParsing.GetTransactionIdFromPayload(payload);
        var seller = context.SellerTaxCode;
        return new InvoiceLookupSuggestion(
            ProviderKey,
            "MISA meInvoice",
            "https://www.meinvoice.vn/tra-cuu",
            string.IsNullOrWhiteSpace(transactionId) ? null : transactionId.Trim(),
            string.IsNullOrWhiteSpace(seller) ? null : seller.Trim());
    }
}
