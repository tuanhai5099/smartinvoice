namespace SmartInvoice.Application.Services;

/// <summary>
/// Điều hướng nội dung chính (Shell): danh sách công ty hoặc quản lý hóa đơn theo công ty.
/// </summary>
public interface INavigationService
{
    void NavigateToCompanies();
    void NavigateToInvoiceList(Guid companyId);
}
