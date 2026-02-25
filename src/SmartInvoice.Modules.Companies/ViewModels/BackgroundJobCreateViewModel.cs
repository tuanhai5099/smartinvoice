using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.Services;

namespace SmartInvoice.Modules.Companies.ViewModels;

public partial class BackgroundJobCreateViewModel : ObservableObject
{
    private readonly IBackgroundJobService _backgroundJobService;
    private readonly ICompanyAppService _companyService;
    private readonly IBackgroundJobDialogService _dialogService;
    private readonly Action _closeCallback;
    private readonly Guid? _defaultCompanyId;
    private readonly bool? _defaultIsSold;

    [ObservableProperty]
    private ObservableCollection<CompanyDto> _companies = [];

    [ObservableProperty]
    private Guid? _selectedCompanyId;

    [ObservableProperty]
    private bool _isSold = true;

    [ObservableProperty]
    private DateTime _fromDate = DateTime.Today.AddMonths(-1);

    [ObservableProperty]
    private DateTime _toDate = DateTime.Today;

    /// <summary>Ngày sớm nhất được chọn (từ tháng 8/2022).</summary>
    public static DateTime MinJobDate => new(2022, 8, 1);

    /// <summary>Ngày muộn nhất được chọn (trễ nhất là hôm nay).</summary>
    public DateTime MaxJobDate => DateTime.Today;

    [ObservableProperty]
    private bool _includeDetail;

    [ObservableProperty]
    private bool _downloadXml = false;

    [ObservableProperty]
    private bool _downloadPdf;

    [ObservableProperty]
    private bool _exportExcel = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public BackgroundJobCreateViewModel(
        IBackgroundJobService backgroundJobService,
        ICompanyAppService companyService,
        IBackgroundJobDialogService dialogService,
        Action closeCallback,
        Guid? defaultCompanyId,
        bool? defaultIsSold)
    {
        _backgroundJobService = backgroundJobService;
        _companyService = companyService;
        _dialogService = dialogService;
        _closeCallback = closeCallback;
        _defaultCompanyId = defaultCompanyId;
        _defaultIsSold = defaultIsSold;
        ClampJobDates();
        _ = LoadCompaniesAsync();
    }

    private async Task LoadCompaniesAsync()
    {
        try
        {
            var list = await _companyService.GetAllAsync().ConfigureAwait(true);
            Companies = new ObservableCollection<CompanyDto>(list);

            if (_defaultCompanyId.HasValue && list.Any(c => c.Id == _defaultCompanyId.Value))
                SelectedCompanyId = _defaultCompanyId;
            else
                SelectedCompanyId = list.FirstOrDefault()?.Id;

            if (_defaultIsSold.HasValue)
                IsSold = _defaultIsSold.Value;
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi tải danh sách công ty: " + ex.Message;
        }
    }

    partial void OnFromDateChanged(DateTime value) => ClampJobDates();
    partial void OnToDateChanged(DateTime value) => ClampJobDates();

    /// <summary>Giới hạn: từ ngày &gt;= 01/08/2022, đến ngày &lt;= hôm nay, từ ngày &lt;= đến ngày.</summary>
    private void ClampJobDates()
    {
        var min = MinJobDate;
        var max = MaxJobDate;
        if (FromDate < min) FromDate = min;
        if (FromDate > max) FromDate = max;
        if (ToDate < min) ToDate = min;
        if (ToDate > max) ToDate = max;
        if (FromDate > ToDate) ToDate = FromDate;
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateJobAsync()
    {
        if (SelectedCompanyId == null)
        {
            StatusMessage = "Chọn công ty.";
            return;
        }
        ClampJobDates();
        if (FromDate > ToDate)
        {
            StatusMessage = "Từ ngày phải nhỏ hơn hoặc bằng Đến ngày.";
            return;
        }
        if (FromDate < MinJobDate || ToDate > MaxJobDate)
        {
            StatusMessage = $"Khoảng ngày phải từ 01/08/2022 đến {MaxJobDate:dd/MM/yyyy} (không chọn tương lai).";
            return;
        }
        IsBusy = true;
        StatusMessage = "Đang tạo job nền...";
        try
        {
            var dto = new BackgroundJobCreateDto(
                SelectedCompanyId.Value,
                IsSold,
                FromDate.Date,
                ToDate.Date,
                IncludeDetail,
                DownloadXml,
                DownloadPdf,
                ExportExcel);
            await _backgroundJobService.EnqueueDownloadInvoicesAsync(dto).ConfigureAwait(true);

            if (ExportExcel)
            {
                // Nếu không đồng bộ chi tiết: xuất Excel Tổng hợp.
                // Nếu có đồng bộ chi tiết: xuất Excel Chi tiết.
                var exportOptions = new ExportExcelCreateDto(
                    SelectedCompanyId.Value,
                    IsSold,
                    FromDate.Date,
                    ToDate.Date,
                    IncludeDetail ? "chitiet" : "tonghop",
                    IsSummaryOnly: !IncludeDetail);
                await _backgroundJobService.EnqueueExportExcelAsync(exportOptions).ConfigureAwait(true);
            }

            StatusMessage = "Đã thêm job tải nền (và xuất Excel nếu đã chọn).";
            _closeCallback();
            // Hiện thông báo thành công và mở cửa sổ quản lý job sau khi popup đóng
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                _dialogService.ShowToast("Thêm thành công", "Job đã được thêm vào hàng đợi. Cửa sổ quản lý job đang mở.");
                _dialogService.ShowManagement();
            });
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
 
    private bool CanCreate() => !IsBusy && SelectedCompanyId != null;

    /// <summary>Tên đầy đủ công ty đang chọn (hiển thị dưới ComboBox).</summary>
    public string? SelectedCompanyName => SelectedCompanyId == null
        ? null
        : Companies.FirstOrDefault(c => c.Id == SelectedCompanyId.Value)?.CompanyName;

    partial void OnSelectedCompanyIdChanged(Guid? value) => OnPropertyChanged(nameof(SelectedCompanyName));

    [RelayCommand]
    private void Cancel()
    {
        _closeCallback();
    }

    [RelayCommand]
    private void SetDatePreset(string preset)
    {
        var now = DateTime.Now;
        switch (preset)
        {
            case "thisMonth":
                FromDate = new DateTime(now.Year, now.Month, 1);
                ToDate = FromDate.AddMonths(1).AddDays(-1);
                break;
            case "lastMonth":
                var pm = now.AddMonths(-1);
                FromDate = new DateTime(pm.Year, pm.Month, 1);
                ToDate = FromDate.AddMonths(1).AddDays(-1);
                break;
            case "thisYear":
                FromDate = new DateTime(now.Year, 1, 1);
                ToDate = new DateTime(now.Year, 12, 31);
                break;
            case "lastYear":
                FromDate = new DateTime(now.Year - 1, 1, 1);
                ToDate = new DateTime(now.Year - 1, 12, 31);
                break;
            case "first6Months":
                FromDate = new DateTime(now.Year, 1, 1);
                ToDate = new DateTime(now.Year, 6, 30);
                break;
            case "last6Months":
                var yearLast6 = now.Month <= 6 ? now.Year - 1 : now.Year;
                FromDate = new DateTime(yearLast6, 7, 1);
                ToDate = new DateTime(yearLast6, 12, 31);
                break;
            default:
                if (preset.StartsWith("q") && int.TryParse(preset[1..], out var qi) && qi >= 1 && qi <= 4)
                {
                    var currentQuarter = (now.Month - 1) / 3 + 1;
                    var yearQ = qi > currentQuarter ? now.Year - 1 : now.Year;
                    FromDate = new DateTime(yearQ, (qi - 1) * 3 + 1, 1);
                    ToDate = FromDate.AddMonths(3).AddDays(-1);
                }
                else if (preset.StartsWith("m") && int.TryParse(preset[1..], out var mi) && mi >= 1 && mi <= 12)
                {
                    var yearM = mi > now.Month ? now.Year - 1 : now.Year;
                    FromDate = new DateTime(yearM, mi, 1);
                    ToDate = FromDate.AddMonths(1).AddDays(-1);
                }
                break;
        }
    }
}

