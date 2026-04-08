namespace SmartInvoice.Infrastructure.Services;

/// <summary>
/// Cấu trúc thư mục lưu file theo công ty và tháng/năm: Mã công ty → Tháng_Năm → XML/PDF.
/// Dùng chung cho tải XML, PDF và các file theo từng hóa đơn để dễ quản lý.
/// </summary>
public static class InvoiceFileStoragePathHelper
{
    private const ushort DefaultXmlInvoiceFormCode = 0;

    /// <summary>Thư mục gốc ứng dụng (Documents\SmartInvoice).</summary>
    public static string GetAppRootPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartInvoice");

    /// <summary>
    /// Thư mục gốc theo công ty cho PDF: Documents\SmartInvoice\{mã công ty}.
    /// Dưới đó tạo thư mục tháng_năm (yyyy_MM) rồi lưu PDF.
    /// </summary>
    public static string GetCompanyRootPath(string? companyCodeOrNameOrId)
    {
        var name = companyCodeOrNameOrId?.Trim();
        if (string.IsNullOrEmpty(name))
            name = "CongTy";
        name = SanitizeFileName(name);
        if (name.Length > 64)
            name = name[..64];
        return Path.Combine(GetAppRootPath(), name);
    }

    /// <summary>
    /// Thư mục gốc theo công ty cho XML: Documents\SmartInvoice\{mã công ty}\XML.
    /// Dưới đó tạo thư mục tháng_năm (yyyy_MM) rồi lưu file XML.
    /// </summary>
    public static string GetCompanyXmlRootPath(string? companyCodeOrNameOrId)
    {
        var companyRoot = GetCompanyRootPath(companyCodeOrNameOrId);
        return Path.Combine(companyRoot, "XML");
    }

    /// <summary>Thư mục gốc PDF theo công ty: Documents\SmartInvoice\{mã công ty}\Pdf (giống UI danh sách hóa đơn).</summary>
    public static string GetCompanyPdfRootPath(string? companyCodeOrNameOrId) =>
        Path.Combine(GetCompanyRootPath(companyCodeOrNameOrId), "Pdf");

    /// <summary>Đường dẫn file PDF một hóa đơn (tháng_năm + tên KyHieu-SoHoaDon.pdf), đồng bộ với <c>InvoiceListViewModel.GetPdfPathForInvoice</c>.</summary>
    public static string GetInvoicePdfPath(string? companyCodeOrNameOrId, string? kyHieu, int soHoaDon, DateTime? ngayLap)
    {
        var pdfRoot = GetCompanyPdfRootPath(companyCodeOrNameOrId);
        var monthPath = GetMonthYearPath(pdfRoot, ngayLap);
        var kh = kyHieu ?? "";
        foreach (var c in Path.GetInvalidFileNameChars())
            kh = kh.Replace(c, '_');
        var baseKey = $"{kh}-{soHoaDon}";
        var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(baseKey) ? "Invoice.pdf" : baseKey + ".pdf");
        return Path.Combine(monthPath, fileName);
    }

    /// <summary>Tên thư mục tháng_năm theo ngày hóa đơn (vd. 2025_02). Nếu không có ngày thì dùng tháng hiện tại.</summary>
    public static string GetMonthYearFolderName(DateTime? invoiceDate)
    {
        var d = invoiceDate ?? DateTime.Now;
        return d.ToString("yyyy_MM");
    }

    /// <summary>Đường dẫn thư mục lưu file cho một hóa đơn: companyRoot\yyyy_MM\.</summary>
    public static string GetMonthYearPath(string companyRootPath, DateTime? invoiceDate)
    {
        var folderName = GetMonthYearFolderName(invoiceDate);
        return Path.Combine(companyRootPath, folderName);
    }

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrEmpty(name) ? "CongTy" : name;
    }

    /// <summary>
    /// Tên chuẩn file XML: {KyHieu}_{Khmshdon}_{SoHoaDon}.xml.
    /// Dùng đồng nhất cho toàn bộ luồng tải/đọc XML.
    /// </summary>
    public static string BuildXmlBaseName(string? kyHieu, ushort? khmshdon, int soHoaDon)
    {
        var sanitizedKyHieu = (kyHieu ?? string.Empty).Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            sanitizedKyHieu = sanitizedKyHieu.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(sanitizedKyHieu))
            sanitizedKyHieu = "KH";
        var formCode = khmshdon ?? DefaultXmlInvoiceFormCode;
        return $"{sanitizedKyHieu}_{formCode}_{soHoaDon}";
    }
}
