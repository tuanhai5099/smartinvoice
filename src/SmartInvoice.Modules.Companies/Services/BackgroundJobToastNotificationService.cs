using SmartInvoice.Application.Services;

namespace SmartInvoice.Modules.Companies.Services;

/// <summary>Thông báo job hoàn thành: cửa sổ popup góc phải (không tự đóng — user bấm hoặc click để tắt, giống thông báo hệ thống).</summary>
public sealed class BackgroundJobToastNotificationService : IBackgroundJobCompletedNotifier
{
    /// <summary>Sự kiện bắn ra mỗi khi có job hoàn thành (Completed/Failed) để màn hình khác có thể cập nhật trạng thái.</summary>
    public static event Action<BackgroundJobDto>? JobCompleted;

    public void Notify(BackgroundJobDto job)
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                // Bắn event cho các ViewModel đang nghe (vd: danh sách hóa đơn) để tự cập nhật trạng thái.
                JobCompleted?.Invoke(job);

                var (title, body) = BuildTitleAndBody(job);
                var isFailed = job.Status == Core.Domain.BackgroundJobStatus.Failed;
                var toast = new Views.ToastPopupWindow(title, body, isFailed);
                toast.Show();
            });
        }
        catch (Exception)
        {
            // Bỏ qua nếu UI chưa sẵn sàng
        }
    }

    private static (string Title, string Body) BuildTitleAndBody(BackgroundJobDto job)
    {
        var isFailed = job.Status == Core.Domain.BackgroundJobStatus.Failed;
        var typeLabel = job.Type switch
        {
            Core.Domain.BackgroundJobType.ExportExcel => "Xuất Excel",
            Core.Domain.BackgroundJobType.DownloadXmlBulk => "Tải XML hàng loạt",
            Core.Domain.BackgroundJobType.DownloadPdfBulk => "Tải PDF hàng loạt",
            _ => "Đồng bộ hóa đơn"
        };
        var companyPart = string.IsNullOrWhiteSpace(job.CompanyName) ? "" : $" — {job.CompanyName.Trim()}";
        var title = isFailed ? $"{typeLabel}{companyPart} — Thất bại" : $"{typeLabel}{companyPart} — Xong";

        // Ưu tiên hiển thị mô tả job giống cột \"Mô tả\" ở màn hình quản lý
        var description = string.IsNullOrWhiteSpace(job.Description) ? "" : job.Description.Trim();

        // Phần chi tiết kết quả / lỗi / đường dẫn file ZIP (hiển thị full path để user biết thư mục chứa file)
        string detail;
        if (!string.IsNullOrWhiteSpace(job.LastError))
            detail = job.LastError;
        else if (!string.IsNullOrWhiteSpace(job.ResultPath))
            detail = $"File ZIP: {job.ResultPath}";
        else
            detail = job.ProgressDisplayText;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(description))
            parts.Add(description);
        if (!string.IsNullOrWhiteSpace(detail) && !string.Equals(detail, description, StringComparison.Ordinal))
            parts.Add(detail);

        var body = string.Join(". ", parts);
        if (body.Length > 200)
            body = body[..197] + "...";
        return (title, body);
    }
}
