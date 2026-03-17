using System.Windows;
using SmartInvoice.Application.DTOs;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.ViewModels;
using SmartInvoice.Modules.Companies.Views;

namespace SmartInvoice.Modules.Companies.Services;

public sealed class CompanyEditDialogService : ICompanyEditDialogService
{
    private readonly ICompanyAppService _companyService;
    private readonly IConfirmationService _confirmationService;

    public CompanyEditDialogService(ICompanyAppService companyService, IConfirmationService confirmationService)
    {
        _companyService = companyService;
        _confirmationService = confirmationService;
    }

    public Task<CompanyEditDialogResult> ShowAddAsync(CancellationToken cancellationToken = default)
    {
        return ShowCoreAsync(isAdd: true, null, cancellationToken);
    }

    public async Task<CompanyEditDialogResult> ShowEditAsync(CompanyDto company, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(company);
        var forEdit = await _companyService.GetByIdForEditAsync(company.Id, cancellationToken).ConfigureAwait(false);
        if (forEdit == null)
            return new CompanyEditDialogResult(false, "Công ty không tồn tại.");
        return await ShowCoreAsync(isAdd: false, forEdit, cancellationToken).ConfigureAwait(false);
    }

    private Task<CompanyEditDialogResult> ShowCoreAsync(bool isAdd, CompanyDto? company, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<CompanyEditDialogResult>();
        var app = System.Windows.Application.Current;
        var owner = app.MainWindow;

        CompanyEditWindow? dialogWindow = null;
        void CloseCallback(bool success, string? message)
        {
            tcs.TrySetResult(new CompanyEditDialogResult(success, message));
            if (dialogWindow != null)
                app.Dispatcher.BeginInvoke(() => dialogWindow.Close());
        }

        var vm = new CompanyEditViewModel(_companyService, _confirmationService, CloseCallback);
        if (isAdd)
            vm.SetAddMode();
        else
            vm.SetEditMode(company!);

        dialogWindow = new CompanyEditWindow
        {
            Owner = owner,
            DataContext = vm
        };

        dialogWindow.Closed += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(new CompanyEditDialogResult(false, null));
        };

        _ = app.Dispatcher.InvokeAsync(() => dialogWindow.ShowDialog());
        return tcs.Task;
    }
}
