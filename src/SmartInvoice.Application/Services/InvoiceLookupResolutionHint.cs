namespace SmartInvoice.Application.Services;

/// <summary>
/// Kết quả “router” tra cứu (cùng nguồn với chọn fetcher PDF): EasyInvoice, key registry, MST từ payload.
/// </summary>
public sealed record InvoiceLookupResolutionHint(
    bool IsEasyInvoice,
    string? FetcherRegistryKeyUsed,
    string? LookupRegistryKey,
    string? ProviderTaxCodeFromPayload,
    string? SellerTaxCodeFromPayload);
