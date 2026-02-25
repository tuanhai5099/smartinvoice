using Prism.Ioc;
using SmartInvoice.Application.Services;
using SmartInvoice.UI.Views;

namespace SmartInvoice.Bootstrapper;

public class AppNavigationService : INavigationService
{
    private ShellWindow? _shell;
    private readonly IContainerProvider _container;

    public AppNavigationService(IContainerProvider container)
    {
        _container = container;
    }

    public void Initialize(ShellWindow shell)
    {
        _shell = shell;
    }

    public void NavigateToCompanies()
    {
        if (_shell == null) return;
        var view = _container.Resolve<SmartInvoice.Modules.Companies.Views.CompaniesListView>();
        view.DataContext = _container.Resolve<SmartInvoice.Modules.Companies.ViewModels.CompaniesListViewModel>();
        _shell.MainContentContent = view;
    }

    public void NavigateToInvoiceList(Guid companyId)
    {
        if (_shell == null) return;
        var vm = _container.Resolve<SmartInvoice.Modules.Companies.ViewModels.InvoiceListViewModel>();
        vm.SetCompanyId(companyId);
        var view = _container.Resolve<SmartInvoice.Modules.Companies.Views.InvoiceListView>();
        view.DataContext = vm;
        _shell.MainContentContent = view;
    }
}
