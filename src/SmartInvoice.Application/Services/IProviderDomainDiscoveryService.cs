namespace SmartInvoice.Application.Services;

public sealed record ProviderDomainDiscoveryResult(
    bool Found,
    string? SearchUrl,
    bool RequiresUserInput,
    string? Source);

public interface IProviderDomainDiscoveryService
{
    Task<ProviderDomainDiscoveryResult> ResolveAsync(
        Guid companyId,
        string providerTaxCode,
        string sellerTaxCode,
        CancellationToken cancellationToken = default);

    Task SaveOverrideAsync(
        Guid companyId,
        string providerTaxCode,
        string sellerTaxCode,
        string searchUrl,
        string? providerName = null,
        CancellationToken cancellationToken = default);
}
