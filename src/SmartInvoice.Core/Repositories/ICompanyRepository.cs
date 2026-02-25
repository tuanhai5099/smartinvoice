using SmartInvoice.Core.Domain;

namespace SmartInvoice.Core.Repositories;

public interface ICompanyRepository
{
    Task<Company?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary> Get entity with tracking for update. </summary>
    Task<Company?> GetByIdTrackedAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Company> AddAsync(Company company, CancellationToken cancellationToken = default);
    Task UpdateAsync(Company company, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Company?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);
    Task<Company?> GetByCompanyCodeAsync(string companyCode, CancellationToken cancellationToken = default);
}
