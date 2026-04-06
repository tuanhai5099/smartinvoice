using SmartInvoice.Core.Domain;

namespace SmartInvoice.Core.Repositories;

public interface IBackgroundJobRepository
{
    Task<BackgroundJob> AddAsync(BackgroundJob job, CancellationToken cancellationToken = default);
    Task UpdateAsync(BackgroundJob job, CancellationToken cancellationToken = default);
    Task<BackgroundJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Trả về các job còn pending theo thời gian tạo (dùng cho worker nền).</summary>
    Task<IReadOnlyList<BackgroundJob>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Trong một transaction: chọn job <see cref="BackgroundJobStatus.Pending"/> sớm nhất mà không trùng <see cref="BackgroundJob.CompanyId"/>
    /// với job đang <see cref="BackgroundJobStatus.Running"/>, và tổng số job đang chạy toàn hệ thống &lt; <paramref name="maxConcurrentGlobal"/>;
    /// gán <see cref="BackgroundJobStatus.Running"/> và <see cref="BackgroundJob.StartedAt"/>. Trả về null nếu không claim được.
    /// </summary>
    Task<BackgroundJob?> TryClaimNextRunnableJobAsync(int maxConcurrentGlobal, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackgroundJob>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Job SCO recovery đang chờ/chạy cùng công ty và khoảng ngày (tránh trùng hàng đợi).</summary>
    Task<BackgroundJob?> FindActiveScoRecoveryJobAsync(Guid companyId, DateTime fromDate, DateTime toDate, bool isSold, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

