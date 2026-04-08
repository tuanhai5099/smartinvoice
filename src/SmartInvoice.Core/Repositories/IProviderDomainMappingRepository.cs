using SmartInvoice.Core.Domain;

namespace SmartInvoice.Core.Repositories;

public interface IProviderDomainMappingRepository
{
    Task<ProviderDomainMapping?> GetActiveAsync(
        Guid companyId,
        string providerTaxCode,
        string sellerTaxCode,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        ProviderDomainMapping mapping,
        CancellationToken cancellationToken = default);
}
