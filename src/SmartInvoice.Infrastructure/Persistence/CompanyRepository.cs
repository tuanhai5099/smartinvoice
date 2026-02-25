using Microsoft.EntityFrameworkCore;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Persistence;

public class CompanyRepository : ICompanyRepository
{
    private readonly AppDbContext _db;

    public CompanyRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Company?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Company?> GetByIdTrackedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Companies.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Companies.AsNoTracking().OrderBy(c => c.Username).ToListAsync(cancellationToken);
    }

    public async Task<Company> AddAsync(Company company, CancellationToken cancellationToken = default)
    {
        _db.Companies.Add(company);
        await _db.SaveChangesAsync(cancellationToken);
        return company;
    }

    public async Task UpdateAsync(Company company, CancellationToken cancellationToken = default)
    {
        var entry = _db.Companies.Attach(company);
        entry.State = EntityState.Modified;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Companies.FindAsync(new object[] { id }, cancellationToken);
        if (entity != null)
        {
            _db.Companies.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<Company?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        return await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Username == username, cancellationToken);
    }

    public async Task<Company?> GetByCompanyCodeAsync(string companyCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyCode)) return null;
        return await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyCode == companyCode, cancellationToken);
    }
}
