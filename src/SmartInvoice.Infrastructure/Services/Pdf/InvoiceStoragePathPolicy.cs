namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Central policy cho đường dẫn lưu trữ XML/PDF theo công ty và kỳ tháng.
/// </summary>
public interface IInvoiceStoragePathPolicy
{
    string GetCompanyRoot(string? companyCodeOrNameOrId);
    string GetCompanyXmlRoot(string? companyCodeOrNameOrId);
    string GetInvoicePdfPath(string? companyCodeOrNameOrId, string? kyHieu, int soHoaDon, DateTime? ngayLap);
}

public sealed class DefaultInvoiceStoragePathPolicy : IInvoiceStoragePathPolicy
{
    public string GetCompanyRoot(string? companyCodeOrNameOrId) =>
        InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCodeOrNameOrId);

    public string GetCompanyXmlRoot(string? companyCodeOrNameOrId) =>
        InvoiceFileStoragePathHelper.GetCompanyXmlRootPath(companyCodeOrNameOrId);

    public string GetInvoicePdfPath(string? companyCodeOrNameOrId, string? kyHieu, int soHoaDon, DateTime? ngayLap) =>
        InvoiceFileStoragePathHelper.GetInvoicePdfPath(companyCodeOrNameOrId, kyHieu, soHoaDon, ngayLap);
}
