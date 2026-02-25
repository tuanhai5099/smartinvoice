using System.Windows;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.ViewModels;
using SmartInvoice.Modules.Companies.Views;

namespace SmartInvoice.Modules.Companies.Services;

public sealed class BackgroundJobDialogService : IBackgroundJobDialogService
{
    private readonly IBackgroundJobService _backgroundJobService;
    private readonly ICompanyAppService _companyService;

    public BackgroundJobDialogService(IBackgroundJobService backgroundJobService, ICompanyAppService companyService)
    {
        _backgroundJobService = backgroundJobService;
        _companyService = companyService;
    }

    public Task ShowCreateAsync(Guid? defaultCompanyId, bool? defaultIsSold, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>();
        var app = System.Windows.Application.Current;
        var owner = app.MainWindow;

        BackgroundJobCreateWindow? dialogWindow = null;

        void CloseCallback()
        {
            tcs.TrySetResult(true);
            if (dialogWindow != null)
                app.Dispatcher.BeginInvoke(() => dialogWindow.Close());
        }

        var vm = new BackgroundJobCreateViewModel(_backgroundJobService, _companyService, this, CloseCallback, defaultCompanyId, defaultIsSold);

        dialogWindow = new BackgroundJobCreateWindow
        {
            Owner = owner,
            DataContext = vm
        };

        dialogWindow.Closed += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(false);
        };

        _ = app.Dispatcher.InvokeAsync(() => dialogWindow.ShowDialog());
        return tcs.Task;
    }

    public void ShowManagement()
    {
        var app = System.Windows.Application.Current;
        var window = new BackgroundJobListWindow
        {
            Owner = app.MainWindow,
            DataContext = new BackgroundJobListViewModel(_backgroundJobService)
        };
        window.ShowDialog();
    }

    public void ShowToast(string title, string message)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var toast = new Views.ToastPopupWindow(title, message, isError: false);
            toast.Show();
        });
    }
}

