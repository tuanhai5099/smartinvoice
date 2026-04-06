using SmartInvoice.Application.Services;

namespace SmartInvoice.Modules.Companies.Services;

/// <summary>Chuyển tiến trình job bulk sang UI thread cho popup danh sách hóa đơn.</summary>
public sealed class BackgroundJobLiveProgressNotifier : IBackgroundJobLiveProgressNotifier
{
    public static event Action<Guid, DownloadXmlProgress>? BulkXmlProgress;

    public static event Action<BulkPdfItemProgress>? BulkPdfItem;

    public void ReportBulkXmlProgress(Guid jobId, DownloadXmlProgress progress)
    {
        RaiseOnUi(() => BulkXmlProgress?.Invoke(jobId, progress));
    }

    public void ReportBulkPdfItem(BulkPdfItemProgress item)
    {
        RaiseOnUi(() => BulkPdfItem?.Invoke(item));
    }

    private static void RaiseOnUi(Action action)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher.CheckAccess() == true)
                action();
            else
                app?.Dispatcher.BeginInvoke(action);
        }
        catch
        {
            // UI chưa sẵn sàng
        }
    }
}
