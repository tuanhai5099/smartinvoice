using Prism.Ioc;
using Prism.Modularity;
using SmartInvoice.Modules.Companies.Services;
using SmartInvoice.Modules.Companies.ViewModels;
using SmartInvoice.Modules.Companies.Views;

namespace SmartInvoice.Modules.Companies;

public class CompaniesModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterForNavigation<CompaniesListView, CompaniesListViewModel>();
        containerRegistry.Register<Views.InvoiceListView>();
        containerRegistry.Register<ViewModels.InvoiceListViewModel>();
        containerRegistry.RegisterSingleton<IInvoiceViewerService, InvoiceViewerService>();
        // ICompanyEditDialogService đăng ký ở Bootstrapper để có sẵn khi CreateShell resolve CompaniesListViewModel
    }

    public void OnInitialized(IContainerProvider containerProvider) { }
}
