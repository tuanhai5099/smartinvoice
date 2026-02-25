using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmartInvoice.Modules.Companies.Views;

/// <summary>Cửa sổ popup thông báo góc phải màn hình (toast). Có tự đóng sau vài giây; user cũng có thể click để tắt sớm.</summary>
public partial class ToastPopupWindow : Window
{
    /// <summary>Thời gian hiển thị trước khi tự đóng (giây).</summary>
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
            _autoCloseTimer.Stop();
            Close();
        };
        _autoCloseTimer.Start();
    }

    private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _autoCloseTimer?.Stop();
        Close();
    }
}
