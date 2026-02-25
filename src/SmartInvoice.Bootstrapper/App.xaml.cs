using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace SmartInvoice.Bootstrapper;

public partial class App : System.Windows.Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Initialize();
        AppLog.AppLogger.LogInformation("=== Ứng dụng khởi động ===");

        // Thiết lập culture toàn app sang tiếng Việt để DatePicker/Calendar hiển thị ngày tháng bằng tiếng Việt.
        var culture = new CultureInfo("vi-VN");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        base.OnStartup(e);

        try
        {
            AppLog.AppLogger.LogDebug("Chạy Bootstrapper...");
            var bootstrapper = new AppBootstrapper();
            bootstrapper.Run();
            AppLog.AppLogger.LogInformation("Khởi tạo xong, cửa sổ chính đã hiển thị.");
        }
        catch (Exception ex)
        {
            AppLog.LogException(ex, "Startup/Bootstrapper.Run");
            AppLog.AppLogger.LogError(ex, "Lỗi khi khởi động: {Message}", ex.Message);
            MessageBox.Show(
                "Lỗi khởi động ứng dụng. Chi tiết đã ghi vào file log.\n\n" + ex.Message,
                "Lỗi",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.LogException(e.Exception, "Dispatcher");
        AppLog.AppLogger.LogError(e.Exception, "DispatcherUnhandledException: {Message}", e.Exception.Message);
        MessageBox.Show(
            "Đã xảy ra lỗi. Chi tiết đã ghi vào file log.\n\n" + e.Exception.Message,
            "Lỗi",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLog.LogException(ex, "AppDomain.UnhandledException");
            AppLog.AppLogger.LogCritical(ex, "UnhandledException (IsTerminating={IsTerminating}): {Message}", e.IsTerminating, ex.Message);
        }
    }
}
