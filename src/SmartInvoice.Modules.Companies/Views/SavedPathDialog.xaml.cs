using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SmartInvoice.Modules.Companies.Views;

public partial class SavedPathDialog : Window
{
    public SavedPathDialog(string filePath)
    {
        InitializeComponent();
        PathBox.Text = filePath ?? "";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        try
        {
            Process.Start("explorer.exe", folder);
        }
        catch { /* ignore */ }
    }
}
