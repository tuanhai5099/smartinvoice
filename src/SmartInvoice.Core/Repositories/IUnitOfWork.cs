namespace SmartInvoice.Core.Repositories;

/// <summary>
/// Unit of Work for transactional operations. Ensures single DbContext scope per operation to avoid bottleneck.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    ICompanyRepository Companies { get; }
    IInvoiceRepository Invoices { get; }
    IBackgroundJobRepository BackgroundJobs { get; }
    IProviderDomainMappingRepository ProviderDomainMappings { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
