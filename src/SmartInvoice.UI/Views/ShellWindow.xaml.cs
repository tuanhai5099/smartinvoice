using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace SmartInvoice.UI.Views;

public partial class ShellWindow : FluentWindow
{
    public ShellWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Đặt icon từ file .ico cạnh exe để taskbar và title bar hiển thị đúng (tránh pack URI / cache).
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var icoPath = Path.Combine(baseDir, "SmartInvoice.ico");
        if (File.Exists(icoPath))
        {
            try
            {
                var decoder = new IconBitmapDecoder(
                    new Uri(icoPath, UriKind.Absolute),
                    BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    var frame = decoder.Frames[0];
                    frame.Freeze();
                    Icon = frame;
                }
            }
            catch
            {
                // Bỏ qua nếu không load được
            }
        }
    }

    public object? MainContentContent
    {
        set => MainContent.Content = value;
    }
}
