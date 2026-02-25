using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.Services;

namespace SmartInvoice.Modules.Companies.ViewModels;

public partial class CompaniesListViewModel : ObservableObject
{
    private const int PageSize = 20;
    /// <summary>Chiều cao mỗi hàng card (px) — đủ chỗ nội dung + nút, các card cùng hàng ngang nhau.</summary>
    private const int CardRowHeight = 168;
    /// <summary>Chiều rộng mỗi card (px).</summary>
    private const int CardWidth = 480;

    private readonly ICompanyAppService _companyService;
    private readonly ICompanyEditDialogService _companyEditDialog;
    private readonly IBackgroundJobDialogService _backgroundJobDialog;
    private readonly IConfirmationService _confirmationService;
    private readonly IApplicationShutdownService _shutdownService;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private ObservableCollection<CompanyDto> _companies = [];

    [ObservableProperty]
    private ObservableCollection<CompanyDto> _filteredCompanies = [];

    /// <summary>Danh sách công ty cho trang hiện tại (tối đa 20 item).</summary>
    [ObservableProperty]
    private ObservableCollection<CompanyDto> _pagedCompanies = [];

    [ObservableProperty]
    private CompanyDto? _selectedCompany;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncInvoicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(PaginationSummary));
        OnPropertyChanged(nameof(HasCompanies));
    }

    /// <summary>Hiển thị tổng số và trang, ví dụ: "Tổng: 45 công ty • Trang 1/3".</summary>
    public string PaginationSummary => TotalCount <= 0
        ? "Không có công ty nào"
        : $"Tổng: {TotalCount} công ty • Trang {CurrentPage}/{Math.Max(1, TotalPages)}";

    /// <summary>Có ít nhất 1 công ty trong danh sách hay không (dùng để enable/disable nút chạy nền).</summary>
    public bool HasCompanies => TotalCount > 0;

    /// <summary>Tổng chiều cao vùng card (số hàng × CardRowHeight) để UniformGrid chia đều chiều cao mỗi hàng.</summary>
    public int TotalCardAreaHeight => Math.Max(CardRowHeight, (int)Math.Ceiling(PagedCompanies.Count / 2.0) * CardRowHeight);

    /// <summary>Tổng chiều rộng vùng card (2 cột × CardWidth + margin) để canh trái.</summary>
    public int TotalCardAreaWidth => 2 * CardWidth + 32;

    public CompaniesListViewModel(
        ICompanyAppService companyService,
        ICompanyEditDialogService companyEditDialog,
        IConfirmationService confirmationService,
        IApplicationShutdownService shutdownService,
        INavigationService navigationService,
        IBackgroundJobDialogService backgroundJobDialog)
    {
        _companyService = companyService;
        _companyEditDialog = companyEditDialog;
        _confirmationService = confirmationService;
        _shutdownService = shutdownService;
        _navigationService = navigationService;
        _backgroundJobDialog = backgroundJobDialog;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void Exit()
    {
        _shutdownService.Shutdown();
    }

    [RelayCommand]
    private async Task OpenBackgroundJobDialogAsync()
    {
        var defaultCompanyId = SelectedCompany?.Id;
        await _backgroundJobDialog.ShowCreateAsync(defaultCompanyId, null).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenJobManagement()
    {
        _backgroundJobDialog.ShowManagement();
    }

    private async Task RefreshCompaniesCoreAsync()
    {
        var list = await _companyService.GetAllAsync().ConfigureAwait(true);
        Companies = new ObservableCollection<CompanyDto>(list);
        ApplySearchFilter();
    }

    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC)
            .Replace('đ', 'd').Replace('Đ', 'D');
    }

    private static bool ContainsIgnoreDiacritics(string? source, string search)
    {
        if (string.IsNullOrEmpty(source)) return false;
        return RemoveDiacritics(source).Contains(RemoveDiacritics(search), StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySearchFilter()
    {
        var query = string.IsNullOrWhiteSpace(SearchText)
            ? Companies
            : Companies.Where(c =>
                ContainsIgnoreDiacritics(c.CompanyName, SearchText) ||
                ContainsIgnoreDiacritics(c.Username, SearchText) ||
                ContainsIgnoreDiacritics(c.TaxCode, SearchText));

        // Sắp xếp theo alphabet của Mã công ty, sau đó đến Tên công ty để hiển thị ổn định.
        var list = query
            .OrderBy(c => (c.CompanyCode ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => (c.CompanyName ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        FilteredCompanies = new ObservableCollection<CompanyDto>(list);
        TotalCount = list.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
        CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);
        ApplyPagination();
    }

    private void ApplyPagination()
    {
        var list = FilteredCompanies;
        var skip = (CurrentPage - 1) * PageSize;
        var page = list.Skip(skip).Take(PageSize).ToList();
        PagedCompanies = new ObservableCollection<CompanyDto>(page);
        OnPropertyChanged(nameof(PaginationSummary));
        OnPropertyChanged(nameof(TotalCardAreaHeight));
        OnPropertyChanged(nameof(TotalCardAreaWidth));
    }

    partial void OnSearchTextChanged(string value) => ApplySearchFilter();
    partial void OnCompaniesChanged(ObservableCollection<CompanyDto> value) => ApplySearchFilter();
    partial void OnCurrentPageChanged(int value) => ApplyPagination();

    [RelayCommand(CanExecute = nameof(CanGoPreviousPage))]
    private void PreviousPage()
    {
        if (CurrentPage > 1) CurrentPage--;
    }

    private bool CanGoPreviousPage() => CurrentPage > 1 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    private void NextPage()
    {
        if (CurrentPage < TotalPages) CurrentPage++;
    }

    private bool CanGoNextPage() => CurrentPage < TotalPages && TotalPages > 1 && !IsBusy;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            await RefreshCompaniesCoreAsync().ConfigureAwait(true);
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

    /// <summary>Mở popup Thêm công ty. Token được lấy ngay trong popup.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private async Task AddAsync()
    {
        var result = await _companyEditDialog.ShowAddAsync().ConfigureAwait(true);
        if (result.Success)
        {
            StatusMessage = "Đã thêm công ty.";
            await RefreshCompaniesCoreAsync().ConfigureAwait(true);
        }
        else if (result.Message != null)
        {
            StatusMessage = result.Message;
        }
    }

    private bool CanExecuteWhenNotBusy() => !IsBusy;

    /// <summary>Sửa công ty (popup). company từ CommandParameter khi gọi per-row.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private async Task EditAsync(CompanyDto? company)
    {
        var target = company ?? SelectedCompany;
        if (target == null)
        {
            StatusMessage = "Chọn một công ty để sửa.";
            return;
        }
        var result = await _companyEditDialog.ShowEditAsync(target).ConfigureAwait(true);
        if (result.Success)
        {
            StatusMessage = "Đã cập nhật công ty.";
            await RefreshCompaniesCoreAsync().ConfigureAwait(true);
        }
        else if (result.Message != null)
        {
            StatusMessage = result.Message;
        }
    }

    /// <summary>Xóa công ty. company từ CommandParameter khi gọi per-row.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private async Task DeleteAsync(CompanyDto? company)
    {
        var target = company ?? SelectedCompany;
        if (target == null)
        {
            StatusMessage = "Chọn một công ty để xóa.";
            return;
        }
        var confirmed = await _confirmationService.ConfirmAsync(
            "Xác nhận xóa",
            $"Bạn có chắc muốn xóa công ty \"{target.CompanyName}\" (MST: {target.Username})?"
        ).ConfigureAwait(true);
        if (!confirmed)
        {
            StatusMessage = "Đã hủy xóa.";
            return;
        }
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            await _companyService.DeleteAsync(target.Id).ConfigureAwait(true);
            StatusMessage = "Đã xóa công ty.";
            if (SelectedCompany?.Id == target.Id)
                SelectedCompany = null;
            await RefreshCompaniesCoreAsync().ConfigureAwait(true);
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

    /// <summary>Mở màn hình quản lý hóa đơn. Chỉ gọi API đăng nhập/captcha khi token không còn hợp lệ.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotBusy))]
    private async Task SyncInvoicesAsync(CompanyDto? company)
    {
        var target = company ?? SelectedCompany;
        if (target == null)
        {
            StatusMessage = "Chọn một công ty để đồng bộ hóa đơn.";
            return;
        }
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Đang kiểm tra token...";
        try
        {
            var tokenValid = await _companyService.ValidateTokenAsync(target.Id).ConfigureAwait(true);
            if (tokenValid)
            {
                StatusMessage = string.Empty;
                _navigationService.NavigateToInvoiceList(target.Id);
                return;
            }
            StatusMessage = "Token hết hạn, đang đăng nhập...";
            var result = await _companyService.LoginAndSyncProfileAsync(target.Id).ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = string.Empty;
                _navigationService.NavigateToInvoiceList(target.Id);
            }
            else
            {
                StatusMessage = "Đăng nhập thất bại: " + (result.Message ?? "Lỗi không xác định.");
            }
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
}
