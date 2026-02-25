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
    private BackgroundJobDto? _selectedJob;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
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
            var running = _allJobs.FirstOrDefault(j => j.Status == BackgroundJobStatus.Running);
            StatusMessage = running != null
                ? $"Đang chạy: {running.ProgressDisplayText}"
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
        Jobs = new ObservableCollection<BackgroundJobDto>(page);
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

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (SelectedJob == null) return;
        IsBusy = true;
        try
        {
            await _jobService.CancelAsync(SelectedJob.Id).ConfigureAwait(true);
            StatusMessage = "Đã hủy job.";
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
        && SelectedJob.Status == BackgroundJobStatus.Pending
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

    private bool CanDelete() => SelectedJob != null
        && SelectedJob.Status != BackgroundJobStatus.Running
        && !IsBusy;

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
        CancelCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _refreshTimer?.Stop();
}
