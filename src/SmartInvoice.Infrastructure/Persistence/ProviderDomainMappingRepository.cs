using Microsoft.EntityFrameworkCore;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Persistence;

public sealed class ProviderDomainMappingRepository : IProviderDomainMappingRepository
{
    private readonly AppDbContext _db;

    public ProviderDomainMappingRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<ProviderDomainMapping?> GetActiveAsync(
        Guid companyId,
        string providerTaxCode,
        string sellerTaxCode,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = Normalize(providerTaxCode);
        var normalizedSeller = Normalize(sellerTaxCode);
        return _db.Set<ProviderDomainMapping>()
            .AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.IsActive)
            .FirstOrDefaultAsync(x =>
                x.ProviderTaxCode == normalizedProvider &&
                x.SellerTaxCode == normalizedSeller,
                cancellationToken);
    }

    public async Task UpsertAsync(ProviderDomainMapping mapping, CancellationToken cancellationToken = default)
    {
        var provider = Normalize(mapping.ProviderTaxCode);
        var seller = Normalize(mapping.SellerTaxCode);
        var existing = await _db.Set<ProviderDomainMapping>()
            .FirstOrDefaultAsync(x =>
                x.CompanyId == mapping.CompanyId &&
                x.ProviderTaxCode == provider &&
                x.SellerTaxCode == seller,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing == null)
        {
            mapping.Id = mapping.Id == Guid.Empty ? Guid.NewGuid() : mapping.Id;
            mapping.ProviderTaxCode = provider;
            mapping.SellerTaxCode = seller;
            mapping.CreatedAt = mapping.CreatedAt == default ? DateTime.Now : mapping.CreatedAt;
            mapping.UpdatedAt = DateTime.Now;
            _db.Set<ProviderDomainMapping>().Add(mapping);
        }
        else
        {
            existing.SearchUrl = mapping.SearchUrl.Trim();
            existing.ProviderName = mapping.ProviderName;
            existing.IsActive = mapping.IsActive;
            existing.UpdatedAt = DateTime.Now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Normalize(string value) => value.Trim().Replace(" ", string.Empty);
}
