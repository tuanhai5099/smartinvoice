using Microsoft.EntityFrameworkCore;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;
    private ICompanyRepository? _companies;
    private IInvoiceRepository? _invoices;
    private IBackgroundJobRepository? _backgroundJobs;
    private IProviderDomainMappingRepository? _providerDomainMappings;

    public UnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public ICompanyRepository Companies => _companies ??= new CompanyRepository(_db);
    public IInvoiceRepository Invoices => _invoices ??= new InvoiceRepository(_db);
    public IBackgroundJobRepository BackgroundJobs => _backgroundJobs ??= (IBackgroundJobRepository)Invoices;
    public IProviderDomainMappingRepository ProviderDomainMappings => _providerDomainMappings ??= new ProviderDomainMappingRepository(_db);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
