using System.Windows;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Modules.Companies.Services;

/// <summary>Thông báo job hoàn thành: cửa sổ popup góc phải (không tự đóng — user bấm hoặc click để tắt, giống thông báo hệ thống).</summary>
public sealed class BackgroundJobToastNotificationService : IBackgroundJobCompletedNotifier
{
    public void Notify(BackgroundJobDto job)
    {
        try
        {
            var (title, body) = BuildTitleAndBody(job);
            var isFailed = job.Status == Core.Domain.BackgroundJobStatus.Failed;
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
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
        var typeLabel = job.Type == Core.Domain.BackgroundJobType.ExportExcel ? "Xuất Excel" : "Đồng bộ hóa đơn";
        var title = isFailed ? $"{typeLabel} — Thất bại" : $"{typeLabel} — Xong";
        var body = job.ProgressDisplayText;
        if (!string.IsNullOrWhiteSpace(job.LastError))
            body = job.LastError;
        else if (!string.IsNullOrWhiteSpace(job.ResultPath))
            body = $"File: {System.IO.Path.GetFileName(job.ResultPath)}";
        if (body.Length > 200)
            body = body[..197] + "...";
        return (title, body);
    }
}
