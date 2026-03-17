using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Services;

/// <summary>Service quản lý job nền. Worker nội bộ dùng Task.Run, mỗi lần lấy job dùng UnitOfWork mới (DbContext thread-safe).</summary>
public class BackgroundJobService : IBackgroundJobService
{
    private const int PdfRedownloadAfterDays = 3;

    private readonly IUnitOfWork _uow;
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly ICompanyAppService _companyService;
    private readonly IInvoiceSyncService _invoiceSyncService;
    private readonly IExcelExportService _excelExportService;
    private readonly IInvoicePdfService _invoicePdfService;
    private readonly IBackgroundJobCompletedNotifier? _notifier;
    private readonly ILogger _logger;

    private readonly object _lock = new();
    private bool _workerStarted;

    public BackgroundJobService(
        IUnitOfWork uow,
        IUnitOfWorkFactory uowFactory,
        ICompanyAppService companyService,
        IInvoiceSyncService invoiceSyncService,
        IExcelExportService excelExportService,
        IInvoicePdfService invoicePdfService,
        ILoggerFactory loggerFactory,
        IBackgroundJobCompletedNotifier? notifier = null)
    {
        _uow = uow;
        _uowFactory = uowFactory;
        _companyService = companyService;
        _invoiceSyncService = invoiceSyncService;
        _excelExportService = excelExportService;
        _invoicePdfService = invoicePdfService;
        _notifier = notifier;
        _logger = loggerFactory.CreateLogger<BackgroundJobService>();
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
        job.ResultPath = null;
        await _uow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        StartWorkerIfNeeded();
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _uow.BackgroundJobs.GetByIdAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null) return;
        if (job.Status != BackgroundJobStatus.Pending) return;
        job.Status = BackgroundJobStatus.Cancelled;
        await _uow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await _uow.BackgroundJobs.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    private void StartWorkerIfNeeded()
    {
        lock (_lock)
        {
            if (_workerStarted) return;
            _workerStarted = true;
            Task.Run(() => WorkerLoopAsync(CancellationToken.None));
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Mỗi lần lặp dùng UnitOfWork mới (DbContext mới) để tránh dùng chung context giữa UI thread và worker thread.
                var jobUow = _uowFactory.Create();
                try
                {
                    var pending = await jobUow.BackgroundJobs.GetPendingAsync(1, cancellationToken).ConfigureAwait(false);
                    if (pending.Count == 0)
                    {
                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var job = pending[0];
                    await RunJobAsync(job, jobUow, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (jobUow is IAsyncDisposable asyncDisposable)
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background job worker loop error.");
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting background job {Id} type {Type}", job.Id, job.Type);
        job.Status = BackgroundJobStatus.Running;
        job.StartedAt = DateTime.Now;
        job.ProgressCurrent = 0;
        if (job.Type != BackgroundJobType.DownloadXmlBulk && job.Type != BackgroundJobType.DownloadPdfBulk)
            job.ProgressTotal = job.Type == BackgroundJobType.ExportExcel ? 1 : 2;
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
                default:
                    throw new NotSupportedException($"Job type {job.Type} không được hỗ trợ.");
            }

            job.Status = BackgroundJobStatus.Completed;
            job.FinishedAt = DateTime.Now;
            job.LastError = null;
        }
        catch (Exception ex)
        {
            job.Status = BackgroundJobStatus.Failed;
            job.FinishedAt = DateTime.Now;
            job.LastError = ex.Message;
            _logger.LogError(ex, "Job {Id} failed.", job.Id);
        }
        finally
        {
            await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            _ = NotifyJobCompletedAsync(job, cancellationToken);
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
        // Bước 1: đồng bộ hóa đơn từ API
        var syncResult = await _invoiceSyncService.SyncInvoicesAsync(
            job.CompanyId,
            job.FromDate,
            job.ToDate,
            job.IncludeDetail,
            isSold: job.IsSold,
            cancellationToken).ConfigureAwait(false);

        job.SyncCount = syncResult.TotalSynced;
        job.ProgressCurrent = 1;
        job.ProgressTotal = 2; // 1 = đồng bộ xong, 2 = tải XML xong (hoặc không tải)
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

        // Bước 2: nếu yêu cầu, tải XML cho tất cả hóa đơn theo bộ lọc tương ứng
        if (job.DownloadXml)
        {
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
                "NgayLap",
                true);

            var (page, totalCount, _) = await _invoiceSyncService.GetInvoicesPagedAsync(
                job.CompanyId, filter, page: 1, pageSize: int.MaxValue, cancellationToken).ConfigureAwait(false);

            job.XmlTotal = totalCount;

            var company = await _companyService.GetByIdAsync(job.CompanyId, cancellationToken).ConfigureAwait(false);
            var companyCode = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? job.CompanyId.ToString("N")[..8];
            var companyRoot = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCode);
            Directory.CreateDirectory(companyRoot);

            var progress = new Progress<DownloadXmlProgress>(p =>
            {
                job.ProgressCurrent = 1 + p.Current;
                job.ProgressTotal = totalCount <= 0 ? 2 : 1 + totalCount;
                _ = jobUow.BackgroundJobs.UpdateAsync(job, CancellationToken.None);
            });

            var downloadResult = await _invoiceSyncService.DownloadInvoicesXmlAsync(job.CompanyId, page, companyRoot, progress, cancellationToken, zipOutputDirectory: companyRoot)
                .ConfigureAwait(false);

            job.XmlDownloadedCount = downloadResult.DownloadedCount;
            job.XmlFailedCount = downloadResult.FailedCount;
            job.XmlNoXmlCount = downloadResult.NoXmlCount;
            if (downloadResult.ZipPath != null)
                job.ResultPath = downloadResult.ZipPath;
            job.ProgressCurrent = job.ProgressTotal;
            await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            job.ProgressCurrent = job.ProgressTotal;
        }
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
        var progress = new Progress<DownloadXmlProgress>(p =>
        {
            job.ProgressCurrent = p.Current;
            _ = jobUow.BackgroundJobs.UpdateAsync(job, CancellationToken.None);
        });
        var result = await _invoiceSyncService.DownloadInvoicesXmlAsync(job.CompanyId, invoices, payload.ExportXmlFolderPath, progress, cancellationToken, zipOutputDirectory: companyRoot).ConfigureAwait(false);
        job.XmlDownloadedCount = result.DownloadedCount;
        job.XmlFailedCount = result.FailedCount;
        job.XmlNoXmlCount = result.NoXmlCount;
        job.ProgressCurrent = job.ProgressTotal;
        if (result.ZipPath != null)
            job.ResultPath = result.ZipPath;
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunDownloadPdfBulkJobAsync(BackgroundJob job, IUnitOfWork jobUow, CancellationToken cancellationToken)
    {
        var payload = DeserializeBulkPayload(job.PayloadJson);
        if (payload?.InvoiceIds == null || payload.InvoiceIds.Count == 0)
        {
            job.ProgressCurrent = job.ProgressTotal;
            await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
            return;
        }
        var company = await _companyService.GetByIdAsync(job.CompanyId, cancellationToken).ConfigureAwait(false);
        var companyCode = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? job.CompanyId.ToString("N")[..8];
        var companyRoot = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCode);
        var entities = await jobUow.Invoices.GetByCompanyAndExternalIdsAsync(job.CompanyId, payload.InvoiceIds, cancellationToken).ConfigureAwait(false);
        job.XmlTotal = entities.Count;
        int successCount = 0, failCount = 0, skipCount = 0;
        var savedPdfPaths = new List<string>();
        for (var i = 0; i < entities.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inv = entities[i];
            job.ProgressCurrent = i + 1;
            _ = jobUow.BackgroundJobs.UpdateAsync(job, CancellationToken.None);
            try
            {
                var baseName = $"{InvoiceFileStoragePathHelper.SanitizeFileName(inv.KyHieu ?? "")}-{inv.SoHoaDon}";
                var monthFolder = InvoiceFileStoragePathHelper.GetMonthYearPath(companyRoot, inv.NgayLap);
                Directory.CreateDirectory(monthFolder);
                var pdfPath = Path.Combine(monthFolder, baseName + ".pdf");

                // Nếu đã có PDF local và còn mới (< PdfRedownloadAfterDays) thì không gọi API lại, chỉ gom vào ZIP.
                if (File.Exists(pdfPath))
                {
                    var lastWrite = File.GetLastWriteTime(pdfPath);
                    if ((DateTime.Now - lastWrite).TotalDays < PdfRedownloadAfterDays)
                    {
                        savedPdfPaths.Add(pdfPath);
                        successCount++;
                        continue;
                    }
                }

                var result = await _invoicePdfService.GetPdfForInvoiceByExternalIdAsync(job.CompanyId, inv.ExternalId, cancellationToken).ConfigureAwait(false);
                if (result is InvoicePdfResult.Success success)
                {
                    await File.WriteAllBytesAsync(pdfPath, success.PdfBytes, cancellationToken).ConfigureAwait(false);
                    savedPdfPaths.Add(pdfPath);
                    successCount++;
                }
                else if (result is InvoicePdfResult.Failure f)
                {
                    var msg = f.ErrorMessage ?? "";
                    if (msg.Contains("thiếu", StringComparison.OrdinalIgnoreCase) || msg.Contains("chưa hỗ trợ", StringComparison.OrdinalIgnoreCase) || msg.Contains("không có", StringComparison.OrdinalIgnoreCase))
                        skipCount++;
                    else
                        failCount++;
                }
                else
                    failCount++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PDF bulk failed for invoice {ExternalId}", inv.ExternalId);
                failCount++;
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
            }
        }
        job.XmlDownloadedCount = successCount;
        job.XmlFailedCount = failCount;
        job.XmlNoXmlCount = skipCount;
        job.ProgressCurrent = job.ProgressTotal;
        await jobUow.BackgroundJobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
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
            job.FinishedAt
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

