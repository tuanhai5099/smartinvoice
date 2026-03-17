using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.Services;
using SmartInvoice.Core.Domain;

namespace SmartInvoice.Modules.Companies.ViewModels;

public enum SyncFilterKind
{
    Month = 0,
    Quarter = 1,
    Year = 2,
    DateRange = 3
}

/// <summary>Hướng hóa đơn: Mua vào / Bán ra.</summary>
public enum FilterDirectionKind
{
    MuaVao = 0,
    BanRa = 1
}

/// <summary>Loại hóa đơn: Tổng số, Có mã, Không mã, Máy tính tiền, Ngoại tệ.</summary>
public enum FilterLoaiHoaDonKind
{
    TatCa = 0,
    CoMa = 1,
    KhongMa = 2,
    MayTinhTien = 3,
    NgoaiTe = 4
}

/// <summary>Trạng thái XML: chưa tải, đã tải (✓), không có XML (✗).</summary>
public enum XmlDownloadState
{
    None,
    Downloaded,
    NoXml
}

/// <summary>Một dòng kết quả trong popup tải XML.</summary>
public sealed class DownloadItemResultViewModel : ObservableObject
{
    public string SoHoaDonDisplay { get; }
    public bool Success { get; }
    public bool NoXml { get; }
    /// <summary>True khi tải PDF bỏ qua do thiếu cấu hình / link / mã tra cứu.</summary>
    public bool IsSkipped { get; }
    public string? Message { get; }
    public string StatusText => Success
        ? "Thành công"
        : (IsSkipped
            ? "Bỏ qua"
            : (NoXml
                ? "Không có XML"
                : (Message != null && Message.StartsWith("Chờ", StringComparison.OrdinalIgnoreCase)
                    ? "Đang chờ"
                    : (Message != null && Message.StartsWith("Đang tải", StringComparison.OrdinalIgnoreCase)
                        ? "Đang tải"
                        : "Thất bại"))));
    /// <summary>
    /// Icon hiển thị:
    /// • chờ tải, ⏳ đang tải, ✓ thành công, ⊘ bỏ qua / không có XML, ✗ thất bại.
    /// </summary>
    public string StatusIcon
    {
        get
        {
            if (!string.IsNullOrEmpty(Message))
            {
                if (Message.StartsWith("Đang tải", StringComparison.OrdinalIgnoreCase))
                    return "⏳";
                if (Message.StartsWith("Chờ", StringComparison.OrdinalIgnoreCase))
                    return "•";
            }

            if (Success) return "✓";
            if (IsSkipped || NoXml) return "⊘";
            return "✗";
        }
    }

    public DownloadItemResultViewModel(string soHoaDonDisplay, bool success, bool noXml, string? message, bool isSkipped = false)
    {
        SoHoaDonDisplay = soHoaDonDisplay;
        Success = success;
        NoXml = noXml;
        IsSkipped = isSkipped;
        Message = message ?? "";
    }
}

public partial class InvoiceListViewModel : ObservableObject
{
    private readonly ICompanyAppService _companyService;
    private readonly IInvoiceSyncService _invoiceSyncService;
    private readonly INavigationService _navigationService;
    private readonly IBackgroundJobDialogService _backgroundJobDialog;
    private readonly IInvoiceViewerService _invoiceViewerService;
    private readonly IInvoiceDetailViewService _invoiceDetailViewService;
    private readonly IInvoicePdfService _invoicePdfService;
    private readonly IBackgroundJobService _backgroundJobService;
    private readonly ILogger<InvoiceListViewModel> _logger;
    private bool _suppressAutoFilter;

    /// <summary>Danh sách hóa đơn thất bại trong lần tải PDF gần nhất (để hỗ trợ nút Tải lại).</summary>
    private readonly List<InvoiceDisplayDto> _lastPdfFailedInvoices = new();

    private void OpenViewerOnUiThread(string path, InvoiceDisplayDto inv, Func<Task<(string? printPath, string? error)>>? getPrintPathAsync)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            _invoiceViewerService.OpenHtmlViewer(path, CompanyCode, CompanyName, inv, getPrintPathAsync);
            return;
        }

        if (dispatcher.CheckAccess())
        {
            _invoiceViewerService.OpenHtmlViewer(path, CompanyCode, CompanyName, inv, getPrintPathAsync);
        }
        else
        {
            dispatcher.Invoke(() =>
                _invoiceViewerService.OpenHtmlViewer(path, CompanyCode, CompanyName, inv, getPrintPathAsync));
        }
    }

    [ObservableProperty]
    private Guid? _companyId;

    [ObservableProperty]
    private string _companyName = string.Empty;

    /// <summary>Mã công ty / tên gọi nhỏ (dùng cho tên thư mục lưu PDF, ZIP XML).</summary>
    [ObservableProperty]
    private string _companyCode = string.Empty;

    [ObservableProperty]
    private SyncFilterKind _syncFilterKind = SyncFilterKind.Month;

    [ObservableProperty]
    private DateTime _filterFromDate;

    [ObservableProperty]
    private DateTime _filterToDate;

    /// <summary>Ngày sớm nhất được chọn (từ tháng 8/2022).</summary>
    public static DateTime MinFilterDate => new(2022, 8, 1);

    /// <summary>Ngày muộn nhất được chọn (trễ nhất là hôm nay, không chọn tương lai).</summary>
    public DateTime MaxFilterDate => DateTime.Today;

    [ObservableProperty]
    private bool _includeDetail;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRowActionBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private short _filterTrangThai = -1;

    [ObservableProperty]
    private FilterDirectionKind _filterDirection = FilterDirectionKind.MuaVao;

    /// <summary>Header cột thông tin đối tác: "Thông tin người bán" (Mua vào) hoặc "Thông tin người mua" (Bán ra).</summary>
    public string CounterpartyColumnHeader => FilterDirection == FilterDirectionKind.MuaVao ? "Thông tin người bán" : "Thông tin người mua";

    [ObservableProperty]
    private FilterLoaiHoaDonKind _filterLoaiHoaDon = FilterLoaiHoaDonKind.TatCa;

    [ObservableProperty]
    private bool _isAdvancedFilterOpen;

    [ObservableProperty]
    private string _filterKyHieu = string.Empty;

    [ObservableProperty]
    private string _filterSoHoaDon = string.Empty;

    [ObservableProperty]
    private string _filterKyHieuMauSo = string.Empty;

    [ObservableProperty]
    private string _filterTenNguoiBan = string.Empty;

    [ObservableProperty]
    private string _filterMstNguoiBan = string.Empty;

    [ObservableProperty]
    private string _filterMstLoaiTru = string.Empty;

    [ObservableProperty]
    private string _filterLoaiTruBenBan = string.Empty;

    [ObservableProperty]
    private string _filterKetQuaKiemTra = string.Empty;

    [ObservableProperty]
    private string _filterLoaiHoaDonMuaVao = string.Empty;

    [ObservableProperty]
    private string _filterTrangThaiTaiFile = string.Empty;

    [ObservableProperty]
    private string _filterLinkTraCuu = string.Empty;

    [ObservableProperty]
    private string _filterMaTraCuu = string.Empty;

    [ObservableProperty]
    private string _filterGhiChu = string.Empty;

    [ObservableProperty]
    private string _filterTags = string.Empty;

    /// <summary>True khi có ít nhất một điều kiện lọc đang áp dụng (để hiện nút Bỏ lọc).</summary>
    [ObservableProperty]
    private bool _hasActiveFilter;

    [ObservableProperty]
    private int _countCoMa;

    [ObservableProperty]
    private int _countKhongMa;

    [ObservableProperty]
    private int _countMayTinhTien;

    /// <summary>Số hóa đơn đã tải (trên danh sách hiện tại) có XML.</summary>
    [ObservableProperty]
    private int _countCoXml;

    /// <summary>Số hóa đơn đã tải chưa có XML (chưa tải hoặc không có XML).</summary>
    [ObservableProperty]
    private int _countChuaXml;

    /// <summary>Số hóa đơn đã tải có PDF.</summary>
    [ObservableProperty]
    private int _countCoPdf;

    /// <summary>Số hóa đơn đã tải chưa có PDF.</summary>
    [ObservableProperty]
    private int _countChuaPdf;

    /// <summary>Số hóa đơn đã tải là ngoại tệ (Dvtte != VND).</summary>
    [ObservableProperty]
    private int _countNgoaiTe;

    [ObservableProperty]
    private string _filterDateLabel = string.Empty;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private decimal _totalChuaThue;

    [ObservableProperty]
    private decimal _totalTienThue;

    [ObservableProperty]
    private decimal _totalThanhTien;

    [ObservableProperty]
    private string _lastSyncedDisplay = string.Empty;

    [ObservableProperty]
    private bool _isDownloadingXml;

    [ObservableProperty]
    private string _downloadXmlProgressText = string.Empty;

    [ObservableProperty]
    private int _downloadTotal;

    [ObservableProperty]
    private int _downloadSucceeded;

    [ObservableProperty]
    private int _downloadFailed;

    [ObservableProperty]
    private int _downloadNoXml;

    [ObservableProperty]
    private int _downloadSkipped;

    /// <summary>Hóa đơn đang tải (1-based). 0 = chưa bắt đầu hoặc đã xong. Dùng cho progress bar khi tải PDF hàng loạt.</summary>
    [ObservableProperty]
    private int _downloadCurrentIndex;

    /// <summary>True khi popup đang hiển thị kết quả tải PDF hàng loạt (để hiện tiêu đề và tổng kết đúng).</summary>
    [ObservableProperty]
    private bool _isDownloadPdfResults;

    /// <summary>True nếu lần tải PDF gần nhất có hóa đơn thất bại (không tính Bỏ qua) và có thể bấm Tải lại.</summary>
    [ObservableProperty]
    private bool _hasFailedPdfInLastBatch;

    /// <summary>Chỉ hiển thị hóa đơn ngoại tệ (Dvtte != VND) trên lưới.</summary>
    [ObservableProperty]
    private bool _filterForeignCurrencyOnly;

    /// <summary>Cột đang sắp xếp (SortMemberPath: NgayLap, KyHieu, SoHoaDon, CounterpartyName, TrangThaiDisplay, Tgtcthue, Tgtthue, TongTien). Null = mặc định NgayLap giảm dần.</summary>
    [ObservableProperty]
    private string? _sortBy = "NgayLap";

    /// <summary>True = giảm dần (Z→A, mới→cũ), False = tăng dần (A→Z, cũ→mới).</summary>
    [ObservableProperty]
    private bool _sortDescending = true;

    private const int PageSize = 50;

    /// <summary>Thư mục gốc lưu XML đã tải cho công ty hiện tại (Documents\SmartInvoice\{CompanyCodeOrName}\XML).</summary>
    public string ExportXmlFolderPath
    {
        get
        {
            var companyCodeOrName = !string.IsNullOrWhiteSpace(CompanyCode)
                ? CompanyCode
                : (!string.IsNullOrWhiteSpace(CompanyName) ? CompanyName : "CongTy");
            return GetCompanyXmlRootPathForUi(companyCodeOrName);
        }
    }

    private static string GetCompanyXmlRootPathForUi(string? companyCodeOrNameOrId)
    {
        static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrEmpty(name) ? "CongTy" : name;
        }

        var name = companyCodeOrNameOrId?.Trim();
        if (string.IsNullOrEmpty(name))
            name = "CongTy";
        name = SanitizeFileName(name);
        if (name.Length > 64)
            name = name[..64];

        var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartInvoice");
        return Path.Combine(appRoot, name, "XML");
    }

    /// <summary>Trạng thái XML theo key hóa đơn (baseName) để hiển thị cột XML.</summary>
    private readonly Dictionary<string, XmlDownloadState> _xmlStateByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tăng sau khi cập nhật trạng thái XML để UI refresh cột XML.</summary>
    [ObservableProperty]
    private int _xmlStateRefreshTrigger;

    /// <summary>Trạng thái đã có PDF theo key hóa đơn (baseName) để hiển thị cột PDF.</summary>
    private readonly Dictionary<string, bool> _pdfStateByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tăng sau khi cập nhật trạng thái PDF để UI refresh cột PDF.</summary>
    [ObservableProperty]
    private int _pdfStateRefreshTrigger;

    /// <summary>Popup tải XML: danh sách kết quả từng hóa đơn.</summary>
    public ObservableCollection<DownloadItemResultViewModel> DownloadResults { get; } = [];

    [ObservableProperty]
    private bool _isDownloadXmlPopupOpen;

    /// <summary>Danh sách hóa đơn liên quan cho HĐ đang chọn (popup "Xem HĐ liên quan").</summary>
    public ObservableCollection<InvoiceRelativeItemDto> RelatedInvoices { get; } = [];

    /// <summary>Hóa đơn hiện tại khi mở popup "Hóa đơn liên quan" (giữ lại vì CloseActionMenu() sẽ set ActionMenuInvoice = null).</summary>
    [ObservableProperty]
    private InvoiceDisplayDto? _relatedInvoicesCurrentInvoice;

    [ObservableProperty]
    private bool _isRelatedInvoicesPopupOpen;

    [ObservableProperty]
    private string? _lastXmlZipPath;

    /// <summary>Hóa đơn đang chọn cho menu thao tác dòng (Đồng bộ lại / Xem hóa đơn / Tải XML).</summary>
    [ObservableProperty]
    private InvoiceDisplayDto? _actionMenuInvoice;

    [ObservableProperty]
    private bool _isActionMenuOpen;

    /// <summary>Menu row hiện có nút "Xem HĐ liên quan" hay không (chỉ cho HĐ thay thế / điều chỉnh...).</summary>
    [ObservableProperty]
    private bool _isActionMenuRelatedVisible;

    /// <summary>Popup gợi ý tra cứu PDF cho hóa đơn đang chọn.</summary>
    [ObservableProperty]
    private bool _isLookupPopupOpen;

    [ObservableProperty]
    private string? _lookupProviderName;

    [ObservableProperty]
    private string? _lookupProviderKey;

    [ObservableProperty]
    private string? _lookupSearchUrl;

    [ObservableProperty]
    private string? _lookupSecretCode;

    [ObservableProperty]
    private string? _lookupSellerTaxCode;

    /// <summary>True nếu gợi ý thuộc HTInvoice (để hiện nút Tự lấy PDF).</summary>
    [ObservableProperty]
    private bool _isHtInvoiceLookup;

    /// <summary>Còn dữ liệu để load thêm (infinite scroll).</summary>
    public bool HasMore => TotalCount > 0 && Invoices.Count < TotalCount;

    [ObservableProperty]
    private string _paginationSummary = string.Empty;

    [ObservableProperty]
    private ObservableCollection<InvoiceDisplayDto> _invoices = [];

    private ICollectionView? _invoicesView;

    public ICollectionView? InvoicesView
    {
        get => _invoicesView;
        private set => SetProperty(ref _invoicesView, value);
    }

    public InvoiceListViewModel(
        ICompanyAppService companyService,
        IInvoiceSyncService invoiceSyncService,
        INavigationService navigationService,
        IBackgroundJobDialogService backgroundJobDialog,
        IInvoiceViewerService invoiceViewerService,
        IInvoiceDetailViewService invoiceDetailViewService,
        IInvoicePdfService invoicePdfService,
        IBackgroundJobService backgroundJobService,
        ILoggerFactory loggerFactory)
    {
        _companyService = companyService;
        _invoiceSyncService = invoiceSyncService;
        _navigationService = navigationService;
        _backgroundJobDialog = backgroundJobDialog;
        _invoiceViewerService = invoiceViewerService;
        _invoiceDetailViewService = invoiceDetailViewService;
        _invoicePdfService = invoicePdfService;
        _backgroundJobService = backgroundJobService;
        _logger = loggerFactory.CreateLogger<InvoiceListViewModel>();
        SetDefaultFilterDates();

        // Lắng nghe job nền hoàn thành (đặc biệt là job tải XML hàng loạt) để tự refresh trạng thái cột XML.
        BackgroundJobToastNotificationService.JobCompleted += OnBackgroundJobCompleted;
    }

    /// <summary>Gọi khi một job nền hoàn thành. Nếu là job tải XML hàng loạt cho đúng công ty hiện tại thì refresh trạng thái XML trên lưới.</summary>
    private void OnBackgroundJobCompleted(BackgroundJobDto job)
    {
        if (CompanyId == null) return;
        if (job.CompanyId != CompanyId.Value) return;
        if (job.Type != BackgroundJobType.DownloadXmlBulk) return;
        if (job.Status != BackgroundJobStatus.Completed && job.Status != BackgroundJobStatus.Failed) return;

        // XML đã được lưu xuống thư mục ExportXml; tăng trigger để UI hỏi lại trạng thái trên đĩa và cập nhật icon.
        XmlStateRefreshTrigger++;
    }

    [RelayCommand]
    private void NavigateBackToCompanies()
    {
        _navigationService.NavigateToCompanies();
    }

    public void SetCompanyId(Guid companyId)
    {
        CompanyId = companyId;
        SetDefaultFilterDates();
        _ = LoadCompanyAndInvoicesAsync();
    }

    private void SetDefaultFilterDates()
    {
        var now = DateTime.Now;
        var prevMonth = now.AddMonths(-1);
        FilterFromDate = new DateTime(prevMonth.Year, prevMonth.Month, 1);
        FilterToDate = FilterFromDate.AddMonths(1).AddDays(-1);
        UpdateFilterDateLabel();
    }

    partial void OnSyncFilterKindChanged(SyncFilterKind value)
    {
        if (_suppressAutoFilter) return;
        ApplyFilterKindToDates();
        UpdateFilterDateLabel();
        _ = ApplyFilterAsync();
    }

    // Ô tìm kiếm và lọc nâng cao: chỉ áp dụng khi nhấn Enter hoặc nút "Áp dụng".
    partial void OnFilterTrangThaiChanged(short value)
    {
        if (_suppressAutoFilter) return;
        _ = ApplyFilterAsync();
    }
    partial void OnFilterDirectionChanged(FilterDirectionKind value)
    {
        if (_suppressAutoFilter)
        {
            OnPropertyChanged(nameof(CounterpartyColumnHeader));
            return;
        }
        OnPropertyChanged(nameof(CounterpartyColumnHeader));
        _ = ApplyFilterAsync();
    }
    partial void OnFilterLoaiHoaDonChanged(FilterLoaiHoaDonKind value)
    {
        if (_suppressAutoFilter) return;
        _ = ApplyFilterAsync();
    }
    partial void OnFilterFromDateChanged(DateTime value)
    {
        if (_suppressAutoFilter)
        {
            UpdateFilterDateLabel();
            return;
        }
        _suppressAutoFilter = true;
        ClampFilterDates();
        _suppressAutoFilter = false;
        UpdateFilterDateLabel();
        _ = ApplyFilterAsync();
    }
    partial void OnFilterToDateChanged(DateTime value)
    {
        if (_suppressAutoFilter)
        {
            UpdateFilterDateLabel();
            return;
        }
        _suppressAutoFilter = true;
        ClampFilterDates();
        _suppressAutoFilter = false;
        UpdateFilterDateLabel();
        _ = ApplyFilterAsync();
    }

    /// <summary>Giới hạn: từ ngày &gt;= 01/08/2022, đến ngày &lt;= hôm nay, từ ngày &lt;= đến ngày.</summary>
    private void ClampFilterDates()
    {
        var min = MinFilterDate;
        var max = MaxFilterDate;
        if (FilterFromDate < min) FilterFromDate = min;
        if (FilterFromDate > max) FilterFromDate = max;
        if (FilterToDate < min) FilterToDate = min;
        if (FilterToDate > max) FilterToDate = max;
        if (FilterFromDate > FilterToDate) FilterToDate = FilterFromDate;
    }

    private void UpdateFilterDateLabel()
    {
        FilterDateLabel = $"Thời gian: {FilterFromDate:dd/MM/yyyy} - {FilterToDate:dd/MM/yyyy}";
    }

    private void ApplyFilterKindToDates()
    {
        var now = DateTime.Now;
        switch (SyncFilterKind)
        {
            case SyncFilterKind.Month:
                var prevMonth = now.AddMonths(-1);
                FilterFromDate = new DateTime(prevMonth.Year, prevMonth.Month, 1);
                FilterToDate = FilterFromDate.AddMonths(1).AddDays(-1);
                break;
            case SyncFilterKind.Quarter:
                var q = (now.Month - 1) / 3 + 1;
                var prevQ = q == 1 ? 4 : q - 1;
                var prevYear = prevQ == 4 ? now.Year - 1 : now.Year;
                FilterFromDate = new DateTime(prevYear, (prevQ - 1) * 3 + 1, 1);
                FilterToDate = FilterFromDate.AddMonths(3).AddDays(-1);
                break;
            case SyncFilterKind.Year:
                FilterFromDate = new DateTime(now.Year - 1, 1, 1);
                FilterToDate = new DateTime(now.Year - 1, 12, 31);
                break;
            case SyncFilterKind.DateRange:
                break;
        }
        ClampFilterDates();
    }

    [RelayCommand]
    private void SetDatePreset(string preset)
    {
        var now = DateTime.Now;
        switch (preset)
        {
            case "today":
                FilterFromDate = now.Date;
                FilterToDate = now.Date;
                break;
            case "thisWeek":
                var dow = (int)now.DayOfWeek;
                var monday = now.AddDays(-(dow == 0 ? 6 : dow - 1));
                FilterFromDate = monday.Date;
                FilterToDate = monday.AddDays(6).Date;
                break;
            case "thisMonth":
                FilterFromDate = new DateTime(now.Year, now.Month, 1);
                FilterToDate = FilterFromDate.AddMonths(1).AddDays(-1);
                break;
            case "thisQuarter":
                var cq = (now.Month - 1) / 3;
                FilterFromDate = new DateTime(now.Year, cq * 3 + 1, 1);
                FilterToDate = FilterFromDate.AddMonths(3).AddDays(-1);
                break;
            case "lastWeek":
                var ldow = (int)now.DayOfWeek;
                var lm = now.AddDays(-(ldow == 0 ? 6 : ldow - 1)).AddDays(-7);
                FilterFromDate = lm.Date;
                FilterToDate = lm.AddDays(6).Date;
                break;
            case "lastMonth":
                var pm = now.AddMonths(-1);
                FilterFromDate = new DateTime(pm.Year, pm.Month, 1);
                FilterToDate = FilterFromDate.AddMonths(1).AddDays(-1);
                break;
            case "lastQuarter":
                var lq = (now.Month - 1) / 3 + 1;
                var pq = lq == 1 ? 4 : lq - 1;
                var py = pq == 4 ? now.Year - 1 : now.Year;
                FilterFromDate = new DateTime(py, (pq - 1) * 3 + 1, 1);
                FilterToDate = FilterFromDate.AddMonths(3).AddDays(-1);
                break;
            case "lastYear":
                FilterFromDate = new DateTime(now.Year - 1, 1, 1);
                FilterToDate = new DateTime(now.Year - 1, 12, 31);
                break;
            case "thisYear":
                FilterFromDate = new DateTime(now.Year, 1, 1);
                FilterToDate = new DateTime(now.Year, 12, 31);
                break;
            case "first6Months":
                // 6 tháng đầu năm: nếu đang ở nửa sau năm thì dùng năm nay (đã qua), nếu đang ở nửa đầu thì năm nay
                FilterFromDate = new DateTime(now.Year, 1, 1);
                FilterToDate = new DateTime(now.Year, 6, 30);
                break;
            case "last6Months":
                // 6 tháng cuối năm: nếu đang ở nửa đầu năm thì dùng năm trước (đã qua), nếu nửa sau thì năm nay
                var yearLast6 = now.Month <= 6 ? now.Year - 1 : now.Year;
                FilterFromDate = new DateTime(yearLast6, 7, 1);
                FilterToDate = new DateTime(yearLast6, 12, 31);
                break;
            default:
                if (preset.StartsWith("q") && int.TryParse(preset[1..], out var qi) && qi >= 1 && qi <= 4)
                {
                    var currentQuarter = (now.Month - 1) / 3 + 1;
                    var yearQ = qi > currentQuarter ? now.Year - 1 : now.Year;
                    FilterFromDate = new DateTime(yearQ, (qi - 1) * 3 + 1, 1);
                    FilterToDate = FilterFromDate.AddMonths(3).AddDays(-1);
                }
                else if (preset.StartsWith("m") && int.TryParse(preset[1..], out var mi) && mi >= 1 && mi <= 12)
                {
                    var yearM = mi > now.Month ? now.Year - 1 : now.Year;
                    FilterFromDate = new DateTime(yearM, mi, 1);
                    FilterToDate = FilterFromDate.AddMonths(1).AddDays(-1);
                }
                else if (preset.StartsWith("year") && int.TryParse(preset[4..], out var yr) && yr >= now.Year - 1 && yr <= now.Year)
                {
                    FilterFromDate = new DateTime(yr, 1, 1);
                    FilterToDate = new DateTime(yr, 12, 31);
                }
                break;
        }
        SyncFilterKind = SyncFilterKind.DateRange;
        UpdateFilterDateLabel();
        _ = ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task LoadCompanyAndInvoicesAsync()
    {
        if (CompanyId == null) return;
        var company = await _companyService.GetByIdAsync(CompanyId.Value).ConfigureAwait(true);
        if (company != null)
        {
            CompanyName = company.CompanyName ?? company.Username ?? "";
            CompanyCode = company.CompanyCode?.Trim() ?? "";
        }
        await LoadPageAsync().ConfigureAwait(true);
    }

    private InvoiceListFilterDto BuildFilter()
    {
        var toDate = FilterToDate.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
        // IsSold: true = Bán ra (sold), false = Mua vào (purchase). Luôn truyền rõ để backend lọc đúng.
        var isSold = FilterDirection == FilterDirectionKind.BanRa;
        return new InvoiceListFilterDto(
            FilterFromDate.Date,
            toDate,
            isSold,
            FilterTrangThai >= 0 ? FilterTrangThai : null,
            (int)FilterLoaiHoaDon,
            string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            string.IsNullOrWhiteSpace(FilterKyHieu) ? null : FilterKyHieu.Trim(),
            string.IsNullOrWhiteSpace(FilterSoHoaDon) ? null : FilterSoHoaDon.Trim(),
            string.IsNullOrWhiteSpace(FilterMstNguoiBan) ? null : FilterMstNguoiBan.Trim(),
            string.IsNullOrWhiteSpace(FilterTenNguoiBan) ? null : FilterTenNguoiBan.Trim(),
            string.IsNullOrWhiteSpace(FilterMstLoaiTru) ? null : FilterMstLoaiTru.Trim(),
            string.IsNullOrWhiteSpace(FilterLoaiTruBenBan) ? null : FilterLoaiTruBenBan.Trim(),
            string.IsNullOrWhiteSpace(SortBy) ? null : SortBy,
            SortDescending
        );
    }

    private async Task LoadPageAsync()
    {
        if (CompanyId == null) return;
        IsBusy = true;
        try
        {
            var filter = BuildFilter();
            var (page, totalCount, summary) = await _invoiceSyncService.GetInvoicesPagedAsync(CompanyId.Value, filter, 1, PageSize).ConfigureAwait(true);
            Invoices = new ObservableCollection<InvoiceDisplayDto>(page);
            InvoicesView = CollectionViewSource.GetDefaultView(Invoices);
            ApplyInvoicesViewFilter();
            TotalCount = totalCount;
            CountCoMa = summary.CountCoMa;
            CountKhongMa = summary.CountKhongMa;
            CountMayTinhTien = summary.CountMayTinhTien;
            TotalChuaThue = summary.TotalChuaThue;
            TotalTienThue = summary.TotalTienThue;
            TotalThanhTien = summary.TotalThanhTien;
            UpdateXmlPdfCounts();
            PaginationSummary = totalCount <= 0 ? "0 hóa đơn" : $"{Invoices.Count} / {totalCount} hóa đơn";
            LastSyncedDisplay = totalCount > 0 ? $"Tổng {totalCount} hóa đơn (theo bộ lọc)" : "";
            var directionLabel = FilterDirection == FilterDirectionKind.BanRa ? "Bán ra" : "Mua vào";
            StatusMessage = totalCount > 0
                ? $"Đã tải {Invoices.Count} / {totalCount} hóa đơn. Cuộn xuống để tải thêm."
                : $"Không có hóa đơn {directionLabel} theo bộ lọc. Thử chọn khoảng ngày khác hoặc nhấn Đồng bộ (sau khi chọn đúng hướng) để tải dữ liệu.";
            OnPropertyChanged(nameof(HasMore));
            DownloadAllXmlCommand.NotifyCanExecuteChanged();
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi tải danh sách hóa đơn cho CompanyId={CompanyId}", CompanyId);
            StatusMessage = "Lỗi: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (CompanyId == null || !HasMore) return;
        IsBusy = true;
        try
        {
            var nextPage = (Invoices.Count / PageSize) + 1;
            var filter = BuildFilter();
            var (page, totalCount, summary) = await _invoiceSyncService.GetInvoicesPagedAsync(CompanyId.Value, filter, nextPage, PageSize).ConfigureAwait(true);
            foreach (var item in page)
                Invoices.Add(item);
            ApplyInvoicesViewFilter();
            TotalCount = totalCount;
            UpdateXmlPdfCounts();
            PaginationSummary = $"{Invoices.Count} / {totalCount} hóa đơn";
            StatusMessage = $"Đã tải {Invoices.Count} / {totalCount} hóa đơn. Cuộn xuống để tải thêm.";
            OnPropertyChanged(nameof(HasMore));
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi tải thêm: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLoadMore() => HasMore && !IsBusy;

    /// <summary>Áp dụng bộ lọc hiện tại (trang 1, gọi LoadPageAsync). Hiển thị "Đang lọc..." và loading cho đến khi xong.</summary>
    private async Task ApplyFilterAsync()
    {
        UpdateHasActiveFilter();
        StatusMessage = "Đang lọc...";
        IsBusy = true;
        try
        {
            await Task.Yield();
            await LoadPageAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnFilterForeignCurrencyOnlyChanged(bool value)
    {
        ApplyInvoicesViewFilter();
        UpdateHasActiveFilter();
    }

    private void ApplyInvoicesViewFilter()
    {
        if (InvoicesView == null)
            return;

        InvoicesView.Filter = o =>
        {
            if (o is not InvoiceDisplayDto inv) return false;
            if (FilterForeignCurrencyOnly && !inv.IsForeignCurrency)
                return false;
            return true;
        };
        InvoicesView.Refresh();
    }

    private void UpdateHasActiveFilter()
    {
        var f = BuildFilter();
        HasActiveFilter =
            f.SearchText != null ||
            (f.Tthai ?? -1) >= 0 ||
            f.LoaiHoaDon != 0 ||
            f.FilterKyHieu != null ||
            f.FilterSoHoaDon != null ||
            f.FilterMstNguoiBan != null ||
            f.FilterTenNguoiBan != null ||
            f.FilterMstLoaiTru != null ||
            f.FilterLoaiTruBenBan != null ||
            FilterForeignCurrencyOnly;
    }

    [RelayCommand]
    private async Task ApplyFilterCommandAsync()
    {
        await ApplyFilterAsync().ConfigureAwait(true);
    }

    /// <summary>Gọi từ code-behind (Enter trên ô tìm kiếm) để áp dụng lọc.</summary>
    public void RequestApplyFilter() => _ = ApplyFilterAsync();

    /// <summary>Gọi khi user bấm header cột: sắp xếp theo cột (trên server). Cùng cột = đảo chiều; cột khác = sắp tăng dần trước.</summary>
    public void ApplySortByColumn(string? sortMemberPath)
    {
        if (string.IsNullOrWhiteSpace(sortMemberPath))
        {
            SortBy = "NgayLap";
            SortDescending = true;
        }
        else if (string.Equals(SortBy, sortMemberPath, StringComparison.OrdinalIgnoreCase))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortBy = sortMemberPath;
            SortDescending = false;
        }
        _ = ApplyFilterAsync();
    }

    partial void OnIsBusyChanged(bool value)
    {
        var app = System.Windows.Application.Current;
        if (app == null)
        {
            DownloadAllXmlCommand.NotifyCanExecuteChanged();
            DownloadAllPdfCommand.NotifyCanExecuteChanged();
            LoadMoreCommand.NotifyCanExecuteChanged();
            return;
        }

        void NotifyAll()
        {
            DownloadAllXmlCommand.NotifyCanExecuteChanged();
            DownloadAllPdfCommand.NotifyCanExecuteChanged();
            LoadMoreCommand.NotifyCanExecuteChanged();
        }

        if (app.Dispatcher.CheckAccess())
        {
            NotifyAll();
        }
        else
        {
            app.Dispatcher.BeginInvoke(new Action(NotifyAll));
        }
    }

    [RelayCommand]
    private async Task SyncAsync()
    {
        if (CompanyId == null)
        {
            StatusMessage = "Không có công ty được chọn.";
            return;
        }
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Đang đồng bộ hóa đơn...";
        try
        {
            var (fromDate, toDate) = GetSyncDateRange();
            var isSold = FilterDirection == FilterDirectionKind.BanRa;
            var result = await _invoiceSyncService.SyncInvoicesAsync(CompanyId.Value, fromDate, toDate, IncludeDetail, isSold).ConfigureAwait(true);
            if (result.Success)
            {
                StatusMessage = $"Đồng bộ xong. Đã lưu {result.TotalSynced} hóa đơn.";
                await LoadCompanyAndInvoicesAsync().ConfigureAwait(true);
            }
            else
            {
                StatusMessage = "Đồng bộ thất bại: " + (result.Message ?? "Lỗi không xác định.");
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

    private (DateTime from, DateTime to) GetSyncDateRange()
    {
        if (SyncFilterKind == SyncFilterKind.DateRange)
            return (FilterFromDate.Date, FilterToDate.Date);
        ApplyFilterKindToDates();
        return (FilterFromDate, FilterToDate);
    }

    [RelayCommand]
    private void BackToCompanies()
    {
        _navigationService.NavigateToCompanies();
    }

    [RelayCommand]
    private void ToggleAdvancedFilter()
    {
        IsAdvancedFilterOpen = !IsAdvancedFilterOpen;
    }

    [RelayCommand]
    private async Task OpenBackgroundJobDialogAsync()
    {
        if (CompanyId == null)
        {
            StatusMessage = "Không có công ty được chọn.";
            return;
        }
        bool? isSold = FilterDirection == FilterDirectionKind.BanRa;
        await _backgroundJobDialog.ShowCreateAsync(CompanyId, isSold).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenJobManagement()
    {
        _backgroundJobDialog.ShowManagement();
    }

    /// <summary>Bỏ toàn bộ lọc, trở về trạng thái mặc định (Tháng trước, Bán ra, Tất cả trạng thái, không tìm kiếm).</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _suppressAutoFilter = true;
        SyncFilterKind = SyncFilterKind.Month;
        ApplyFilterKindToDates();
        FilterTrangThai = -1;
        FilterDirection = FilterDirectionKind.BanRa;
        FilterLoaiHoaDon = FilterLoaiHoaDonKind.TatCa;
        SearchText = string.Empty;
        FilterKyHieu = string.Empty;
        FilterSoHoaDon = string.Empty;
        FilterKyHieuMauSo = string.Empty;
        FilterTenNguoiBan = string.Empty;
        FilterMstNguoiBan = string.Empty;
        FilterMstLoaiTru = string.Empty;
        FilterLoaiTruBenBan = string.Empty;
        FilterKetQuaKiemTra = string.Empty;
        FilterLoaiHoaDonMuaVao = string.Empty;
        FilterTrangThaiTaiFile = string.Empty;
        FilterLinkTraCuu = string.Empty;
        FilterMaTraCuu = string.Empty;
        FilterGhiChu = string.Empty;
        FilterTags = string.Empty;
        SortBy = "NgayLap";
        SortDescending = true;
        HasActiveFilter = false;
        _suppressAutoFilter = false;
        _ = ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task ExportExcelTongHopAsync()
    {
        if (CompanyId == null) { StatusMessage = "Chọn công ty để xuất Excel."; return; }
        try
        {
            var options = new ExportExcelCreateDto(
                CompanyId.Value,
                FilterDirection == FilterDirectionKind.BanRa,
                FilterFromDate,
                FilterToDate,
                "tonghop",
                IsSummaryOnly: true);
            var job = await _backgroundJobService.EnqueueExportExcelAsync(options).ConfigureAwait(true);
            StatusMessage = "Đã đưa xuất Excel (Tổng hợp) vào hàng đợi. Xem tại Quản lý chạy tự động.";
            _backgroundJobDialog.ShowManagement();
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportExcelChiTietAsync()
    {
        if (CompanyId == null) { StatusMessage = "Chọn công ty để xuất Excel."; return; }
        try
        {
            var options = new ExportExcelCreateDto(
                CompanyId.Value,
                FilterDirection == FilterDirectionKind.BanRa,
                FilterFromDate,
                FilterToDate,
                "chitiet",
                IsSummaryOnly: false);
            var job = await _backgroundJobService.EnqueueExportExcelAsync(options).ConfigureAwait(true);
            StatusMessage = "Đã đưa xuất Excel (Chi tiết) vào hàng đợi. Xem tại Quản lý chạy tự động.";
            _backgroundJobDialog.ShowManagement();
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadAllXml))]
    private async Task DownloadAllXmlAsync()
    {
        if (CompanyId == null) return;

        // Lấy toàn bộ danh sách hóa đơn theo bộ lọc hiện tại từ database (không chỉ trang đang hiển thị).
        List<InvoiceDisplayDto> list;
        try
        {
            var filter = BuildFilter();
            var pageSize = TotalCount > 0 ? TotalCount : PageSize;
            var (page, totalCount, _) = await _invoiceSyncService
                .GetInvoicesPagedAsync(CompanyId.Value, filter, page: 1, pageSize: pageSize)
                .ConfigureAwait(true);
            list = page.ToList();
            TotalCount = totalCount;
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi lấy danh sách hóa đơn để chuẩn bị tải XML: " + ex.Message;
            return;
        }

        if (list.Count == 0)
        {
            StatusMessage = "Không có hóa đơn nào trên danh sách để tải XML.";
            return;
        }

        // Chuẩn bị popup và trạng thái tiến trình.
        IsDownloadingXml = true;
        IsDownloadPdfResults = false;
        DownloadTotal = list.Count;
        DownloadCurrentIndex = 0;
        DownloadSucceeded = 0;
        DownloadFailed = 0;
        DownloadNoXml = 0;
        DownloadSkipped = 0;
        DownloadResults.Clear();

        foreach (var inv in list)
        {
            var displayInit = inv.SoHoaDonDisplay ?? $"{inv.KyHieu}-{inv.SoHoaDon}";
            DownloadResults.Add(new DownloadItemResultViewModel(displayInit, success: false, noXml: false, message: "Chờ tải...", isSkipped: true));
        }

        IsDownloadXmlPopupOpen = true;
        DownloadXmlProgressText = $"Đang chuẩn bị tải XML cho {list.Count} hóa đơn...";
        StatusMessage = "Đang tải XML hàng loạt cho các hóa đơn theo bộ lọc...";

        try
        {
            Directory.CreateDirectory(ExportXmlFolderPath);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không tạo được thư mục lưu XML: " + ex.Message;
            SetIsDownloadingXmlFalseOnUi();
            return;
        }

        try
        {
            var total = list.Count;
            var progress = new Progress<DownloadXmlProgress>(p =>
            {
                if (p.ItemResult is { } item)
                {
                    var index = p.Current - 1;
                    if (index < 0 || index >= DownloadResults.Count)
                        index = Math.Clamp(index, 0, DownloadResults.Count - 1);

                    var display = item.SoHoaDonDisplay;
                    var vmItem = new DownloadItemResultViewModel(display, item.Success, item.NoXml, item.Message ?? "", isSkipped: false);

                    InvokeOnUi(() =>
                    {
                        DownloadCurrentIndex = p.Current;
                        DownloadXmlProgressText = $"Đang tải XML hóa đơn {p.Current}/{total}: {display}";

                        if (index >= 0 && index < DownloadResults.Count)
                            DownloadResults[index] = vmItem;

                        if (item.Success) DownloadSucceeded++;
                        else if (item.NoXml) DownloadNoXml++;
                        else DownloadFailed++;

                        // Cập nhật trạng thái XML cho cột XML.
                        _xmlStateByKey[item.InvoiceKey] = item.Success
                            ? XmlDownloadState.Downloaded
                            : (item.NoXml ? XmlDownloadState.NoXml : XmlDownloadState.None);
                    });
                }
            });

            var result = await _invoiceSyncService
                .DownloadInvoicesXmlAsync(CompanyId.Value, list, ExportXmlFolderPath, progress)
                .ConfigureAwait(true);

            InvokeOnUi(() =>
            {
                XmlStateRefreshTrigger++;
                if (!string.IsNullOrEmpty(result.ZipPath))
                    LastXmlZipPath = result.ZipPath;

                DownloadCurrentIndex = total;
                DownloadXmlProgressText =
                    $"Đã xong tải XML hàng loạt. Thành công: {DownloadSucceeded} | Thất bại: {DownloadFailed} | Không có XML: {DownloadNoXml}";
                StatusMessage = DownloadXmlProgressText;
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi tải XML hàng loạt: " + ex.Message;
        }
        finally
        {
            SetIsDownloadingXmlFalseOnUi();
        }
    }

    /// <summary>Kết thúc tải XML trên UI thread (tránh cross-thread khi gọi NotifyCanExecuteChanged).</summary>
    private void SetDownloadXmlFinishedOnUi()
    {
        void Finish()
        {
            IsDownloadingXml = false;
            DownloadXmlProgressText = string.Empty;
            XmlStateRefreshTrigger++;
        }
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher.CheckAccess() == true)
            Finish();
        else
            app?.Dispatcher.BeginInvoke(new Action(Finish));
    }

    private bool CanDownloadAllXml() => !IsDownloadingXml && !IsBusy && CompanyId != null && TotalCount > 0;

    [RelayCommand(CanExecute = nameof(CanDownloadAllPdf))]
    private async Task DownloadAllPdfAsync()
    {
        if (CompanyId == null) return;

        // Lấy toàn bộ danh sách hóa đơn theo bộ lọc hiện tại từ database (không chỉ trang đang hiển thị).
        List<InvoiceDisplayDto> list;
        try
        {
            var filter = BuildFilter();
            var pageSize = TotalCount > 0 ? TotalCount : PageSize;
            var (page, totalCount, _) = await _invoiceSyncService
                .GetInvoicesPagedAsync(CompanyId.Value, filter, page: 1, pageSize: pageSize)
                .ConfigureAwait(true);
            list = page.ToList();
            TotalCount = totalCount;
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi lấy danh sách hóa đơn để chuẩn bị tải PDF: " + ex.Message;
            return;
        }

        if (list.Count == 0)
        {
            StatusMessage = "Không có hóa đơn nào trên danh sách để tải PDF.";
            return;
        }

        IsDownloadingXml = true;
        IsDownloadPdfResults = true;
        DownloadTotal = list.Count;
        DownloadCurrentIndex = 0;
        DownloadSucceeded = 0;
        DownloadFailed = 0;
        DownloadNoXml = 0;
        DownloadSkipped = 0;
        DownloadResults.Clear();

        foreach (var inv in list)
        {
            var displayInit = inv.SoHoaDonDisplay ?? $"{inv.KyHieu}-{inv.SoHoaDon}";
            DownloadResults.Add(new DownloadItemResultViewModel(displayInit, success: false, noXml: false, message: "Chờ tải...", isSkipped: true));
        }

        IsDownloadXmlPopupOpen = true;
        DownloadXmlProgressText = $"Đang chuẩn bị tải PDF cho {list.Count} hóa đơn...";
        StatusMessage = "Đang tải PDF hàng loạt cho các hóa đơn theo bộ lọc...";

        try
        {
            var total = list.Count;

            for (var i = 0; i < total; i++)
            {
                var inv = list[i];
                var display = inv.SoHoaDonDisplay ?? $"{inv.KyHieu}-{inv.SoHoaDon}";
                var index = i;

                InvokeOnUi(() =>
                {
                    DownloadCurrentIndex = index + 1;
                    DownloadXmlProgressText = $"Đang tải PDF hóa đơn {index + 1}/{total}: {display}";
                    if (index >= 0 && index < DownloadResults.Count)
                        DownloadResults[index] = new DownloadItemResultViewModel(display, success: false, noXml: false, message: "Đang tải...", isSkipped: false);
                });

                try
                {
                    var result = await _invoicePdfService
                        .GetPdfForInvoiceByExternalIdAsync(CompanyId.Value, inv.Id)
                        .ConfigureAwait(true);

                    if (result is InvoicePdfResult.Success success)
                    {
                        var pdfPath = GetPdfPathForInvoice(inv);
                        var pdfFolder = Path.GetDirectoryName(pdfPath) ?? GetCompanyPdfFolder();
                        Directory.CreateDirectory(pdfFolder);
                        await File.WriteAllBytesAsync(pdfPath, success.PdfBytes).ConfigureAwait(false);

                        var key = GetExportBaseName(inv);
                        _pdfStateByKey[key] = true;

                        var itemSuccess = new DownloadItemResultViewModel(display, true, false, "Đã tải PDF", isSkipped: false);
                        InvokeOnUi(() =>
                        {
                            DownloadSucceeded++;
                            if (index >= 0 && index < DownloadResults.Count)
                                DownloadResults[index] = itemSuccess;
                        });
                    }
                    else if (result is InvoicePdfResult.Failure f)
                    {
                        var msg = NormalizePdfErrorMessage(f.ErrorMessage ?? "Lỗi tải PDF.");
                        var skipped = IsPdfSkipReason(msg);
                        var itemFail = new DownloadItemResultViewModel(display, false, false, msg, isSkipped: skipped);
                        InvokeOnUi(() =>
                        {
                            if (skipped) DownloadSkipped++; else DownloadFailed++;
                            if (index >= 0 && index < DownloadResults.Count)
                                DownloadResults[index] = itemFail;
                        });
                    }
                    else
                    {
                        var itemFail = new DownloadItemResultViewModel(display, false, false, "Không lấy được PDF.", isSkipped: false);
                        InvokeOnUi(() =>
                        {
                            DownloadFailed++;
                            if (index >= 0 && index < DownloadResults.Count)
                                DownloadResults[index] = itemFail;
                        });
                    }
                }
                catch (Exception ex)
                {
                    var itemEx = new DownloadItemResultViewModel(display, false, false, "Lỗi: " + ex.Message, isSkipped: false);
                    InvokeOnUi(() =>
                    {
                        DownloadFailed++;
                        if (index >= 0 && index < DownloadResults.Count)
                            DownloadResults[index] = itemEx;
                    });
                }
            }

            InvokeOnUi(() =>
            {
                PdfStateRefreshTrigger++;
                DownloadCurrentIndex = total;
                DownloadXmlProgressText =
                    $"Đã xong tải PDF hàng loạt. Thành công: {DownloadSucceeded} | Thất bại: {DownloadFailed} | Bỏ qua (chưa cấu hình): {DownloadSkipped}";
                StatusMessage = DownloadXmlProgressText;
            });
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi tải PDF hàng loạt: " + ex.Message;
        }
        finally
        {
            SetIsDownloadingXmlFalseOnUi();
        }
    }

    /// <summary>Chạy action trên UI thread (Dispatcher). Tránh thay đổi ObservableCollection từ thread khác.</summary>
    private static void InvokeOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher.CheckAccess() == true)
            action();
        else
            app?.Dispatcher.Invoke(action);
    }

    /// <summary>Đặt IsDownloadingXml = false trên UI thread để tránh InvalidOperationException (NotifyCanExecuteChanged).</summary>
    private void SetIsDownloadingXmlFalseOnUi()
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher.CheckAccess() == true)
        {
            IsDownloadingXml = false;
            return;
        }
        app?.Dispatcher.BeginInvoke(new Action(() => IsDownloadingXml = false));
    }

    /// <summary>Lỗi do thiếu cấu hình / link / mã tra cứu → coi là bỏ qua, không phải lỗi thực thi.</summary>
    private static bool IsPdfSkipReason(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("thiếu", StringComparison.OrdinalIgnoreCase)
               || message.Contains("chưa hỗ trợ", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Chưa implement", StringComparison.OrdinalIgnoreCase)
               || message.Contains("không thể tải", StringComparison.OrdinalIgnoreCase)
               || message.Contains("chưa được cấu hình", StringComparison.OrdinalIgnoreCase)
               || (message.Contains("không có", StringComparison.OrdinalIgnoreCase) && message.Contains("để tải", StringComparison.OrdinalIgnoreCase))
               || message.Contains("Không tìm thấy XML", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Chuẩn hóa message lỗi PDF: lỗi captcha được gom thành thông báo chung để user chỉ cần bấm Tải lại.</summary>
    private static string NormalizePdfErrorMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Có lỗi xảy ra khi tải PDF. Vui lòng bấm \"Tải lại\" sau.";
        if (message.Contains("captcha", StringComparison.OrdinalIgnoreCase))
            return "Có lỗi xảy ra khi kết nối hoặc giải mã captcha. Vui lòng bấm \"Tải lại\" sau.";
        return message;
    }

    [RelayCommand(CanExecute = nameof(CanRetryFailedPdf))]
    private async Task RetryFailedPdfAsync()
    {
        List<InvoiceDisplayDto> failed;
        lock (_lastPdfFailedInvoices)
        {
            failed = _lastPdfFailedInvoices.ToList();
        }
        if (CompanyId == null || failed.Count == 0) return;

        IsDownloadingXml = true;
        IsDownloadPdfResults = true;
        DownloadTotal = failed.Count;
        DownloadCurrentIndex = 0;
        DownloadSucceeded = 0;
        DownloadFailed = 0;
        DownloadNoXml = 0;
        DownloadSkipped = 0;
        DownloadResults.Clear();
        _lastPdfFailedInvoices.Clear();
        HasFailedPdfInLastBatch = false;
        // Khởi tạo danh sách: tất cả hóa đơn thất bại ở trạng thái "Chờ tải lại..."
        foreach (var inv in failed)
        {
            var displayInit = inv.SoHoaDonDisplay ?? $"{inv.KyHieu}-{inv.SoHoaDon}";
            DownloadResults.Add(new DownloadItemResultViewModel(displayInit, success: false, noXml: false, message: "Chờ tải lại...", isSkipped: true));
        }
        IsDownloadXmlPopupOpen = true;
        DownloadXmlProgressText = "Đang chuẩn bị tải lại các hóa đơn thất bại...";
        StatusMessage = "Đang tải lại PDF cho các hóa đơn thất bại...";

        try
        {
            var total = failed.Count;
            for (var i = 0; i < total; i++)
            {
                var inv = failed[i];
                var display = inv.SoHoaDonDisplay ?? $"{inv.KyHieu}-{inv.SoHoaDon}";
                var index = i + 1;
                InvokeOnUi(() =>
                {
                    DownloadCurrentIndex = index;
                    DownloadXmlProgressText = $"Đang tải lại PDF hóa đơn {index}/{total}: {display}";
                    if (i >= 0 && i < DownloadResults.Count)
                        DownloadResults[i] = new DownloadItemResultViewModel(display, success: false, noXml: false, message: "Đang tải lại...", isSkipped: false);
                });

                try
                {
                    var result = await _invoicePdfService
                        .GetPdfForInvoiceByExternalIdAsync(CompanyId.Value, inv.Id)
                        .ConfigureAwait(true);

                    if (result is InvoicePdfResult.Success success)
                    {
                        var pdfPath = GetPdfPathForInvoice(inv);
                        var pdfFolder = Path.GetDirectoryName(pdfPath) ?? GetCompanyPdfFolder();
                        Directory.CreateDirectory(pdfFolder);
                        await File.WriteAllBytesAsync(pdfPath, success.PdfBytes).ConfigureAwait(false);

                        var key = GetExportBaseName(inv);
                        _pdfStateByKey[key] = true;

                        var itemSuccess = new DownloadItemResultViewModel(display, true, false, "Đã tải PDF (lần 2)", isSkipped: false);
                        InvokeOnUi(() =>
                        {
                            DownloadSucceeded++;
                            DownloadResults[i] = itemSuccess;
                        });
                    }
                    else if (result is InvoicePdfResult.Failure f)
                    {
                        var msg = NormalizePdfErrorMessage(f.ErrorMessage ?? "Lỗi tải PDF.");
                        var skipped = IsPdfSkipReason(msg);
                        var itemFail = new DownloadItemResultViewModel(display, false, false, msg, isSkipped: skipped);
                        InvokeOnUi(() =>
                        {
                            if (skipped) DownloadSkipped++; else DownloadFailed++;
                            DownloadResults[i] = itemFail;
                        });
                        if (!skipped)
                        {
                            lock (_lastPdfFailedInvoices)
                            {
                                _lastPdfFailedInvoices.Add(inv);
                            }
                        }
                    }
                    else
                    {
                        var itemFail = new DownloadItemResultViewModel(display, false, false, "Không lấy được PDF.", isSkipped: false);
                        InvokeOnUi(() =>
                        {
                            DownloadFailed++;
                            DownloadResults[i] = itemFail;
                        });
                        lock (_lastPdfFailedInvoices)
                        {
                            _lastPdfFailedInvoices.Add(inv);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var itemEx = new DownloadItemResultViewModel(display, false, false, "Lỗi: " + ex.Message, isSkipped: false);
                    InvokeOnUi(() =>
                    {
                        DownloadFailed++;
                        DownloadResults[i] = itemEx;
                    });
                    lock (_lastPdfFailedInvoices)
                    {
                        _lastPdfFailedInvoices.Add(inv);
                    }
                }
            }

            InvokeOnUi(() =>
            {
                PdfStateRefreshTrigger++;
                DownloadCurrentIndex = total;
                DownloadXmlProgressText = $"Đã xong lần tải lại. Thành công: {DownloadSucceeded} | Thất bại: {DownloadFailed} | Bỏ qua (chưa cấu hình): {DownloadSkipped}";
                StatusMessage = DownloadXmlProgressText;
                HasFailedPdfInLastBatch = DownloadFailed > 0;
            });
        }
        finally
        {
            SetIsDownloadingXmlFalseOnUi();
        }
    }

    private bool CanRetryFailedPdf() =>
        HasFailedPdfInLastBatch && !IsDownloadingXml && !IsBusy && CompanyId != null;

    private bool CanDownloadAllPdf() => !IsDownloadingXml && !IsBusy && CompanyId != null && TotalCount > 0;

    partial void OnIsDownloadingXmlChanged(bool value)
    {
        DownloadAllXmlCommand.NotifyCanExecuteChanged();
        DownloadAllPdfCommand.NotifyCanExecuteChanged();
    }

    partial void OnXmlStateRefreshTriggerChanged(int value) => UpdateXmlPdfCounts();
    partial void OnPdfStateRefreshTriggerChanged(int value) => UpdateXmlPdfCounts();

    /// <summary>Đếm trên danh sách đã tải: có/chưa XML, có/chưa PDF.</summary>
    private void UpdateXmlPdfCounts()
    {
        var list = Invoices;
        if (list == null || list.Count == 0)
        {
            CountCoXml = 0;
            CountChuaXml = 0;
            CountCoPdf = 0;
            CountChuaPdf = 0;
            CountNgoaiTe = 0;
            return;
        }
        int coXml = 0, coPdf = 0, ngoaiTe = 0;
        foreach (var inv in list)
        {
            if (GetXmlState(inv) == XmlDownloadState.Downloaded) coXml++;
            if (HasPdf(inv)) coPdf++;
            if (inv.IsForeignCurrency) ngoaiTe++;
        }
        CountCoXml = coXml;
        CountChuaXml = list.Count - coXml;
        CountCoPdf = coPdf;
        CountChuaPdf = list.Count - coPdf;
        CountNgoaiTe = ngoaiTe;
    }

    [RelayCommand]
    private void CloseDownloadXmlPopup() => IsDownloadXmlPopupOpen = false;

    [RelayCommand]
    private void OpenXmlZipFolder()
    {
        if (string.IsNullOrWhiteSpace(LastXmlZipPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(LastXmlZipPath);
            if (dir == null || !Directory.Exists(dir))
            {
                StatusMessage = "Thư mục ZIP không còn tồn tại.";
                return;
            }
            var psi = new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusMessage = "Không mở được thư mục ZIP: " + ex.Message;
        }
    }

    /// <summary>Mở menu thao tác cho một dòng (nút "...").</summary>
    [RelayCommand]
    private void OpenRowActionMenu(InvoiceDisplayDto? inv)
    {
        if (inv == null) return;
        ActionMenuInvoice = inv;
        IsActionMenuOpen = true;
    }

    [RelayCommand]
    private void CloseActionMenu()
    {
        IsActionMenuOpen = false;
        ActionMenuInvoice = null;
    }

    /// <summary>Đồng bộ lại (chạy sync toàn bộ theo bộ lọc hiện tại).</summary>
    [RelayCommand(CanExecute = nameof(CanRunRowAction))]
    private async Task SyncAgainForRowAsync()
    {
        CloseActionMenu();
        await SyncAsync().ConfigureAwait(true);
    }

    /// <summary>Xem hóa đơn: ưu tiên file HTML local (ExportXml/key/*.html), không có thì gọi API lấy detail và fill template.</summary>
    [RelayCommand(CanExecute = nameof(CanRunRowAction))]
    private async Task ViewInvoiceForRowAsync()
    {
        var inv = ActionMenuInvoice;
        CloseActionMenu();
        if (inv == null || CompanyId == null) return;
        IsBusy = true;
        IsRowActionBusy = true;
        FlushUiUpdates();
        try
        {
            var htmlPath = GetHtmlPathForInvoice(inv);
            if (!string.IsNullOrEmpty(htmlPath) && htmlPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                ClearRowActionBusyOnUi(); // Tắt popup trước khi mở cửa sổ (ShowDialog sẽ chặn đến khi đóng)
                OpenViewerOnUiThread(
                    htmlPath,
                    inv,
                    () => _invoiceDetailViewService.GetInvoicePrintHtmlPathAsync(CompanyId!.Value, inv));
                StatusMessage = "Đã mở xem hóa đơn";
                return;
            }
            StatusMessage = "Đang tải chi tiết hóa đơn...";
            var (filePath, error) = await _invoiceDetailViewService.GetInvoiceDetailHtmlAsync(CompanyId.Value, inv).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(error))
            {
                StatusMessage = error;
                return;
            }
            if (string.IsNullOrEmpty(filePath))
            {
                StatusMessage = "Không có nội dung để hiển thị.";
                return;
            }
            ClearRowActionBusyOnUi(); // Tắt popup trước khi mở cửa sổ (ShowDialog sẽ chặn đến khi đóng)
            OpenViewerOnUiThread(
                filePath,
                inv,
                () => _invoiceDetailViewService.GetInvoicePrintHtmlPathAsync(CompanyId!.Value, inv));
            StatusMessage = "Đã mở xem hóa đơn";
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi: " + ex.Message;
        }
        finally
        {
            ClearRowActionBusyOnUi();
        }
    }

    /// <summary>
    /// Xem PDF: nếu đã có file cache thì mở, không thì gọi service lấy PDF.
    /// Với NCC yêu cầu XML (BKAV/eHoadon): nếu chưa có XML sẽ tự tải XML trước rồi gọi lại PDF.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunRowAction))]
    private async Task ViewPdfForRowAsync()
    {
        var inv = ActionMenuInvoice;
        CloseActionMenu();
        if (inv == null || CompanyId == null) return;
        var key = GetExportBaseName(inv);
        var pdfPath = GetPdfPathForInvoice(inv);
        IsBusy = true;
        IsRowActionBusy = true;
        FlushUiUpdates();
        try
        {
            // Nếu đã có file PDF trong thư mục ứng dụng thì mở bằng viewer nhúng.
            if (File.Exists(pdfPath))
            {
                _pdfStateByKey[key] = true;
                PdfStateRefreshTrigger++;
                ClearRowActionBusyOnUi(); // Tắt popup trước khi mở cửa sổ (ShowDialog sẽ chặn đến khi đóng)
                OpenViewerOnUiThread(pdfPath, inv, null);
                StatusMessage = "Đã mở PDF hóa đơn (file đã lưu).";
                return;
            }
            StatusMessage = "Đang tải PDF hóa đơn...";
            var result = await _invoicePdfService.GetPdfForInvoiceByExternalIdAsync(CompanyId.Value, inv.Id).ConfigureAwait(true);

            // Nếu lỗi vì chưa có XML (NCC cần XML như BKAV), tự tải XML rồi thử lại một lần.
            if (result is InvoicePdfResult.Failure firstFail &&
                firstFail.ErrorMessage.Contains("Không tìm thấy XML cho hóa đơn", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "Chưa có XML, đang tải XML cho hóa đơn...";
                var folder = ExportXmlFolderPath;
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    StatusMessage = "Lỗi thư mục khi tải XML: " + ex.Message;
                    return;
                }

                var list = new List<InvoiceDisplayDto> { inv };
                var progress = new Progress<DownloadXmlProgress>(p =>
                {
                    if (p.ItemResult is { } item)
                    {
                        _xmlStateByKey[item.InvoiceKey] = item.Success ? XmlDownloadState.Downloaded : (item.NoXml ? XmlDownloadState.NoXml : XmlDownloadState.None);
                        XmlStateRefreshTrigger++;
                    }
                });

                try
                {
                    var xmlResult = await _invoiceSyncService.DownloadInvoicesXmlAsync(CompanyId.Value, list, folder, progress).ConfigureAwait(true);
                    if (!xmlResult.Success)
                    {
                        StatusMessage = "Không tải được XML: " + (xmlResult.Message ?? "");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = "Lỗi tải XML: " + ex.Message;
                    return;
                }

                StatusMessage = "Đã tải XML, đang tải lại PDF hóa đơn...";
                result = await _invoicePdfService.GetPdfForInvoiceByExternalIdAsync(CompanyId.Value, inv.Id).ConfigureAwait(true);
            }
            if (result is InvoicePdfResult.Success success)
            {
                var pdfFolder = Path.GetDirectoryName(pdfPath) ?? GetCompanyPdfFolder();
                Directory.CreateDirectory(pdfFolder);
                await File.WriteAllBytesAsync(pdfPath, success.PdfBytes).ConfigureAwait(false);
                _pdfStateByKey[key] = true;
                PdfStateRefreshTrigger++;
                ClearRowActionBusyOnUi(); // Tắt popup trước khi mở cửa sổ (ShowDialog sẽ chặn đến khi đóng)
                OpenViewerOnUiThread(pdfPath, inv, null);
                StatusMessage = "Đã tải và mở PDF hóa đơn.";
            }
            else
            {
                StatusMessage = result is InvoicePdfResult.Failure f ? f.ErrorMessage : "Không lấy được PDF.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi xem PDF hóa đơn cho InvoiceId={InvoiceId}, CompanyId={CompanyId}", inv.Id, CompanyId);
            StatusMessage = "Lỗi xem PDF: " + ex.Message;
        }
        finally
        {
            ClearRowActionBusyOnUi();
        }
    }

    /// <summary>Tải XML cho đúng một hóa đơn đang chọn trong menu.</summary>
    [RelayCommand(CanExecute = nameof(CanRunRowAction))]
    private async Task DownloadXmlForRowAsync()
    {
        var inv = ActionMenuInvoice;
        CloseActionMenu();
        if (inv == null || CompanyId == null) return;

        // 1. Nếu đã có XML local thì mở viewer ngay.
        var existingXmlPath = GetXmlPathForInvoice(inv);
        if (!string.IsNullOrEmpty(existingXmlPath) && File.Exists(existingXmlPath))
        {
            OpenXmlViewer(existingXmlPath);
            StatusMessage = "Đã mở XML hóa đơn (file đã lưu).";
            return;
        }

        // 2. Nếu chưa có thì tải rồi mở viewer.
        var folder = ExportXmlFolderPath;
        StatusMessage = "Đang tải XML hóa đơn...";
        IsBusy = true;
        IsRowActionBusy = true;
        FlushUiUpdates();
        try
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                StatusMessage = "Lỗi thư mục: " + ex.Message;
                return;
            }
            var list = new List<InvoiceDisplayDto> { inv };
            var progress = new Progress<DownloadXmlProgress>(p =>
            {
                if (p.ItemResult is { } item)
                {
                    _xmlStateByKey[item.InvoiceKey] = item.Success ? XmlDownloadState.Downloaded : (item.NoXml ? XmlDownloadState.NoXml : XmlDownloadState.None);
                    XmlStateRefreshTrigger++;
                }
            });
            try
            {
                var result = await _invoiceSyncService.DownloadInvoicesXmlAsync(CompanyId.Value, list, folder, progress).ConfigureAwait(true);
                if (!result.Success)
                {
                    StatusMessage = "Lỗi tải XML: " + (result.Message ?? "Không xác định.");
                    return;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = "Lỗi tải XML: " + ex.Message;
                return;
            }

            // Sau khi tải xong, tìm lại đường dẫn XML (trong thư mục yyyy_MM) và mở viewer nếu có.
            var xmlPath = GetXmlPathForInvoice(inv);
            if (!string.IsNullOrEmpty(xmlPath) && File.Exists(xmlPath))
            {
                var key = GetExportBaseName(inv);
                _xmlStateByKey[key] = XmlDownloadState.Downloaded;
                XmlStateRefreshTrigger++;

                ClearRowActionBusyOnUi();
                OpenXmlViewer(xmlPath);
                StatusMessage = "Đã tải và mở cửa sổ xem XML.";
            }
            else
            {
                StatusMessage = "Đã tải nhưng không tìm thấy file XML (kiểm tra thư mục ExportXml).";
            }
        }
        finally
        {
            ClearRowActionBusyOnUi();
        }
    }

    /// <summary>Xem danh sách hóa đơn liên quan (thay thế / điều chỉnh...) cho hóa đơn đang chọn.</summary>
    [RelayCommand(CanExecute = nameof(CanRunRowAction))]
    private async Task ViewRelatedInvoicesForRowAsync()
    {
        var inv = ActionMenuInvoice;
        if (inv == null || CompanyId == null) return;
        RelatedInvoicesCurrentInvoice = inv;
        CloseActionMenu();
        IsBusy = true;
        IsRowActionBusy = true;
        FlushUiUpdates();
        try
        {
            StatusMessage = "Đang tải hóa đơn liên quan...";
            var (items, error) = await _invoiceDetailViewService.GetInvoiceRelatedAsync(CompanyId.Value, inv).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(error))
            {
                StatusMessage = error;
                return;
            }

            RelatedInvoices.Clear();
            foreach (var item in items)
                RelatedInvoices.Add(item);

            IsRelatedInvoicesPopupOpen = true;
            StatusMessage = RelatedInvoices.Count > 0 ? "Đã tải danh sách hóa đơn liên quan." : "Không có hóa đơn liên quan.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi lấy hóa đơn liên quan: " + ex.Message;
        }
        finally
        {
            ClearRowActionBusyOnUi();
        }
    }

    [RelayCommand]
    private void CloseRelatedInvoicesPopup()
    {
        IsRelatedInvoicesPopupOpen = false;
    }

    private bool CanRunRowAction() => ActionMenuInvoice != null;

    /// <summary>Đặt IsBusy và IsRowActionBusy = false trên UI thread để popup loading chắc chắn đóng.</summary>
    private void ClearRowActionBusyOnUi()
    {
        var app = System.Windows.Application.Current;
        if (app == null)
        {
            IsBusy = false;
            IsRowActionBusy = false;
            return;
        }
        if (app.Dispatcher.CheckAccess())
        {
            IsBusy = false;
            IsRowActionBusy = false;
        }
        else
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsBusy = false;
                IsRowActionBusy = false;
            }));
        }
    }

    /// <summary>Ép xử lý cập nhật UI ngay để popup loading hiện trước khi await.</summary>
    private void FlushUiUpdates()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));
    }

    /// <summary>Tìm file XML tương ứng với hóa đơn (nếu đã được tải về). Service lưu vào ExportXml\yyyy_MM\baseName\ hoặc ExportXml\yyyy_MM\baseName.xml.</summary>
    private string? GetXmlPathForInvoice(InvoiceDisplayDto inv)
    {
        var key = GetExportBaseName(inv);
        var monthFolderName = (inv.NgayLap ?? DateTime.Now).ToString("yyyy_MM");
        var monthFolder = Path.Combine(ExportXmlFolderPath, monthFolderName);

        // 1) Thư mục theo tháng + tên hóa đơn (ZIP giải nén ra thư mục con)
        var subDir = Path.Combine(monthFolder, key);
        if (Directory.Exists(subDir))
        {
            var xml = Directory.GetFiles(subDir, "*.xml", SearchOption.AllDirectories).FirstOrDefault();
            if (xml != null) return xml;
        }
        // 2) File XML trực tiếp trong tháng: yyyy_MM\key.xml
        var monthXmlPath = Path.Combine(monthFolder, key + ".xml");
        if (File.Exists(monthXmlPath)) return monthXmlPath;

        // 3) Cấu trúc cũ: ngay dưới ExportXml (không có thư mục tháng)
        var legacySubDir = Path.Combine(ExportXmlFolderPath, key);
        if (Directory.Exists(legacySubDir))
        {
            var xml = Directory.GetFiles(legacySubDir, "*.xml", SearchOption.AllDirectories).FirstOrDefault();
            if (xml != null) return xml;
        }
        var directPath = Path.Combine(ExportXmlFolderPath, key + ".xml");
        return File.Exists(directPath) ? directPath : null;
    }

    /// <summary>Mở cửa sổ xem XML với nội dung đã được format.</summary>
    private void OpenXmlViewer(string xmlPath)
    {
        var app = System.Windows.Application.Current;

        void Show()
        {
            var wnd = new Views.XmlViewerWindow
            {
                Owner = app?.MainWindow
            };
            wnd.LoadFile(xmlPath);
            // Mở dạng modal để trong lúc xem XML không tương tác được với màn hình nền.
            wnd.ShowDialog();
        }

        if (app?.Dispatcher.CheckAccess() == true)
            Show();
        else
            app?.Dispatcher.Invoke(Show);
    }

    partial void OnActionMenuInvoiceChanged(InvoiceDisplayDto? value)
    {
        SyncAgainForRowCommand.NotifyCanExecuteChanged();
        ViewInvoiceForRowCommand.NotifyCanExecuteChanged();
        ViewPdfForRowCommand.NotifyCanExecuteChanged();
        DownloadXmlForRowCommand.NotifyCanExecuteChanged();
        ViewRelatedInvoicesForRowCommand.NotifyCanExecuteChanged();
        OpenLookupPopupForRowCommand.NotifyCanExecuteChanged();
        OpenHtInvoiceAutoLookupCommand.NotifyCanExecuteChanged();

        var canRelated = false;
        if (value is { TrangThaiDisplay: { } status })
        {
            var s = status.ToLowerInvariant();
            // Chỉ bật nút cho các trạng thái có chữ "điều chỉnh" hoặc "thay thế".
            canRelated = s.Contains("điều chỉnh") || s.Contains("dieu chinh") ||
                         s.Contains("thay thế") || s.Contains("thay the");
        }
        IsActionMenuRelatedVisible = canRelated;
    }

    /// <summary>Mở popup gợi ý tra cứu PDF cho hóa đơn đang chọn.</summary>
    [RelayCommand(CanExecute = nameof(CanRunRowAction))]
    private async Task OpenLookupPopupForRowAsync()
    {
        var inv = ActionMenuInvoice;
        CloseActionMenu();
        if (inv == null || CompanyId == null) return;

        try
        {
            StatusMessage = "Đang lấy thông tin gợi ý tra cứu...";
            var suggestion = await _invoicePdfService
                .GetLookupSuggestionAsync(CompanyId.Value, inv.Id)
                .ConfigureAwait(true);

            // Không có provider theo NCC: vẫn mở popup, hiển thị link tra cứu mặc định (GDT) và mã bí mật = mccqt (MaHoaDon).
            if (suggestion == null)
            {
                LookupProviderKey = string.Empty;
                LookupProviderName = "Chưa có cấu hình gợi ý";
                LookupSearchUrl = "https://tracuunnt.gdt.gov.vn";
                LookupSecretCode = string.IsNullOrWhiteSpace(inv.MaHoaDon) ? null : inv.MaHoaDon.Trim();
                LookupSellerTaxCode = inv.NbMst;
                IsHtInvoiceLookup = false;
                IsLookupPopupOpen = true;
                StatusMessage = "Chưa có cấu hình gợi ý tra cứu cho hóa đơn này.";
                return;
            }

            LookupProviderKey = suggestion.ProviderKey;
            LookupProviderName = suggestion.ProviderName ?? suggestion.ProviderKey;
            LookupSearchUrl = suggestion.SearchUrl;
            LookupSecretCode = !string.IsNullOrWhiteSpace(suggestion.SecretCode) ? suggestion.SecretCode.Trim() : (string.IsNullOrWhiteSpace(inv.MaHoaDon) ? null : inv.MaHoaDon.Trim());
            LookupSellerTaxCode = suggestion.SellerTaxCode;
            IsHtInvoiceLookup = IsHtInvoiceProviderKey(suggestion.ProviderKey);
            IsLookupPopupOpen = true;
            StatusMessage = "Đã lấy thông tin gợi ý tra cứu.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Lỗi lấy gợi ý tra cứu: " + ex.Message;
        }
    }

    [RelayCommand]
    private void CloseLookupPopup()
    {
        IsLookupPopupOpen = false;
    }

    /// <summary>
    /// Mở trình duyệt nhúng tới link tra cứu và tự điền mã tra cứu (chỉ cho HTInvoice).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunRowAction))]
    private void OpenHtInvoiceAutoLookup()
    {
        var inv = ActionMenuInvoice;
        if (!IsLookupPopupOpen || inv == null || CompanyId == null)
            return;
        if (!IsHtInvoiceLookup || string.IsNullOrWhiteSpace(LookupSearchUrl))
            return;

        var code = string.IsNullOrWhiteSpace(LookupSecretCode) ? null : LookupSecretCode;
        _invoiceViewerService.OpenLookupBrowser(LookupSearchUrl!, CompanyCode, CompanyName, inv, code);
    }

    private static bool IsHtInvoiceProviderKey(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return false;
        var trimmed = providerKey.Trim();
        var noZero = trimmed.TrimStart('0');
        return string.Equals(trimmed, "0315638251", StringComparison.OrdinalIgnoreCase)
               || string.Equals(noZero, "315638251", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Đường dẫn file HTML để xem hóa đơn (trong thư mục ExportXml/key/ hoặc key.xml).</summary>
    private string? GetHtmlPathForInvoice(InvoiceDisplayDto inv)
    {
        var key = GetExportBaseName(inv);
        var subDir = Path.Combine(ExportXmlFolderPath, key);
        if (Directory.Exists(subDir))
        {
            var html = Directory.GetFiles(subDir, "*.html", SearchOption.AllDirectories).FirstOrDefault();
            if (html != null) return html;
        }
        var xmlPath = Path.Combine(ExportXmlFolderPath, key + ".xml");
        return File.Exists(xmlPath) ? xmlPath : null;
    }

    /// <summary>
    /// Trạng thái XML cho một hóa đơn: None (chưa tải), Downloaded (✓), NoXml (✗).
    /// Ưu tiên kiểm tra thực tế trên ổ đĩa cho trạng thái Downloaded; dùng XmlStatus==2 để nhận biết &quot;không có XML&quot;.
    /// </summary>
    public XmlDownloadState GetXmlState(InvoiceDisplayDto inv)
    {
        var key = GetExportBaseName(inv);
        var subDir = Path.Combine(ExportXmlFolderPath, key);
        if (Directory.Exists(subDir) && Directory.GetFileSystemEntries(subDir).Length > 0)
            return XmlDownloadState.Downloaded;
        if (File.Exists(Path.Combine(ExportXmlFolderPath, key + ".xml")))
            return XmlDownloadState.Downloaded;

        // Backend đã xác nhận không có XML cho hóa đơn này.
        if (inv.XmlStatus == 2)
            return XmlDownloadState.NoXml;
        if (_xmlStateByKey.TryGetValue(key, out var state))
            return state;
        return XmlDownloadState.None;
    }

    private static string GetExportBaseName(InvoiceDisplayDto inv)
    {
        var kh = inv.KyHieu ?? "";
        foreach (var c in Path.GetInvalidFileNameChars())
            kh = kh.Replace(c, '_');
        return $"{kh}-{inv.SoHoaDon}";
    }

    /// <summary>Thư mục PDF cho công ty hiện tại (Documents\SmartInvoice\{CompanyCodeOrName}\Pdf).</summary>
    private string GetCompanyPdfFolder()
    {
        var smartInvoiceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartInvoice");
        var companyFolderName = SanitizeFileName(
            !string.IsNullOrWhiteSpace(CompanyCode) ? CompanyCode :
            !string.IsNullOrWhiteSpace(CompanyName) ? CompanyName : "CongTy");
        var companyRoot = Path.Combine(smartInvoiceRoot, companyFolderName);
        return Path.Combine(companyRoot, "Pdf");
    }

    /// <summary>Đường dẫn file PDF chuẩn cho một hóa đơn khi tải từ NCC.</summary>
    private string GetPdfPathForInvoice(InvoiceDisplayDto inv)
    {
        var pdfRoot = GetCompanyPdfFolder();
        var date = inv.NgayLap ?? inv.NgayKy ?? DateTime.Now;
        var monthFolderName = date.ToString("yyyy_MM");
        var pdfFolder = Path.Combine(pdfRoot, monthFolderName);
        var key = GetExportBaseName(inv);
        var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(key) ? "Invoice.pdf" : key + ".pdf");
        return Path.Combine(pdfFolder, fileName);
    }

    /// <summary>Kiểm tra hóa đơn đã có file PDF tải về chưa.</summary>
    public bool HasPdf(InvoiceDisplayDto inv)
    {
        var key = GetExportBaseName(inv);
        var path = GetPdfPathForInvoice(inv);
        if (File.Exists(path))
        {
            _pdfStateByKey[key] = true;
            return true;
        }
        return _pdfStateByKey.TryGetValue(key, out var has) && has;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private void ExportToCsvCore(bool isSummary)
    {
        try
        {
            var rows = new List<string>();
            var header = "Ký hiệu;Số HĐ;Ngày lập;Ngày ký;Người bán;MST người bán;Người mua;MST người mua;Trạng thái;Chưa thuế;Tiền thuế;Thành tiền";
            rows.Add(header);
            if (InvoicesView != null)
            {
                foreach (var item in InvoicesView)
                {
                    if (item is not InvoiceDisplayDto inv) continue;
                    var line = $"\"{(inv.KyHieu ?? "")}\";{inv.SoHoaDon};{inv.NgayLap:dd/MM/yyyy};{inv.NgayKy:dd/MM/yyyy};\"{(inv.NguoiBan ?? "")}\";\"{(inv.NbMst ?? "")}\";\"{(inv.NguoiMua ?? "")}\";\"{(inv.MstMua ?? "")}\";\"{(inv.TrangThaiDisplay ?? "")}\";{inv.Tgtcthue ?? 0:N0};{inv.Tgtthue ?? 0:N0};{inv.TongTien ?? 0:N0}";
                    rows.Add(line);
                }
            }
            var name = isSummary ? "TongHop" : "ChiTiet";
            var fileName = $"HoaDon_{name}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), fileName);
            System.IO.File.WriteAllText(path, string.Join(Environment.NewLine, rows), System.Text.Encoding.UTF8);
            StatusMessage = $"Đã xuất file: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = "Xuất file lỗi: " + ex.Message;
        }
    }
}
