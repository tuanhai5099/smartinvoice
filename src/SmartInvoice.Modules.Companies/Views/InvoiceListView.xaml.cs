using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using SmartInvoice.Application.Services;
using SmartInvoice.Modules.Companies.ViewModels;

namespace SmartInvoice.Modules.Companies.Views;

public partial class InvoiceListView : UserControl
{
    private ScrollViewer? _dataGridScrollViewer;

    public InvoiceListView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InvoiceListViewModel vm) return;
        CboLoaiHoaDon.SelectedIndex = (int)vm.FilterLoaiHoaDon;
        FixDatePickerPopupAlignment();
        ApplySortDirectionToHeader();
        // Infinite scroll: khi cuộn xuống cuối DataGrid thì tải thêm
        if (InvoiceDataGrid != null)
        {
            InvoiceDataGrid.Loaded += DataGrid_Loaded;
            if (InvoiceDataGrid.IsLoaded)
                AttachScrollViewer();
        }
    }

    private void DataGrid_Loaded(object? sender, RoutedEventArgs e)
    {
        AttachScrollViewer();
    }

    private void AttachScrollViewer()
    {
        if (InvoiceDataGrid == null || _dataGridScrollViewer != null) return;
        _dataGridScrollViewer = FindVisualChild<ScrollViewer>(InvoiceDataGrid);
        if (_dataGridScrollViewer != null)
            _dataGridScrollViewer.ScrollChanged += DataGridScrollViewer_ScrollChanged;
    }

    private void DataGridScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || DataContext is not InvoiceListViewModel vm || !vm.HasMore || vm.IsBusy)
            return;
        var atBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 30;
        if (atBottom && vm.LoadMoreCommand.CanExecute(null))
            vm.LoadMoreCommand.Execute(null);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                return found;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }
        return null;
    }

    private static IEnumerable<TChild> FindVisualChildren<TChild>(DependencyObject parent) where TChild : DependencyObject
    {
        if (parent == null) yield break;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TChild childOfType)
                yield return childOfType;

            foreach (var descendant in FindVisualChildren<TChild>(child))
                yield return descendant;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_dataGridScrollViewer != null)
        {
            _dataGridScrollViewer.ScrollChanged -= DataGridScrollViewer_ScrollChanged;
            _dataGridScrollViewer = null;
        }
        if (InvoiceDataGrid != null)
            InvoiceDataGrid.Loaded -= DataGrid_Loaded;
    }

    private void ApplySortDirectionToHeader()
    {
        if (DataContext is not InvoiceListViewModel vm || InvoiceDataGrid?.Columns == null) return;

        var direction = vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        foreach (var col in InvoiceDataGrid.Columns)
        {
            col.SortDirection = null;
            if (string.Equals(col.SortMemberPath, vm.SortBy, StringComparison.OrdinalIgnoreCase))
                col.SortDirection = direction;
        }
    }

    private void FixDatePickerPopupAlignment()
    {
        FixPopupForDatePicker(FilterFromDatePicker);
        FixPopupForDatePicker(FilterToDatePicker);
    }

    private static void FixPopupForDatePicker(DatePicker datePicker)
    {
        if (datePicker == null) return;
        datePicker.ApplyTemplate();
        var popup = datePicker.Template?.FindName("PART_Popup", datePicker) as Popup;
        if (popup != null)
        {
            popup.Placement = PlacementMode.Bottom;
            popup.PlacementTarget = datePicker;
            popup.HorizontalOffset = 0;
            popup.VerticalOffset = 2;
        }
    }

    private void CboLoaiHoaDon_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is InvoiceListViewModel vm && sender is ComboBox cbo && cbo.SelectedIndex >= 0 && cbo.SelectedIndex <= 4)
            vm.FilterLoaiHoaDon = (FilterLoaiHoaDonKind)cbo.SelectedIndex;
    }

    private void RbDirection_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InvoiceListViewModel vm) return;
        if (sender == RbMuaVao)
            vm.FilterDirection = FilterDirectionKind.MuaVao;
        else if (sender == RbBanRa)
            vm.FilterDirection = FilterDirectionKind.BanRa;
    }

    private void InvoiceSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not InvoiceListViewModel vm) return;
        vm.RequestApplyFilter();
        e.Handled = true;
    }

    private void InvoiceDataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not InvoiceListViewModel vm) return;
        var path = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        e.Handled = true;
        vm.ApplySortByColumn(path);
        ApplySortDirectionToHeader();
        // Cập nhật icon ▲/▼ sau khi layout để header nhận SortDirection
        Dispatcher.BeginInvoke(new Action(ApplySortDirectionToHeader), DispatcherPriority.Loaded);
    }

    private void BtnThang_Click(object sender, RoutedEventArgs e)
    {
        PopupThang.IsOpen = true;
    }

    private void MonthPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PopupThang.IsOpen = false;
    }

    private void BtnQuy_Click(object sender, RoutedEventArgs e)
    {
        PopupQuy.IsOpen = true;
    }

    private void QuarterPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PopupQuy.IsOpen = false;
    }

    private void BtnNam_Click(object sender, RoutedEventArgs e)
    {
        PopupNam.IsOpen = true;
    }

    private void YearPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PopupNam.IsOpen = false;
    }

    private void InvoiceDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not InvoiceListViewModel vm)
            return;

        if (InvoiceDataGrid?.SelectedItem is not InvoiceDisplayDto inv)
            return;

        vm.ActionMenuInvoice = inv;
        if (vm.ViewInvoiceForRowCommand.CanExecute(null))
            vm.ViewInvoiceForRowCommand.Execute(null);
    }

    private void InvoiceDataGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not InvoiceListViewModel vm) return;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row == null || row.DataContext is not InvoiceDisplayDto inv) return;
        vm.OpenRowActionMenuCommand.Execute(inv);
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T found) return found;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
