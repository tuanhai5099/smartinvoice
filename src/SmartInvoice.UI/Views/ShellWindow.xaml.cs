using System.Windows;
using Wpf.Ui.Controls;

namespace SmartInvoice.UI.Views;

public partial class ShellWindow : FluentWindow
{
    public ShellWindow()
    {
        InitializeComponent();
    }

    public object? MainContentContent
    {
        set => MainContent.Content = value;
    }
}
