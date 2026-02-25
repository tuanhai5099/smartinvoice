using SmartInvoice.Core.Domain;

namespace SmartInvoice.Core.Repositories;

public interface IBackgroundJobRepository
{
    Task<BackgroundJob> AddAsync(BackgroundJob job, CancellationToken cancellationToken = default);
    Task UpdateAsync(BackgroundJob job, CancellationToken cancellationToken = default);
    Task<BackgroundJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Trả về các job còn pending theo thời gian tạo (dùng cho worker nền).</summary>
    Task<IReadOnlyList<BackgroundJob>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundJob>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

