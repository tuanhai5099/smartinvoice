using SmartInvoice.Application.Services;

namespace SmartInvoice.Modules.Companies.Services;

public interface IBackgroundJobDialogService
{
    /// <summary>Mở popup tạo job tải nền. Nếu đang ở màn hình invoices thì có thể truyền sẵn CompanyId + IsSold.</summary>
    Task ShowCreateAsync(Guid? defaultCompanyId, bool? defaultIsSold, CancellationToken cancellationToken = default);

    /// <summary>Mở cửa sổ quản lý job đang chạy / lịch sử job.</summary>
    void ShowManagement();

    /// <summary>Hiện thông báo toast (vd. "Thêm thành công").</summary>
    void ShowToast(string title, string message);
}

