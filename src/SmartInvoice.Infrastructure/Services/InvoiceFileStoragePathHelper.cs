namespace SmartInvoice.Infrastructure.Services;

/// <summary>
/// Cấu trúc thư mục lưu file theo công ty và tháng/năm: Mã công ty → Tháng_Năm → XML/PDF.
/// Dùng chung cho tải XML, PDF và các file theo từng hóa đơn để dễ quản lý.
/// </summary>
public static class InvoiceFileStoragePathHelper
{
    /// <summary>Thư mục gốc ứng dụng (Documents\SmartInvoice).</summary>
    public static string GetAppRootPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartInvoice");

    /// <summary>
    /// Thư mục gốc theo công ty: Documents\SmartInvoice\{mã công ty}.
    /// Dưới đó tạo thư mục tháng_năm (yyyy_MM) rồi lưu XML/PDF.
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
}
