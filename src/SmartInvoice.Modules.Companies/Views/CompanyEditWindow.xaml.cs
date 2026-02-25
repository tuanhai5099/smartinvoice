using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SmartInvoice.Modules.Companies.ViewModels;
using Wpf.Ui.Controls;

namespace SmartInvoice.Modules.Companies.Views;

public partial class CompanyEditWindow : FluentWindow
{
    public CompanyEditWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncPasswordFromViewModel();
    }

    private void PasswordHidden_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox pb && DataContext is CompanyEditViewModel vm)
            vm.Password = pb.Password;
    }

    /// <summary>Đồng bộ PasswordBox với ViewModel (khi mở form sửa hoặc khi chuyển từ hiện sang ẩn).</summary>
    private void SyncPasswordFromViewModel()
    {
        if (DataContext is not CompanyEditViewModel vm) return;
        if (PasswordHidden.Password != vm.Password)
            PasswordHidden.Password = vm.Password;

        ((INotifyPropertyChanged)vm).PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CompanyEditViewModel.IsPasswordVisible) && !vm.IsPasswordVisible)
            {
                if (PasswordHidden.Password != vm.Password)
                    PasswordHidden.Password = vm.Password;
            }
        };
    }
}
