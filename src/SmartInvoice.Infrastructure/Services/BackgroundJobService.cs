using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;
using SmartInvoice.Infrastructure.Services.Pdf;

namespace SmartInvoice.Infrastructure.Services;

/// <summary>Service quản lý job nền. Worker dùng <see cref="IUnitOfWorkFactory"/> mỗi vòng lặp (DbContext riêng). Không gọi async DB trên cùng jobUow từ callback Progress (EF không hỗ trợ song song trên một context).</summary>
public class BackgroundJobService : IBackgroundJobService
{
    private const int PdfRedownloadAfterDays = 3;
    private const int MaxConcurrentGlobalJobs = 5;
    private const int WorkerLoopCount = 5;

    private readonly IUnitOfWork _uow;
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly ICompanyAppService _companyService;
    private readonly IInvoiceSyncService _invoiceSyncService;
    private readonly IExcelExportService _excelExportService;
    private readonly IInvoicePdfService _invoicePdfService;
    private readonly IScoSyncRecoveryPlanner _scoRecoveryPlanner;
    private readonly IBackgroundJobCompletedNotifier? _notifier;
    private readonly IBackgroundJobLiveProgressNotifier? _liveProgress;
    private readonly IInvoicePdfProviderResolver _pdfProviderResolver;
    private readonly ILogger _logger;

    private readonly object _workerStartLock = new();
    private bool _workersStarted;
    private readonly CancellationTokenSource _appShutdownCts = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningJobCancels = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _runningJobCompletions = new();
    private int _shutdownNotifyOnce;

    public BackgroundJobService(
        IUnitOfWork uow,
        IUnitOfWorkFactory uowFactory,
        ICompanyAppService companyService,
        IInvoiceSyncService invoiceSyncService,
        IExcelExportService excelExportService,
        IInvoicePdfService invoicePdfService,
        IScoSyncRecoveryPlanner scoRecoveryPlanner,
        IInvoicePdfProviderResolver pdfProviderResolver,
        ILoggerFactory loggerFactory,
        IBackgroundJobCompletedNotifier? notifier = null,
        IBackgroundJobLiveProgressNotifier? liveProgress = null)
    {
        _uow = uow;
        _uowFactory = uowFactory;
        _companyService = companyService;
        _invoiceSyncService = invoiceSyncService;
        _excelExportService = excelExportService;
        _invoicePdfService = invoicePdfService;
        _scoRecoveryPlanner = scoRecoveryPlanner;
        _pdfProviderResolver = pdfProviderResolver;
        _notifier = notifier;
        _liveProgress = liveProgress;
        _logger = loggerFactory.CreateLogger<BackgroundJobService>();
    }

    /// <inheritdoc />
    public void NotifyApplicationStopping()
    {
        if (Interlocked.Exchange(ref _shutdownNotifyOnce, 1) != 0)
            return;
        try
        {
            _appShutdownCts.Cancel();
            foreach (var cts in _runningJobCancels.Values.ToArray())
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                    // ignore
                }
            }

            var tasks = _runningJobCompletions.Values.Select(t => (Task)t.Task).ToArray();
            if (tasks.Length > 0)
                Task.WaitAll(tasks, millisecondsTimeout: 3000);
        }
        catch
        {
            // best-effort khi thoát
        }
    }

    public async Task<BackgroundJobDto> EnqueueDownloadInvoicesAsync(BackgroundJobCreateDto options, CancellationToken cancellationToken = default)
    {
        var company = await _companyService.GetByIdAsync(options.CompanyId).ConfigureAwait(false);
        if (company == null) throw new InvalidOperationException("Công ty không tồn tại.");

        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = BackgroundJobType.DownloadInvoices,
            Status = BackgroundJobStatus.Pending,
            CompanyId = options.CompanyId,
            IsSold = options.IsSold,
            FromDate = options.FromDate.Date,
            ToDate = options.ToDate.Date,
            IncludeDetail = options.IncludeDetail,
            DownloadXml = options.DownloadXml,
            DownloadPdf = options.DownloadPdf,
            ProgressCurrent = 0,
            ProgressTotal = 1, // sẽ cập nhật khi chạy
            Description = $"Tải hóa đơn {(options.IsSold ? "Bán ra" : "Mua vào")} {options.FromDate:dd/MM/yyyy} - {options.ToDate:dd/MM/yyyy}",
            CreatedAt = DateTime.Now
        };

        await _uow.BackgroundJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        StartWorkerIfNeeded();
        return await MapToDtoAsync(job, company.CompanyCode ?? company.CompanyName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackgroundJobDto> EnqueueExportExcelAsync(ExportExcelCreateDto options, CancellationToken cancellationToken = default)
    {
        var company = await _companyService.GetByIdAsync(options.CompanyId).ConfigureAwait(false);
        if (company == null) throw new InvalidOperationException("Công ty không tồn tại.");

        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = BackgroundJobType.ExportExcel,
            Status = BackgroundJobStatus.Pending,
            CompanyId = options.CompanyId,
            IsSold = options.IsSold,
            FromDate = options.FromDate.Date,
            ToDate = options.ToDate.Date,
            IncludeDetail = false,
            DownloadXml = false,
            DownloadPdf = false,
            ExportKey = options.ExportKey ?? "default",
            IsSummaryOnly = options.IsSummaryOnly,
            ProgressCurrent = 0,
            ProgressTotal = 1,
            Description = $"Xuất Excel {(options.IsSummaryOnly ? "Tổng hợp" : "Chi tiết")} {(options.IsSold ? "Bán ra" : "Mua vào")} {options.FromDate:dd/MM/yyyy} - {options.ToDate:dd/MM/yyyy}",
            CreatedAt = DateTime.Now
        };

        await _uow.BackgroundJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        StartWorkerIfNeeded();
        return await MapToDtoAsync(job, company.CompanyCode ?? company.CompanyName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackgroundJobDto> EnqueueDownloadXmlBulkAsync(BulkDownloadCreateDto options, CancellationToken cancellationToken = default)
    {
        var company = await _companyService.GetByIdAsync(options.CompanyId).ConfigureAwait(false);
        if (company == null) throw new InvalidOperationException("Công ty không tồn tại.");
        if (string.IsNullOrWhiteSpace(options.ExportXmlFolderPath))
            throw new ArgumentException("ExportXmlFolderPath bắt buộc cho job tải XML hàng loạt.", nameof(options));
        var payload = JsonSerializer.Serialize(new { InvoiceIds = options.InvoiceIds, ExportXmlFolderPath = options.ExportXmlFolderPath });
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = BackgroundJobType.DownloadXmlBulk,
            Status = BackgroundJobStatus.Pending,
            CompanyId = options.CompanyId,
            IsSold = options.IsSold,
            FromDate = default,
            ToDate = default,
            PayloadJson = payload,
            ProgressCurrent = 0,
            ProgressTotal = Math.Max(1, options.InvoiceIds?.Count ?? 0),
            Description = $"Tải XML hàng loạt {(options.IsSold ? "Bán ra" : "Mua vào")} ({options.InvoiceIds?.Count ?? 0} hóa đơn)",
            CreatedAt = DateTime.Now
        };
        await _uow.BackgroundJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        StartWorkerIfNeeded();
        return await MapToDtoAsync(job, company.CompanyCode ?? company.CompanyName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackgroundJobDto> EnqueueDownloadPdfBulkAsync(BulkDownloadCreateDto options, CancellationToken cancellationToken = default)
    {
        var company = await _companyService.GetByIdAsync(options.CompanyId).ConfigureAwait(false);
        if (company == null) throw new InvalidOperationException("Công ty không tồn tại.");
        var payload = JsonSerializer.Serialize(new { InvoiceIds = options.InvoiceIds });
        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = BackgroundJobType.DownloadPdfBulk,
            Status = BackgroundJobStatus.Pending,
            CompanyId = options.CompanyId,
            IsSold = options.IsSold,
            FromDate = default,
            ToDate = default,
            PayloadJson = payload,
            ProgressCurrent = 0,
            ProgressTotal = Math.Max(1, options.InvoiceIds?.Count ?? 0),
            Description = $"Tải PDF hàng loạt {(options.IsSold ? "Bán ra" : "Mua vào")} ({options.InvoiceIds?.Count ?? 0} hóa đơn)",
            CreatedAt = DateTime.Now
        };
        await _uow.BackgroundJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        StartWorkerIfNeeded();
        return await MapToDtoAsync(job, company.CompanyCode ?? company.CompanyName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackgroundJobDto>> GetRecentJobsAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        var jobs = await _uow.BackgroundJobs.GetRecentAsync(maxCount, cancellationToken).ConfigureAwait(false);
        var companies = await _uow.Companies.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var companyDict = companies.ToDictionary(c => c.Id, c => c.CompanyCode ?? c.CompanyName ?? c.TaxCode ?? "");
        return jobs
            .Select(j => MapToDto(j, companyDict.TryGetValue(j.CompanyId, out var name) ? name : null))
            .ToList();
    }

    public async Task<BackgroundJobDto?> GetJobByIdAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _uow.BackgroundJobs.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null) return null;
        var company = await _companyService.GetByIdAsync(job.CompanyId, cancellationToken).ConfigureAwait(false);
        var label = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode;
        return await MapToDtoAsync(job, label, cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _uow.BackgroundJobs.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null) return;
        job.Status = BackgroundJobStatus.Pending;
        job.ProgressCurrent = 0;
        job.ProgressTotal = 1;
        job.LastError = null;
        job.StartedAt = null;
        job.FinishedAt = null;
        job.SyncCount = 0;
        job.XmlTotal = 0;
        job.XmlDownloadedCount = 0;
        job.XmlFailedCount = 0;
        job.XmlNoXmlCount = 0;
        job.PdfDownloadedCount = 0;
        job.PdfFailedCount = 0;
        job.PdfSkippedCount = 0;
        job.ResultPath = null;
        job.FailureSummaryJson = null;
        await _uow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        StartWorkerIfNeeded();
    }

    public async Task<BackgroundJobDto> EnqueueRetryFailedInvoicesAsync(Guid sourceJobId, BackgroundJobRetryMode mode, CancellationToken cancellationToken = default)
    {
        var source = await _uow.BackgroundJobs.GetByIdAsync(sourceJobId, cancellationToken).ConfigureAwait(false);
        if (source == null)
            throw new InvalidOperationException("Không tìm thấy job nguồn.");

        var summary = JobFailureSummary.Parse(source.FailureSummaryJson);
        var ids = mode switch
        {
            BackgroundJobRetryMode.Detail => summary.DetailFailedIds,
            BackgroundJobRetryMode.Xml => summary.XmlFailedIds,
            BackgroundJobRetryMode.Pdf => summary.PdfFailedIds,
            _ => new List<string>()
        };
        var distinctIds = ids.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        if (distinctIds.Count == 0)
            throw new InvalidOperationException("Không có hóa đơn lỗi để chạy lại cho chế độ này.");

        var company = await _companyService.GetByIdAsync(source.CompanyId, cancellationToken).ConfigureAwait(false);
        if (company == null)
            throw new InvalidOperationException("Công ty không tồn tại.");
        var companyLabel = company.CompanyCode ?? company.CompanyName ?? company.TaxCode ?? "";

        switch (mode)
        {
            case BackgroundJobRetryMode.Detail:
                if (source.Type != BackgroundJobType.DownloadInvoices && source.Type != BackgroundJobType.RefreshInvoiceDetails)
                    throw new InvalidOperationException("Chỉ job đồng bộ hóa đơn (hoặc chạy lại chi tiết) mới có danh sách lỗi chi tiết.");
                var payloadDetail = JsonSerializer.Serialize(new BulkPayloadDto { InvoiceIds = distinctIds });
                var jobDetail = new BackgroundJob
                {
                    Id = Guid.NewGuid(),
                    Type = BackgroundJobType.RefreshInvoiceDetails,
                    Status = BackgroundJobStatus.Pending,
                    CompanyId = source.CompanyId,
                    IsSold = source.IsSold,
                    FromDate = source.FromDate,
                    ToDate = source.ToDate,
                    IncludeDetail = true,
                    DownloadXml = false,
                    DownloadPdf = false,
                    PayloadJson = payloadDetail,
                    ProgressCurrent = 0,
                    ProgressTotal = Math.Max(1, distinctIds.Count),
                    Description = $"Chạy lại chi tiết ({distinctIds.Count} HĐ)",
                    CreatedAt = DateTime.Now
                };
                await _uow.BackgroundJobs.AddAsync(jobDetail, cancellationToken).ConfigureAwait(false);
                StartWorkerIfNeeded();
                return await MapToDtoAsync(jobDetail, companyLabel, cancellationToken).ConfigureAwait(false);

            case BackgroundJobRetryMode.Xml:
                if (source.Type == BackgroundJobType.DownloadXmlBulk)
                {
                    var bulk = DeserializeBulkPayload(source.PayloadJson);
                    if (string.IsNullOrWhiteSpace(bulk?.ExportXmlFolderPath))
                        throw new InvalidOperationException("Job nguồn thiếu thư mục XML.");
                    return await EnqueueDownloadXmlBulkAsync(
                        new BulkDownloadCreateDto(source.CompanyId, source.IsSold, distinctIds, bulk.ExportXmlFolderPath),
                        cancellationToken).ConfigureAwait(false);
                }
                if (source.Type == BackgroundJobType.DownloadInvoices && source.DownloadXml)
                {
                    var code = company.CompanyCode ?? company.CompanyName ?? company.TaxCode ?? source.CompanyId.ToString("N")[..8];
                    var xmlRoot = InvoiceFileStoragePathHelper.GetCompanyXmlRootPath(code);
                    return await EnqueueDownloadXmlBulkAsync(
                        new BulkDownloadCreateDto(source.CompanyId, source.IsSold, distinctIds, xmlRoot),
                        cancellationToken).ConfigureAwait(false);
                }
                throw new InvalidOperationException("Job nguồn không có bước tải XML để chạy lại.");

            case BackgroundJobRetryMode.Pdf:
                if (source.Type == BackgroundJobType.DownloadPdfBulk)
                    return await EnqueueDownloadPdfBulkAsync(
                        new BulkDownloadCreateDto(source.CompanyId, source.IsSold, distinctIds, null),
                        cancellationToken).ConfigureAwait(false);
                if (source.Type == BackgroundJobType.DownloadInvoices && source.DownloadPdf)
                    return await EnqueueDownloadPdfBulkAsync(
                        new BulkDownloadCreateDto(source.CompanyId, source.IsSold, distinctIds, null),
                        cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Chỉ job tải PDF hàng loạt hoặc job tải hóa đơn (có tích PDF) mới có danh sách lỗi PDF.");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    public async Task<BackgroundJobDto?> EnqueueScoRecoveryAsync(ScoRecoveryEnqueueDto options, CancellationToken cancellationToken = default)
    {
        if (!options.Plan.ShouldEnqueue)
            return null;

        var from = options.FromDate.Date;
        var to = options.ToDate.Date;
        var existing = await _uow.BackgroundJobs.FindActiveScoRecoveryJobAsync(options.CompanyId, from, to, options.IsSold, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            var company = await _companyService.GetByIdAsync(options.CompanyId, cancellationToken).ConfigureAwait(false);
            var label = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? "";
            return MapToDto(existing, label);
        }

        var companyForJob = await _companyService.GetByIdAsync(options.CompanyId, cancellationToken).ConfigureAwait(false);
        if (companyForJob == null)
            throw new InvalidOperationException("Công ty không tồn tại.");

        var payloadDto = new ScoRecoveryPayloadDto
        {
            Version = 1,
            ResyncFullDateRange = options.Plan.ResyncFullDateRange,
            ScoDetailExternalIds = options.Plan.ScoDetailExternalIds.Count > 0
                ? options.Plan.ScoDetailExternalIds.ToList()
                : null,
            DetailRetryWaves = 3,
            WaveDelayMs = 8000
        };
        var resyncSteps = options.Plan.ResyncFullDateRange ? 1 : 0;
        var detailWaves = options.IncludeDetail && (options.Plan.ResyncFullDateRange || options.Plan.ScoDetailExternalIds.Count > 0)
            ? Math.Clamp(payloadDto.DetailRetryWaves, 1, 5)
            : 0;
        var progressTotal = resyncSteps + detailWaves;
        if (progressTotal < 1)
            progressTotal = 1;

        var job = new BackgroundJob
        {
            Id = Guid.NewGuid(),
            Type = BackgroundJobType.ScoRecovery,
            Status = BackgroundJobStatus.Pending,
            CompanyId = options.CompanyId,
            IsSold = options.IsSold,
            FromDate = from,
            ToDate = to,
            IncludeDetail = options.IncludeDetail,
            DownloadXml = false,
            DownloadPdf = false,
            ProgressCurrent = 0,
            ProgressTotal = progressTotal,
            PayloadJson = JsonSerializer.Serialize(payloadDto),
            Description = $"Phục hồi SCO (máy tính tiền) {(options.IsSold ? "Bán ra" : "Mua vào")} {from:dd/MM/yyyy} - {to:dd/MM/yyyy}",
            CreatedAt = DateTime.Now
        };

        await _uow.BackgroundJobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        StartWorkerIfNeeded();
        return await MapToDtoAsync(job, companyForJob.CompanyCode ?? companyForJob.CompanyName, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _uow.BackgroundJobs.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null) return;
        if (job.Status == BackgroundJobStatus.Pending)
        {
            job.Status = BackgroundJobStatus.Cancelled;
            job.FinishedAt = DateTime.Now;
            await _uow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (job.Status == BackgroundJobStatus.Running
            && _runningJobCancels.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // ignore
            }
        }
    }

    public async Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _uow.BackgroundJobs.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null) return;
        if (job.Status == BackgroundJobStatus.Running)
        {
            if (_runningJobCancels.TryGetValue(jobId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                    // ignore
                }
            }

            if (_runningJobCompletions.TryGetValue(jobId, out var tcs))
                await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
        }

        await _uow.BackgroundJobs.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    private void StartWorkerIfNeeded()
    {
        lock (_workerStartLock)
        {
            if (_workersStarted) return;
            _workersStarted = true;
            for (var i = 0; i < WorkerLoopCount; i++)
                _ = Task.Run(() => WorkerLoopAsync(_appShutdownCts.Token));
        }
    }

    private async Task WorkerLoopAsync(CancellationToken shutdownToken)
    {
        while (!shutdownToken.IsCancellationRequested)
        {
            try
            {
                var jobUow = _uowFactory.Create();
                try
                {
                    var job = await jobUow.BackgroundJobs
                        .TryClaimNextRunnableJobAsync(MaxConcurrentGlobalJobs, shutdownToken)
                        .ConfigureAwait(false);
                    if (job == null)
                    {
                        await Task.Delay(2000, shutdownToken).ConfigureAwait(false);
                        continue;
                    }

                    var jobCts = new CancellationTokenSource();
                    _runningJobCancels[job.Id] = jobCts;
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _runningJobCompletions[job.Id] = tcs;
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, jobCts.Token);
                        await RunJobAsync(job, jobUow, linked.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _runningJobCancels.TryRemove(job.Id, out var removeCts);
                        removeCts?.Dispose();
                        _runningJobCompletions.TryRemove(job.Id, out _);
                        tcs.TrySetResult(true);
                    }
                }
                finally
                {
                    if (jobUow is IAsyncDisposable asyncDisposable)
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background job worker loop error.");
                try
                {
                    await Task.Delay(5000, shutdownToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task RunJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting background job {Id} type {Type}", job.Id, job.Type);
        job.Status = BackgroundJobStatus.Running;
        job.StartedAt = DateTime.Now;
        job.ProgressCurrent = 0;
        if (job.Type != BackgroundJobType.DownloadXmlBulk
            && job.Type != BackgroundJobType.DownloadPdfBulk
            && job.Type != BackgroundJobType.RefreshInvoiceDetails
            && job.Type != BackgroundJobType.ScoRecovery)
            job.ProgressTotal = job.Type == BackgroundJobType.ExportExcel ? 1 : 2;
        job.LastError = null;
        job.PdfDownloadedCount = 0;
        job.PdfFailedCount = 0;
        job.PdfSkippedCount = 0;
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        try
        {
            switch (job.Type)
            {
                case BackgroundJobType.DownloadInvoices:
                    await RunDownloadInvoicesJobAsync(job, jobUow, cancellationToken).ConfigureAwait(false);
                    break;
                case BackgroundJobType.ExportExcel:
                    await RunExportExcelJobAsync(job, jobUow, cancellationToken).ConfigureAwait(false);
                    break;
                case BackgroundJobType.DownloadXmlBulk:
                    await RunDownloadXmlBulkJobAsync(job, jobUow, cancellationToken).ConfigureAwait(false);
                    break;
                case BackgroundJobType.DownloadPdfBulk:
                    await RunDownloadPdfBulkJobAsync(job, jobUow, cancellationToken).ConfigureAwait(false);
                    break;
                case BackgroundJobType.RefreshInvoiceDetails:
                    await RunRefreshInvoiceDetailsJobAsync(job, jobUow, cancellationToken).ConfigureAwait(false);
                    break;
                case BackgroundJobType.ScoRecovery:
                    await RunScoRecoveryJobAsync(job, jobUow, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Job type {job.Type} không được hỗ trợ.");
            }

            job.Status = BackgroundJobStatus.Completed;
            job.FinishedAt = DateTime.Now;
            // Không gán LastError = null ở đây: các bước Run* có thể ghi cảnh báo (vd. chạy lại chi tiết còn HĐ lỗi).
            // LastError đã được xóa khi bắt đầu job (trên).
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Status = BackgroundJobStatus.Cancelled;
            job.FinishedAt = DateTime.Now;
            job.LastError = null;
        }
        catch (Exception ex)
        {
            job.Status = BackgroundJobStatus.Failed;
            job.FinishedAt = DateTime.Now;
            job.LastError = BuildErrorMessage(ex);
            _logger.LogError(ex, "Job {Id} failed.", job.Id);
        }
        finally
        {
            try
            {
                await jobUow.BackgroundJobs.UpdateAsync(job, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Job {JobId} final update skipped.", job.Id);
            }

            _ = NotifyJobCompletedAsync(job, CancellationToken.None);
        }
    }

    private async Task NotifyJobCompletedAsync(BackgroundJob job, CancellationToken cancellationToken)
    {
        if (_notifier == null) return;
        try
        {
            var company = await _companyService.GetByIdAsync(job.CompanyId, cancellationToken).ConfigureAwait(false);
            var displayName = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? "";
            var dto = MapToDto(job, displayName);
            _notifier.Notify(dto);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notify job completed failed.");
        }
    }

    private async Task RunDownloadInvoicesJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        var failureSummary = new JobFailureSummary();
        // Bước 1: đồng bộ hóa đơn từ API
        var syncResult = await _invoiceSyncService.SyncInvoicesAsync(
            job.CompanyId,
            job.FromDate,
            job.ToDate,
            job.IncludeDetail,
            isSold: job.IsSold,
            cancellationToken).ConfigureAwait(false);

        if (syncResult.DetailFailureItems is { Count: > 0 } detailItems)
        {
            foreach (var it in detailItems)
            {
                failureSummary.DetailFailures.Add(it);
                if (!failureSummary.DetailFailedIds.Contains(it.ExternalId))
                    failureSummary.DetailFailedIds.Add(it.ExternalId);
            }
        }
        else if (syncResult.Success && syncResult.DetailFetchFailedExternalIds is { Count: > 0 } detailFailed)
        {
            failureSummary.DetailFailedIds.AddRange(detailFailed);
        }

        job.SyncCount = syncResult.TotalSynced;
        job.ProgressCurrent = 1;
        job.ProgressTotal = 2; // 1 = đồng bộ xong, 2 = tải XML xong (hoặc không tải)
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        string? companyCodeForFiles = null;
        string? companyRootForFiles = null;

        // Bước 2: nếu yêu cầu, tải XML cho tất cả hóa đơn theo bộ lọc tương ứng
        if (job.DownloadXml)
        {
            var xmlFailedLock = new object();
            var xmlFailedIds = new List<string>();
            var xmlFailMessages = new Dictionary<string, string>(StringComparer.Ordinal);
            var filter = new InvoiceListFilterDto(
                job.FromDate,
                job.ToDate.AddDays(1).AddSeconds(-1),
                job.IsSold,
                null,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "NgayLap",
                true);

            var (page, totalCount, _) = await _invoiceSyncService.GetInvoicesPagedAsync(
                job.CompanyId, filter, page: 1, pageSize: int.MaxValue, cancellationToken).ConfigureAwait(false);

            job.XmlTotal = totalCount;

            var company = await _companyService.GetByIdAsync(job.CompanyId, cancellationToken).ConfigureAwait(false);
            var companyCode = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? job.CompanyId.ToString("N")[..8];
            // XML từng HĐ: Documents\SmartInvoice\{công ty}\XML\yyyy_MM\ (đồng bộ với UI danh sách hóa đơn).
            var xmlRoot = InvoiceFileStoragePathHelper.GetCompanyXmlRootPath(companyCode);
            Directory.CreateDirectory(xmlRoot);
            // ZIP gom nhanh: cùng cấp thư mục XML (thư mục công ty), không nằm trong XML\.
            var companyRoot = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCode);
            Directory.CreateDirectory(companyRoot);
            companyCodeForFiles = companyCode;
            companyRootForFiles = companyRoot;

            // Không gọi UpdateAsync từ callback Progress: EF không cho nhiều thao tác đồng thời trên cùng DbContext (jobUow).
            var progress = new Progress<DownloadXmlProgress>(p =>
            {
                job.ProgressCurrent = 1 + p.Current;
                job.ProgressTotal = totalCount <= 0 ? 2 : 1 + totalCount;
                if (p.ItemResult is { Success: false, NoXml: false, ExternalInvoiceId: { Length: > 0 } ext })
                {
                    lock (xmlFailedLock)
                    {
                        xmlFailedIds.Add(ext);
                        var m = p.ItemResult.Message;
                        if (!string.IsNullOrWhiteSpace(m))
                            xmlFailMessages[ext] = m!;
                        else
                            xmlFailMessages.TryAdd(ext, "Tải file XML thất bại.");
                    }
                }
            });

            var downloadResult = await _invoiceSyncService.DownloadInvoicesXmlAsync(job.CompanyId, page, xmlRoot, progress, cancellationToken, zipOutputDirectory: companyRoot)
                .ConfigureAwait(false);

            job.XmlDownloadedCount = downloadResult.DownloadedCount;
            job.XmlFailedCount = downloadResult.FailedCount;
            job.XmlNoXmlCount = downloadResult.NoXmlCount;
            if (downloadResult.ZipPath != null)
                job.ResultPath = downloadResult.ZipPath;
            else if (downloadResult.DownloadedCount > 0)
                job.ResultPath = xmlRoot;
            job.ProgressCurrent = job.ProgressTotal;
            var distinctXml = xmlFailedIds.Distinct(StringComparer.Ordinal).ToList();
            failureSummary.XmlFailedIds.AddRange(distinctXml);
            if (distinctXml.Count > 0)
            {
                var invs = await _invoiceSyncService.GetInvoicesByIdsAsync(job.CompanyId, distinctXml, cancellationToken).ConfigureAwait(false);
                var byId = invs.ToDictionary(i => i.Id, StringComparer.Ordinal);
                foreach (var id in distinctXml)
                {
                    byId.TryGetValue(id, out var inv);
                    var msg = xmlFailMessages.TryGetValue(id, out var m) ? m : "Tải file XML thất bại.";
                    failureSummary.XmlFailures.Add(new InvoiceFailureItem
                    {
                        ExternalId = id,
                        KyHieu = inv?.KyHieu,
                        Khmshdon = inv?.Khmshdon ?? 0,
                        SoHoaDon = inv?.SoHoaDon ?? 0,
                        ErrorMessage = msg
                    });
                }
            }
        }
        else
        {
            job.ProgressCurrent = job.ProgressTotal;
        }

        if (job.DownloadPdf)
        {
            if (companyCodeForFiles == null || companyRootForFiles == null)
            {
                var companyP = await _companyService.GetByIdAsync(job.CompanyId, cancellationToken).ConfigureAwait(false);
                companyCodeForFiles = companyP?.CompanyCode ?? companyP?.CompanyName ?? companyP?.TaxCode ?? job.CompanyId.ToString("N")[..8];
                companyRootForFiles = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCodeForFiles);
                Directory.CreateDirectory(companyRootForFiles);
            }

            var filterPdf = new InvoiceListFilterDto(
                job.FromDate,
                job.ToDate.AddDays(1).AddSeconds(-1),
                job.IsSold,
                null,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                "NgayLap",
                true);
            var (pagePdf, _, _) = await _invoiceSyncService.GetInvoicesPagedAsync(
                job.CompanyId, filterPdf, page: 1, pageSize: int.MaxValue, cancellationToken).ConfigureAwait(false);
            var orderedIds = pagePdf.Select(p => p.Id).ToList();
            var entitiesPdf = await jobUow.Invoices.GetByCompanyAndExternalIdsAsync(job.CompanyId, orderedIds, cancellationToken).ConfigureAwait(false);
            var orderMapPdf = orderedIds
                .Select((id, idx) => (id, idx))
                .ToDictionary(x => x.id, x => x.idx, StringComparer.Ordinal);
            var sortedPdf = entitiesPdf
                .OrderBy(e => orderMapPdf.TryGetValue(e.ExternalId, out var o) ? o : int.MaxValue)
                .ToList();
            var phaseEnd = job.ProgressCurrent;
            if (sortedPdf.Count > 0)
            {
                await RunPdfDownloadCoreAsync(
                    job,
                    jobUow,
                    companyCodeForFiles,
                    companyRootForFiles,
                    sortedPdf,
                    phaseEnd,
                    failureSummary,
                    useBulkXmlCountFields: false,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        job.FailureSummaryJson = failureSummary.DetailFailedIds.Count > 0
            || failureSummary.XmlFailedIds.Count > 0
            || failureSummary.PdfFailedIds.Count > 0
            ? failureSummary.ToJson()
            : null;
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        if (syncResult.Success)
        {
            var recoveryPlan = _scoRecoveryPlanner.Plan(syncResult, job.IncludeDetail);
            if (recoveryPlan.ShouldEnqueue)
            {
                try
                {
                    await EnqueueScoRecoveryAsync(new ScoRecoveryEnqueueDto(
                        job.CompanyId,
                        job.IsSold,
                        job.FromDate,
                        job.ToDate,
                        job.IncludeDetail,
                        recoveryPlan), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SCO recovery enqueue after download-invoices job failed.");
                }
            }
        }
    }

    private async Task RunScoRecoveryJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        var payload = DeserializeScoRecoveryPayload(job.PayloadJson);
        if (payload == null)
            throw new InvalidOperationException("SCO recovery: payload không hợp lệ.");

        var waves = job.IncludeDetail ? Math.Clamp(payload.DetailRetryWaves, 1, 5) : 0;
        var waveDelay = TimeSpan.FromMilliseconds(Math.Clamp(payload.WaveDelayMs, 2000, 60_000));

        var detailIds = new HashSet<string>(payload.ScoDetailExternalIds ?? [], StringComparer.Ordinal);
        var step = 0;

        if (payload.ResyncFullDateRange)
        {
            var syncResult = await _invoiceSyncService.SyncInvoicesAsync(
                job.CompanyId,
                job.FromDate,
                job.ToDate,
                job.IncludeDetail,
                job.IsSold,
                cancellationToken).ConfigureAwait(false);
            if (!syncResult.Success)
                throw new InvalidOperationException(syncResult.Message ?? "Đồng bộ lại SCO thất bại.");
            job.SyncCount = syncResult.TotalSynced;
            if (syncResult.ScoDetailFailedExternalIds is { Count: > 0 } scoFails)
            {
                foreach (var id in scoFails)
                    detailIds.Add(id);
            }
            step++;
            job.ProgressCurrent = step;
            await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        }

        var remaining = detailIds.ToList();
        job.XmlTotal = remaining.Count;

        if (job.IncludeDetail && waves > 0)
        {
            for (var w = 0; w < waves && remaining.Count > 0; w++)
            {
                if (w > 0)
                    await Task.Delay(waveDelay, cancellationToken).ConfigureAwait(false);

                // Chỉ cập nhật DB giữa các wave (await bên dưới); không fire-and-forget UpdateAsync trong Progress → tránh DbContext concurrent.
                var progress = new Progress<int>(_ => { job.ProgressCurrent = step; });
                var refresh = await _invoiceSyncService.RefreshInvoiceDetailsAsync(job.CompanyId, remaining, progress, cancellationToken).ConfigureAwait(false);
                remaining = refresh.StillFailedExternalIds.ToList();
                step++;
                job.ProgressCurrent = step;
                await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            }
        }

        job.XmlFailedCount = remaining.Count;
        if (remaining.Count > 0)
        {
            var invs = await _invoiceSyncService.GetInvoicesByIdsAsync(job.CompanyId, remaining, cancellationToken).ConfigureAwait(false);
            var summary = new JobFailureSummary { DetailFailedIds = remaining };
            summary.DetailFailures.AddRange(BuildInvoiceFailureItemsFromDisplay(invs, remaining, "Chưa lấy được chi tiết sau các wave phục hồi SCO."));
            job.FailureSummaryJson = summary.ToJson();
        }
        else
            job.FailureSummaryJson = null;
        job.ProgressCurrent = job.ProgressTotal;
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunRefreshInvoiceDetailsJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        var payload = DeserializeBulkPayload(job.PayloadJson);
        var ids = payload?.InvoiceIds ?? new List<string>();
        job.XmlTotal = ids.Count;
        if (ids.Count == 0)
        {
            job.ProgressCurrent = job.ProgressTotal;
            job.FailureSummaryJson = null;
            await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }

        var progress = new Progress<int>(n => { job.ProgressCurrent = n; });
        var result = await _invoiceSyncService.RefreshInvoiceDetailsAsync(job.CompanyId, ids, progress, cancellationToken).ConfigureAwait(false);
        job.SyncCount = result.SuccessCount;
        job.XmlFailedCount = result.FailedCount;
        job.ProgressCurrent = job.ProgressTotal;
        var stillIds = result.StillFailedExternalIds.ToList();
        if (stillIds.Count > 0)
        {
            var invs = await _invoiceSyncService.GetInvoicesByIdsAsync(job.CompanyId, stillIds, cancellationToken).ConfigureAwait(false);
            var still = new JobFailureSummary { DetailFailedIds = stillIds };
            still.DetailFailures.AddRange(BuildInvoiceFailureItemsFromDisplay(invs, stillIds, "Chưa lấy được chi tiết sau khi chạy lại."));
            job.FailureSummaryJson = still.ToJson();
            job.LastError = stillIds.Count >= ids.Count
                ? $"Không lấy được chi tiết cho {stillIds.Count}/{ids.Count} hóa đơn (đăng nhập, mạng, timeout hoặc API)."
                : $"Còn {stillIds.Count}/{ids.Count} hóa đơn chưa lấy được chi tiết. Xem báo cáo chi tiết trong quản lý job.";
        }
        else
        {
            job.FailureSummaryJson = null;
            job.LastError = null;
        }

        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        if (result.SuccessCount == 0 && ids.Count > 0)
            throw new InvalidOperationException(job.LastError ?? "Chạy lại chi tiết không thành công cho hóa đơn nào.");
    }

    private async Task RunExportExcelJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        var request = new ExportExcelRequest(
            job.CompanyId,
            job.IsSold,
            job.FromDate,
            job.ToDate,
            job.ExportKey ?? "default",
            job.IsSummaryOnly);
        var path = await _excelExportService.ExportAsync(request, cancellationToken).ConfigureAwait(false);
        job.ResultPath = path;
        job.ProgressCurrent = 1;
        job.ProgressTotal = 1;
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunDownloadXmlBulkJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        var payload = DeserializeBulkPayload(job.PayloadJson);
        if (payload?.InvoiceIds == null || payload.InvoiceIds.Count == 0)
        {
            job.XmlTotal = 0;
            job.ProgressCurrent = job.ProgressTotal;
            job.FailureSummaryJson = null;
            await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(payload.ExportXmlFolderPath))
        {
            job.LastError = "Thiếu thư mục lưu XML.";
            throw new InvalidOperationException(job.LastError);
        }
        var company = await _companyService.GetByIdAsync(job.CompanyId, cancellationToken).ConfigureAwait(false);
        var companyCode = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? job.CompanyId.ToString("N")[..8];
        var companyRoot = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCode);
        var invoices = await _invoiceSyncService.GetInvoicesByIdsAsync(job.CompanyId, payload.InvoiceIds, cancellationToken).ConfigureAwait(false);
        job.XmlTotal = invoices.Count;
        var xmlFailedLock = new object();
        var xmlFailedIds = new List<string>();
        var xmlFailMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        var progress = new Progress<DownloadXmlProgress>(p =>
        {
            job.ProgressCurrent = p.Current;
            if (p.ItemResult is { Success: false, NoXml: false, ExternalInvoiceId: { Length: > 0 } ext })
            {
                lock (xmlFailedLock)
                {
                    xmlFailedIds.Add(ext);
                    var m = p.ItemResult.Message;
                    if (!string.IsNullOrWhiteSpace(m))
                        xmlFailMessages[ext] = m!;
                    else
                        xmlFailMessages.TryAdd(ext, "Tải file XML thất bại.");
                }
            }
            _liveProgress?.ReportBulkXmlProgress(job.Id, p);
        });
        var result = await _invoiceSyncService.DownloadInvoicesXmlAsync(job.CompanyId, invoices, payload.ExportXmlFolderPath, progress, cancellationToken, zipOutputDirectory: companyRoot).ConfigureAwait(false);
        job.XmlDownloadedCount = result.DownloadedCount;
        job.XmlFailedCount = result.FailedCount;
        job.XmlNoXmlCount = result.NoXmlCount;
        job.ProgressCurrent = job.ProgressTotal;
        if (result.ZipPath != null)
            job.ResultPath = result.ZipPath;
        else if (result.DownloadedCount > 0 && !string.IsNullOrWhiteSpace(payload.ExportXmlFolderPath))
            job.ResultPath = payload.ExportXmlFolderPath.Trim();
        var distinctXmlFailed = xmlFailedIds.Distinct(StringComparer.Ordinal).ToList();
        if (distinctXmlFailed.Count > 0)
        {
            var byId = invoices.ToDictionary(i => i.Id, StringComparer.Ordinal);
            var summary = new JobFailureSummary { XmlFailedIds = distinctXmlFailed };
            foreach (var id in distinctXmlFailed)
            {
                byId.TryGetValue(id, out var inv);
                var msg = xmlFailMessages.TryGetValue(id, out var m) ? m : "Tải file XML thất bại.";
                summary.XmlFailures.Add(new InvoiceFailureItem
                {
                    ExternalId = id,
                    KyHieu = inv?.KyHieu,
                    Khmshdon = inv?.Khmshdon ?? 0,
                    SoHoaDon = inv?.SoHoaDon ?? 0,
                    ErrorMessage = msg
                });
            }
            job.FailureSummaryJson = summary.ToJson();
        }
        else
            job.FailureSummaryJson = null;
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunDownloadPdfBulkJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        var payload = DeserializeBulkPayload(job.PayloadJson);
        if (payload?.InvoiceIds == null || payload.InvoiceIds.Count == 0)
        {
            job.ProgressCurrent = job.ProgressTotal;
            job.FailureSummaryJson = null;
            await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }

        var company = await _companyService.GetByIdAsync(job.CompanyId, cancellationToken).ConfigureAwait(false);
        var companyCode = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? job.CompanyId.ToString("N")[..8];
        var companyRoot = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCode);
        var entities = await jobUow.Invoices.GetByCompanyAndExternalIdsAsync(job.CompanyId, payload.InvoiceIds, cancellationToken).ConfigureAwait(false);
        var orderMap = payload.InvoiceIds
            .Select((id, idx) => (id, idx))
            .ToDictionary(x => x.id, x => x.idx, StringComparer.Ordinal);
        var sortedEntities = entities
            .OrderBy(e => orderMap.TryGetValue(e.ExternalId, out var o) ? o : int.MaxValue)
            .ToList();
        job.XmlTotal = sortedEntities.Count;
        var pdfFailures = new JobFailureSummary();
        await RunPdfDownloadCoreAsync(
            job,
            jobUow,
            companyCode,
            companyRoot,
            sortedEntities,
            progressIndexBase: 0,
            pdfFailures,
            useBulkXmlCountFields: true,
            cancellationToken).ConfigureAwait(false);
        if (pdfFailures.PdfFailedIds.Count > 0)
            job.FailureSummaryJson = pdfFailures.ToJson();
        else
            job.FailureSummaryJson = null;
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="useBulkXmlCountFields">true = job PDF bulk (ghi XmlDownloadedCount/…); false = bước PDF trong job tải hóa đơn (ghi PdfDownloadedCount/…).</param>
    private async Task RunPdfDownloadCoreAsync(
        BackgroundJob job,
        IUnitOfWork jobUow,
        string companyCode,
        string companyRoot,
        IReadOnlyList<Invoice> sortedEntities,
        int progressIndexBase,
        JobFailureSummary failureSummary,
        bool useBulkXmlCountFields,
        CancellationToken cancellationToken)
    {
        int successCount = 0, failCount = 0, skipCount = 0;
        var failDetails = new List<string>();
        var pdfFailedExternalIds = new List<string>();
        var pdfFailureById = new Dictionary<string, InvoiceFailureItem>(StringComparer.Ordinal);
        var savedPdfPaths = new List<string>();
        var total = sortedEntities.Count;
        if (total > 0)
            job.ProgressTotal = progressIndexBase + total;

        for (var i = 0; i < sortedEntities.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inv = sortedEntities[i];
            var display = $"{inv.KyHieu}-{inv.SoHoaDon}";
            job.ProgressCurrent = progressIndexBase + i + 1;
            await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            _liveProgress?.ReportBulkPdfItem(new BulkPdfItemProgress(
                job.Id, i + 1, total, inv.ExternalId, display, false, false, false, "Đang tải PDF…", IsStartOfItem: true));
            InvoicePdfProviderMetadata? meta = null;
            if (!string.IsNullOrWhiteSpace(inv.PayloadJson))
            {
                try
                {
                    meta = _pdfProviderResolver.ResolveMetadata(inv.PayloadJson);
                }
                catch
                {
                    // ignored
                }
            }

            try
            {
                var pdfPath = InvoiceFileStoragePathHelper.GetInvoicePdfPath(companyCode, inv.KyHieu, inv.SoHoaDon, inv.NgayLap);
                var pdfFolder = Path.GetDirectoryName(pdfPath);
                if (!string.IsNullOrEmpty(pdfFolder))
                    Directory.CreateDirectory(pdfFolder);

                if (File.Exists(pdfPath))
                {
                    var lastWrite = File.GetLastWriteTime(pdfPath);
                    if ((DateTime.Now - lastWrite).TotalDays < PdfRedownloadAfterDays)
                    {
                        savedPdfPaths.Add(pdfPath);
                        successCount++;
                        _liveProgress?.ReportBulkPdfItem(new BulkPdfItemProgress(
                            job.Id, i + 1, total, inv.ExternalId, display, true, false, false, "Đã có PDF trên máy"));
                        continue;
                    }
                }

                var result = await _invoicePdfService.GetPdfForInvoiceByExternalIdAsync(job.CompanyId, inv.ExternalId, cancellationToken).ConfigureAwait(false);
                if (result is InvoicePdfResult.Success success)
                {
                    await File.WriteAllBytesAsync(pdfPath, success.PdfBytes, cancellationToken).ConfigureAwait(false);
                    savedPdfPaths.Add(pdfPath);
                    successCount++;
                    _liveProgress?.ReportBulkPdfItem(new BulkPdfItemProgress(
                        job.Id, i + 1, total, inv.ExternalId, display, true, false, false, "Đã tải PDF"));
                }
                else if (result is InvoicePdfResult.Failure f)
                {
                    var msg = f.ErrorMessage ?? "";
                    var needsManual = (meta?.MayRequireUserIntervention == true) ||
                                      msg.Contains("captcha", StringComparison.OrdinalIgnoreCase);
                    if (msg.Contains("thiếu", StringComparison.OrdinalIgnoreCase) || msg.Contains("chưa hỗ trợ", StringComparison.OrdinalIgnoreCase) || msg.Contains("không có", StringComparison.OrdinalIgnoreCase))
                    {
                        skipCount++;
                        _liveProgress?.ReportBulkPdfItem(new BulkPdfItemProgress(
                            job.Id, i + 1, total, inv.ExternalId, display, false, true, false, msg));
                    }
                    else
                    {
                        failCount++;
                        pdfFailedExternalIds.Add(inv.ExternalId);
                        if (failDetails.Count < 5)
                            failDetails.Add($"{inv.KyHieu}-{inv.SoHoaDon}: {msg}");
                        var hint = needsManual ? " Có thể cần tra cứu / thao tác thủ công trên cổng NCC." : string.Empty;
                        var fullMsg = msg + hint;
                        pdfFailureById[inv.ExternalId] = new InvoiceFailureItem
                        {
                            ExternalId = inv.ExternalId,
                            KyHieu = inv.KyHieu,
                            Khmshdon = 0,
                            SoHoaDon = inv.SoHoaDon,
                            ErrorMessage = fullMsg.Trim()
                        };
                        _liveProgress?.ReportBulkPdfItem(new BulkPdfItemProgress(
                            job.Id, i + 1, total, inv.ExternalId, display, false, false, needsManual, fullMsg));
                    }
                }
                else
                {
                    failCount++;
                    pdfFailedExternalIds.Add(inv.ExternalId);
                    if (failDetails.Count < 5)
                        failDetails.Add($"{inv.KyHieu}-{inv.SoHoaDon}: Không lấy được PDF.");
                    pdfFailureById[inv.ExternalId] = new InvoiceFailureItem
                    {
                        ExternalId = inv.ExternalId,
                        KyHieu = inv.KyHieu,
                        Khmshdon = 0,
                        SoHoaDon = inv.SoHoaDon,
                        ErrorMessage = "Không lấy được PDF."
                    };
                    _liveProgress?.ReportBulkPdfItem(new BulkPdfItemProgress(
                        job.Id, i + 1, total, inv.ExternalId, display, false, false, false, "Không lấy được PDF."));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PDF bulk failed for invoice {ExternalId}", inv.ExternalId);
                failCount++;
                pdfFailedExternalIds.Add(inv.ExternalId);
                if (failDetails.Count < 5)
                    failDetails.Add($"{inv.KyHieu}-{inv.SoHoaDon}: {ex.Message}");
                pdfFailureById[inv.ExternalId] = new InvoiceFailureItem
                {
                    ExternalId = inv.ExternalId,
                    KyHieu = inv.KyHieu,
                    Khmshdon = 0,
                    SoHoaDon = inv.SoHoaDon,
                    ErrorMessage = ex.Message
                };
                _liveProgress?.ReportBulkPdfItem(new BulkPdfItemProgress(
                    job.Id, i + 1, total, inv.ExternalId, display, false, false, false, "Lỗi: " + ex.Message));
            }
        }

        if (savedPdfPaths.Count > 0)
        {
            try
            {
                var zipName = $"HoaDonPdf_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                var zipPath = Path.Combine(companyRoot, zipName);
                if (File.Exists(zipPath)) File.Delete(zipPath);
                using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var pdfPath in savedPdfPaths)
                    {
                        if (!File.Exists(pdfPath)) continue;
                        var entryName = Path.GetFileName(pdfPath);
                        var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);
                        await using (var entryStream = entry.Open())
                        await using (var fileStream = File.OpenRead(pdfPath))
                            await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                    }
                }
                job.ResultPath = zipPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF bulk: failed to create ZIP at {CompanyRoot}", companyRoot);
                job.LastError = $"Tải PDF xong nhưng không tạo được file ZIP: {ex.Message}";
            }
        }

        if (failCount > 0 && string.IsNullOrWhiteSpace(job.LastError))
        {
            var detail = failDetails.Count > 0 ? $" Ví dụ lỗi: {string.Join(" | ", failDetails)}" : string.Empty;
            job.LastError = $"Có {failCount} hóa đơn tải PDF thất bại.{detail}";
        }

        if (useBulkXmlCountFields)
        {
            job.XmlDownloadedCount = successCount;
            job.XmlFailedCount = failCount;
            job.XmlNoXmlCount = skipCount;
        }
        else
        {
            job.PdfDownloadedCount = successCount;
            job.PdfFailedCount = failCount;
            job.PdfSkippedCount = skipCount;
        }

        job.ProgressCurrent = job.ProgressTotal;
        var distinctPdfFailed = pdfFailedExternalIds.Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in distinctPdfFailed)
        {
            if (!failureSummary.PdfFailedIds.Contains(id))
                failureSummary.PdfFailedIds.Add(id);
            if (pdfFailureById.TryGetValue(id, out var fi))
                failureSummary.PdfFailures.Add(fi);
        }
    }

    private static List<InvoiceFailureItem> BuildInvoiceFailureItemsFromDisplay(
        IReadOnlyList<InvoiceDisplayDto> invoices,
        IReadOnlyList<string> externalIdsOrdered,
        string defaultMessage)
    {
        var byId = invoices.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var list = new List<InvoiceFailureItem>();
        foreach (var id in externalIdsOrdered)
        {
            byId.TryGetValue(id, out var inv);
            list.Add(new InvoiceFailureItem
            {
                ExternalId = id,
                KyHieu = inv?.KyHieu,
                Khmshdon = inv?.Khmshdon ?? 0,
                SoHoaDon = inv?.SoHoaDon ?? 0,
                ErrorMessage = defaultMessage
            });
        }
        return list;
    }

    private static string BuildErrorMessage(Exception ex)
    {
        var messages = new List<string>();
        Exception? current = ex;
        while (current != null && messages.Count < 3)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.Message.Trim());
            current = current.InnerException;
        }
        if (messages.Count == 0)
            return "Lỗi không xác định khi chạy job nền.";
        return string.Join(" | ", messages.Distinct(StringComparer.Ordinal));
    }

    private static BulkPayloadDto? DeserializeBulkPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<BulkPayloadDto>(payloadJson);
        }
        catch
        {
            return null;
        }
    }

    private sealed class BulkPayloadDto
    {
        public List<string>? InvoiceIds { get; set; }
        public string? ExportXmlFolderPath { get; set; }
    }

    private sealed class ScoRecoveryPayloadDto
    {
        public int Version { get; set; } = 1;
        public bool ResyncFullDateRange { get; set; }
        public List<string>? ScoDetailExternalIds { get; set; }
        public int DetailRetryWaves { get; set; } = 3;
        public int WaveDelayMs { get; set; } = 8000;
    }

    private static ScoRecoveryPayloadDto? DeserializeScoRecoveryPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<ScoRecoveryPayloadDto>(payloadJson);
        }
        catch
        {
            return null;
        }
    }

    private BackgroundJobDto MapToDto(BackgroundJob job, string? companyName) =>
        new(
            job.Id,
            job.Type,
            job.Status,
            job.CompanyId,
            companyName,
            job.IsSold,
            job.FromDate,
            job.ToDate,
            job.IncludeDetail,
            job.DownloadXml,
            job.DownloadPdf,
            job.ExportKey,
            job.IsSummaryOnly,
            job.ProgressCurrent,
            job.ProgressTotal,
            job.Description,
            job.LastError,
            job.ResultPath,
            job.SyncCount,
            job.XmlTotal,
            job.XmlDownloadedCount,
            job.XmlFailedCount,
            job.XmlNoXmlCount,
            job.CreatedAt,
            job.StartedAt,
            job.FinishedAt,
            job.PdfDownloadedCount,
            job.PdfFailedCount,
            job.PdfSkippedCount,
            job.FailureSummaryJson
        );

    private async Task<BackgroundJobDto> MapToDtoAsync(BackgroundJob job, string? companyDisplayName, CancellationToken cancellationToken)
    {
        if (companyDisplayName == null)
        {
            var company = await _companyService.GetByIdAsync(job.CompanyId).ConfigureAwait(false);
            companyDisplayName = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? "";
        }
        return MapToDto(job, companyDisplayName);
    }
}

