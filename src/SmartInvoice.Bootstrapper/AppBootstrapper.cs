using System.Net.Http;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Repositories;
using SmartInvoice.Infrastructure.Captcha;
using SmartInvoice.Infrastructure.HoaDonDienTu;
using SmartInvoice.Infrastructure.Persistence;
using SmartInvoice.Infrastructure.Services;
using SmartInvoice.Infrastructure.Services.Pdf;
using SmartInvoice.InvoicePdfFetchers;
using SmartInvoice.Modules.Companies;
using SmartInvoice.Modules.Companies.Services;
using SmartInvoice.UI.Views;
using SmartInvoice.Captcha.Preprocessing;

namespace SmartInvoice.Bootstrapper;

public class AppBootstrapper : PrismBootstrapper
{
    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        moduleCatalog.AddModule<CompaniesModule>();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        AppLog.AppLogger.LogDebug("RegisterTypes: bắt đầu đăng ký dịch vụ...");
        var connectionString = "Data Source=smartinvoice.db";
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite(connectionString);
        containerRegistry.RegisterInstance(optionsBuilder.Options);

        containerRegistry.Register<AppDbContext>();
        containerRegistry.Register<IUnitOfWork, UnitOfWork>();
        containerRegistry.Register<ICompanyAppService, CompanyAppService>();
        containerRegistry.Register<IInvoiceSyncService, InvoiceSyncService>();
        containerRegistry.Register<IInvoiceDetailViewService, InvoiceDetailViewService>();
        containerRegistry.Register<IExcelExportService, ExcelExportService>();
        containerRegistry.RegisterSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
        containerRegistry.RegisterSingleton<IBackgroundJobService, BackgroundJobService>();
        containerRegistry.RegisterSingleton<IBackgroundJobDialogService, BackgroundJobDialogService>();
        containerRegistry.RegisterSingleton<SmartInvoice.Application.Services.IBackgroundJobCompletedNotifier, SmartInvoice.Modules.Companies.Services.BackgroundJobToastNotificationService>();

        containerRegistry.RegisterSingleton<ILoggerFactory>(_ => AppLog.LoggerFactory);
        containerRegistry.RegisterSingleton<HttpClient>();
        containerRegistry.Register<IHoaDonDienTuApiClient, HoaDonDienTuApiClient>();
        containerRegistry.RegisterSingleton<ICaptchaSolverService, CaptchaSolverService>();
        // Cấu hình mặc định cho captcha (dùng chung): để nguyên như cũ cho các fetcher khác, VNPT merchant sẽ tự xử lý riêng nếu cần.
        containerRegistry.RegisterInstance(PreprocessOptions.None);

        // PDF theo nhà cung cấp (tvandnkntt): Strategy + Registry, đầu vào payload JSON đầy đủ
        containerRegistry.Register<IInvoicePdfFallbackFetcher, FallbackInvoicePdfFetcher>();
        // DryIoc mặc định Replace khi đăng ký cùng interface → dùng provider để Registry nhận đủ fetchers
        containerRegistry.Register<EhoadonInvoicePdfFetcher>();
        containerRegistry.Register<FastInvoicePdfFetcher>();
        containerRegistry.Register<MinvoiceInvoicePdfFetcher>();
        containerRegistry.Register<SmartsignInvoicePdfFetcher>();
        containerRegistry.Register<VininvoiceInvoicePdfFetcher>();
        containerRegistry.Register<IhoadonInvoicePdfFetcher>();
        containerRegistry.Register<MeinvoiceInvoicePdfFetcher>();
        containerRegistry.Register<EasyInvoicePdfFetcher>();
        containerRegistry.Register<ViettelInvoicePdfFetcher>();
        containerRegistry.Register<VdsgInvoicePdfFetcher>();
        containerRegistry.Register<WinInvoicePdfFetcher>();
        containerRegistry.Register<WinCommerceInvoicePdfFetcher>();
        containerRegistry.Register<EinvoiceInvoicePdfFetcher>();
        containerRegistry.Register<MerchantVnptInvoiceFetcher>();
        containerRegistry.Register<SesGroupInvoicePdfFetcher>();
        containerRegistry.Register<HtInvoiceInvoicePdfFetcher>();
        containerRegistry.Register<InvoicePdfFetcherSkeleton>();
        containerRegistry.Register<IKeyedInvoicePdfFetcherProvider, KeyedInvoicePdfFetcherProvider>();
        containerRegistry.Register<IInvoicePdfFetcherRegistry, InvoicePdfFetcherRegistry>();
        // Gợi ý tra cứu (link, mã tra cứu) theo nhà cung cấp – tách biệt khỏi fetcher PDF để có thể chạy ở server.
        containerRegistry.RegisterSingleton<IInvoiceLookupProviderRegistry, InvoiceLookupProviderRegistry>();
        containerRegistry.Register<IInvoicePdfService, InvoicePdfService>();

        containerRegistry.RegisterSingleton<ILoginAttemptTracker, LoginAttemptTracker>();
        containerRegistry.RegisterSingleton<INavigationService, AppNavigationService>();
        containerRegistry.RegisterSingleton<IConfirmationService, ConfirmationDialogService>();
        containerRegistry.RegisterSingleton<IApplicationShutdownService, ApplicationShutdownService>();
        containerRegistry.RegisterSingleton<ICompanyEditDialogService, CompanyEditDialogService>();
    }

    protected override Window CreateShell()
    {
        using (var db = Container.Resolve<AppDbContext>())
        {
            db.Database.EnsureCreated();
            AppDbContextSchemaMigrator.Migrate(db);
        }
        var shell = Container.Resolve<ShellWindow>();
        var nav = (AppNavigationService)Container.Resolve<INavigationService>();
        nav.Initialize(shell);
        var view = Container.Resolve<SmartInvoice.Modules.Companies.Views.CompaniesListView>();
        view.DataContext = Container.Resolve<SmartInvoice.Modules.Companies.ViewModels.CompaniesListViewModel>();
        shell.MainContentContent = view;
        System.Windows.Application.Current.MainWindow = shell;
        shell.Show();
        AppLog.AppLogger.LogDebug("CreateShell: xong.");
        return shell;
    }
}
