using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;

namespace SmartInvoice.Modules.Companies.ViewModels;

public partial class BackgroundJobListViewModel : ObservableObject, IDisposable
{
    private readonly IBackgroundJobService _jobService;
    private readonly DispatcherTimer? _refreshTimer;
    private const int MaxJobs = 200;
    private const int PageSize = 10;
    private List<BackgroundJobDto> _allJobs = [];

    [ObservableProperty]
    private ObservableCollection<BackgroundJobDto> _jobs = [];

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedDetailCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedXmlCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedPdfCommand))]
    private BackgroundJobDto? _selectedJob;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedDetailCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedXmlCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedPdfCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public BackgroundJobListViewModel(IBackgroundJobService jobService)
    {
        _jobService = jobService;
        _ = LoadJobsAsync();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _refreshTimer.Tick += (_, _) => _ = LoadJobsAsync();
        _refreshTimer.Start();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadJobsAsync().ConfigureAwait(true);
    }

    private async Task LoadJobsAsync()
    {
        try
        {
            var list = await _jobService.GetRecentJobsAsync(MaxJobs).ConfigureAwait(true);
            _allJobs = list.ToList();
            TotalCount = _allJobs.Count;
            TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));
            if (CurrentPage > TotalPages)
                CurrentPage = TotalPages;
            ApplyPaging();
            var running = _allJobs.Where(j => j.Status == BackgroundJobStatus.Running).ToList();
            StatusMessage = running.Count > 0
                ? running.Count == 1
                    ? $"Đang chạy: {running[0].ProgressDisplayText}"
                    : $"Đang chạy {running.Count} job song song ({string.Join("; ", running.Take(3).Select(j => j.Description ?? j.Type.ToString()))}{(running.Count > 3 ? "…" : "")})."
                : TotalCount > 0
                    ? $"Trang {CurrentPage}/{TotalPages}, tổng {TotalCount} job."
                    : "Chưa có job nào.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
        }
    }

    private void ApplyPaging()
    {
        var skip = (CurrentPage - 1) * PageSize;
        var page = _allJobs.Skip(skip).Take(PageSize).ToList();
        var keepId = SelectedJob?.Id;
        Jobs = new ObservableCollection<BackgroundJobDto>(page);
        // Giữ selection theo Id sau mỗi lần nạp lại danh sách (timer 3s / Refresh); tránh nhảy về dòng đầu khi user đang xem job khác.
        if (Jobs.Count == 0)
            SelectedJob = null;
        else if (keepId.HasValue)
        {
            var same = Jobs.FirstOrDefault(j => j.Id == keepId.Value);
            SelectedJob = same ?? Jobs[0];
        }
        else
            SelectedJob = Jobs[0];
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPrevPage))]
    private void PrevPage()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        ApplyPaging();
        StatusMessage = $"Trang {CurrentPage}/{TotalPages}, tổng {TotalCount} job.";
    }

    private bool CanPrevPage() => CurrentPage > 1;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private void NextPage()
    {
        if (CurrentPage >= TotalPages) return;
        CurrentPage++;
        ApplyPaging();
        StatusMessage = $"Trang {CurrentPage}/{TotalPages}, tổng {TotalCount} job.";
    }

    private bool CanNextPage() => CurrentPage < TotalPages;

    partial void OnCurrentPageChanged(int value)
    {
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnTotalPagesChanged(int value)
    {
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (SelectedJob == null) return;
        IsBusy = true;
        try
        {
            await _jobService.RetryAsync(SelectedJob.Id).ConfigureAwait(true);
            StatusMessage = "Đã đưa job vào hàng đợi thử lại.";
            await LoadJobsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRetry() => SelectedJob != null
        && SelectedJob.Status == BackgroundJobStatus.Failed
        && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRetryFailedDetail))]
    private async Task RetryFailedDetailAsync()
    {
        if (SelectedJob == null) return;
        IsBusy = true;
        try
        {
            await _jobService.EnqueueRetryFailedInvoicesAsync(SelectedJob.Id, BackgroundJobRetryMode.Detail).ConfigureAwait(true);
            StatusMessage = "Đã tạo job chạy lại chi tiết cho các hóa đơn lỗi.";
            await LoadJobsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
            System.Windows.MessageBox.Show(ex.Message, "Không tạo được job chạy lại chi tiết", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRetryFailedDetail() => SelectedJob != null
        && !IsBusy
        && (SelectedJob.Status == BackgroundJobStatus.Completed || SelectedJob.Status == BackgroundJobStatus.Failed)
        && (SelectedJob.Type == BackgroundJobType.DownloadInvoices || SelectedJob.Type == BackgroundJobType.RefreshInvoiceDetails)
        && JobFailureSummary.Parse(SelectedJob.FailureSummaryJson).DetailFailedIds.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRetryFailedXml))]
    private async Task RetryFailedXmlAsync()
    {
        if (SelectedJob == null) return;
        IsBusy = true;
        try
        {
            await _jobService.EnqueueRetryFailedInvoicesAsync(SelectedJob.Id, BackgroundJobRetryMode.Xml).ConfigureAwait(true);
            StatusMessage = "Đã tạo job tải lại XML cho các hóa đơn lỗi.";
            await LoadJobsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
            System.Windows.MessageBox.Show(ex.Message, "Không tạo được job tải lại XML", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRetryFailedXml() => SelectedJob != null
        && !IsBusy
        && (SelectedJob.Status == BackgroundJobStatus.Completed || SelectedJob.Status == BackgroundJobStatus.Failed)
        && JobFailureSummary.Parse(SelectedJob.FailureSummaryJson).XmlFailedIds.Count > 0
        && (SelectedJob.Type == BackgroundJobType.DownloadXmlBulk
            || (SelectedJob.Type == BackgroundJobType.DownloadInvoices && SelectedJob.DownloadXml));

    [RelayCommand(CanExecute = nameof(CanRetryFailedPdf))]
    private async Task RetryFailedPdfAsync()
    {
        if (SelectedJob == null) return;
        IsBusy = true;
        try
        {
            await _jobService.EnqueueRetryFailedInvoicesAsync(SelectedJob.Id, BackgroundJobRetryMode.Pdf).ConfigureAwait(true);
            StatusMessage = "Đã tạo job tải lại PDF cho các hóa đơn lỗi.";
            await LoadJobsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
            System.Windows.MessageBox.Show(ex.Message, "Không tạo được job tải lại PDF", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRetryFailedPdf() => SelectedJob != null
        && !IsBusy
        && (SelectedJob.Status == BackgroundJobStatus.Completed || SelectedJob.Status == BackgroundJobStatus.Failed)
        && JobFailureSummary.Parse(SelectedJob.FailureSummaryJson).PdfFailedIds.Count > 0
        && (SelectedJob.Type == BackgroundJobType.DownloadPdfBulk
            || (SelectedJob.Type == BackgroundJobType.DownloadInvoices && SelectedJob.DownloadPdf));

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (SelectedJob == null) return;
        IsBusy = true;
        try
        {
            await _jobService.CancelAsync(SelectedJob.Id).ConfigureAwait(true);
            StatusMessage = SelectedJob.Status == BackgroundJobStatus.Running
                ? "Đã gửi tín hiệu hủy; job sẽ chuyển sang Đã hủy khi worker dừng."
                : "Đã hủy job.";
            await LoadJobsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCancel() => SelectedJob != null
        && (SelectedJob.Status == BackgroundJobStatus.Pending || SelectedJob.Status == BackgroundJobStatus.Running)
        && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedJob == null) return;
        IsBusy = true;
        try
        {
            await _jobService.DeleteAsync(SelectedJob.Id).ConfigureAwait(true);
            StatusMessage = "Đã xóa job.";
            await LoadJobsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDelete() => SelectedJob != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDeleteAll))]
    private async Task DeleteAllAsync()
    {
        if (TotalCount == 0) return;
        IsBusy = true;
        try
        {
            // Xóa tất cả job không ở trạng thái Running (Pending/Completed/Failed/Cancelled)
            var toDelete = _allJobs
                .Where(j => j.Status != BackgroundJobStatus.Running)
                .Select(j => j.Id)
                .ToList();
            foreach (var id in toDelete)
            {
                await _jobService.DeleteAsync(id).ConfigureAwait(true);
            }
            StatusMessage = $"Đã xóa {toDelete.Count} job.";
            await LoadJobsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDeleteAll() => !IsBusy && TotalCount > 0;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        var path = SelectedJob?.ResultPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            Process.Start("explorer.exe", folder);
        }
        catch { /* ignore */ }
    }

    private bool CanOpenFolder() => !string.IsNullOrWhiteSpace(SelectedJob?.ResultPath);

    partial void OnSelectedJobChanged(BackgroundJobDto? value)
    {
        RetryCommand.NotifyCanExecuteChanged();
        RetryFailedDetailCommand.NotifyCanExecuteChanged();
        RetryFailedXmlCommand.NotifyCanExecuteChanged();
        RetryFailedPdfCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _refreshTimer?.Stop();
}
