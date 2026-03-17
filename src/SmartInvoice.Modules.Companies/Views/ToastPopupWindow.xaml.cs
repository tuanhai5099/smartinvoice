using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmartInvoice.Modules.Companies.Views;

/// <summary>Cửa sổ popup thông báo góc phải màn hình (toast). Có nút X để đóng; click vào thông báo sẽ mở Smart Invoice lên trước.</summary>
public partial class ToastPopupWindow : Window
{
    private const int AutoCloseSeconds = 10;
    private DispatcherTimer? _autoCloseTimer;

    public ToastPopupWindow(string title, string body, bool isError)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;

        if (isError)
        {
            IconEllipse.Fill = new SolidColorBrush(Color.FromRgb(0xc6, 0x28, 0x28));
            IconPath.Data = Geometry.Parse("M 6 6 L 18 18 M 18 6 L 6 18");
        }
        else
        {
            IconEllipse.Fill = new SolidColorBrush(Color.FromRgb(0x2e, 0x7d, 0x32));
            IconPath.Data = Geometry.Parse("M 4 12 L 10 18 L 20 6");
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        const int margin = 16;
        Left = workArea.Right - Width - margin;
        Top = workArea.Bottom - Height - margin;

        _autoCloseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(AutoCloseSeconds)
        };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer?.Stop();
            Close();
        };
        _autoCloseTimer.Start();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsCloseButtonOrChild(e.OriginalSource))
            return;
        ActivateMainWindowAndClose();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _autoCloseTimer?.Stop();
        Close();
    }

    private static bool IsCloseButtonOrChild(object? source)
    {
        if (source is not DependencyObject dob) return false;
        var current = dob;
        while (current != null)
        {
            if (current is System.Windows.Controls.Button b && b.Name == "CloseButton")
                return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void ActivateMainWindowAndClose()
    {
        _autoCloseTimer?.Stop();
        var main = System.Windows.Application.Current?.MainWindow;
        if (main != null)
        {
            if (main.WindowState == WindowState.Minimized)
                main.WindowState = WindowState.Normal;
            main.Activate();
        }
        Close();
    }
}
