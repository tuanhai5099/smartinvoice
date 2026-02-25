using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SmartInvoice.Modules.Companies.Views;

public partial class InvoiceHtmlViewerWindow : Window
{
    private string? _filePath;
    private string? _htmlContent;
    private string? _navigateUrl;
    private string? _autoFillSearchCode;
    private string? _companyCode;
    private string? _companyName;
    private object? _invoice;
    private Func<Task<(string? printPath, string? error)>>? _getPrintPathAsync;
    private bool _isShowingPrintTemplate;

    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x00020000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public InvoiceHtmlViewerWindow()
    {
        InitializeComponent();
        SourceInitialized += InvoiceHtmlViewerWindow_SourceInitialized;
        Loaded += InvoiceHtmlViewerWindow_Loaded;
    }

    private void InvoiceHtmlViewerWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        if (hwndSource?.Handle == IntPtr.Zero) return;
        var hwnd = hwndSource!.Handle;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style & ~WS_MINIMIZEBOX);
    }

    /// <param name="companyCode">Mã công ty / tên gọi nhỏ (dùng cho tên thư mục lưu PDF).</param>
    public void SetContext(string? companyCode, string? companyName, object? invoice)
    {
        _companyCode = companyCode?.Trim();
        _companyName = companyName?.Trim();
        _invoice = invoice;
    }

    /// <summary>Khi set: In / Lưu PDF dùng template in (fill từ API) thay vì nội dung đang xem.</summary>
    public void SetPrintProvider(Func<Task<(string? printPath, string? error)>>? getPrintPathAsync)
    {
        _getPrintPathAsync = getPrintPathAsync;
    }

    public void LoadFile(string filePath)
    {
        _filePath = filePath;
        _htmlContent = null;
        PathText.Text = filePath;
        // Nếu đang xem file PDF đã sinh sẵn thì ẩn nút In / Lưu PDF
        var isPdf = string.Equals(System.IO.Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);
        PrintButton.Visibility = isPdf ? Visibility.Collapsed : Visibility.Visible;
        SavePdfButton.Visibility = isPdf ? Visibility.Collapsed : Visibility.Visible;
        NavigateIfReady();
    }

    /// <summary>Hiển thị nội dung HTML đã sinh (từ API detail + template), không dùng file.</summary>
    public void LoadContent(string htmlContent)
    {
        _filePath = null;
        _htmlContent = htmlContent ?? "";
        PathText.Text = "(Nội dung từ API chi tiết)";
        // Nội dung HTML từ API detail: luôn cho phép In / Lưu PDF
        PrintButton.Visibility = Visibility.Visible;
        SavePdfButton.Visibility = Visibility.Visible;
        NavigateIfReady();
    }

    /// <summary>
    /// Điều hướng tới một URL tra cứu (không dùng file), và nếu có searchCode thì sau khi load xong sẽ tự điền vào ô txtMaTraCuu.
    /// Dùng cho các trang như HTInvoice.
    /// </summary>
    public void LoadUrlWithAutoFill(string url, string? searchCode)
    {
        _filePath = null;
        _htmlContent = null;
        _navigateUrl = url;
        _autoFillSearchCode = searchCode;
        PathText.Text = url;
        // Trang tra cứu: ẩn nút In / Lưu PDF, chỉ dùng để người dùng thao tác tay.
        PrintButton.Visibility = Visibility.Collapsed;
        SavePdfButton.Visibility = Visibility.Collapsed;
        NavigateIfReady();
    }

    private async void InvoiceHtmlViewerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await EnsureWebViewReadyAsync();
        NavigateIfReady();
    }

    private async Task EnsureWebViewReadyAsync()
    {
        try
        {
            if (WebView.CoreWebView2 == null)
                await WebView.EnsureCoreWebView2Async();
        }
        catch
        {
            // ignore init errors; sẽ hiển thị message khi navigate/print
        }
    }

    /// <summary>Điều hướng tới file (template in) rồi chờ load xong. Dùng CoreWebView2.Navigate với file URI đúng chuẩn. Trả về true nếu load thành công.</summary>
    private async Task<bool> NavigateToFileAndWaitAsync(string path)
    {
        if (WebView.CoreWebView2 == null) return false;
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(path);
        var fileUri = new Uri(path);
        var pathForUri = fileUri.AbsoluteUri;
        var tcs = new TaskCompletionSource<bool>();
        void OnCompleted(object? o, CoreWebView2NavigationCompletedEventArgs args)
        {
            WebView.CoreWebView2.NavigationCompleted -= OnCompleted;
            tcs.TrySetResult(args.IsSuccess);
        }
        WebView.CoreWebView2.NavigationCompleted += OnCompleted;
        try
        {
            WebView.CoreWebView2.Navigate(pathForUri);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(15000)).ConfigureAwait(true);
            return completed == tcs.Task && tcs.Task.Result;
        }
        finally
        {
            WebView.CoreWebView2.NavigationCompleted -= OnCompleted;
        }
    }

    private void NavigateIfReady()
    {
        if (WebView.CoreWebView2 == null)
            return;

        if (!string.IsNullOrEmpty(_navigateUrl))
        {
            try
            {
                WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompletedForAutoFill;
            }
            catch
            {
                // ignore
            }
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompletedForAutoFill;
            WebView.Source = new Uri(_navigateUrl);
            return;
        }

        if (!string.IsNullOrEmpty(_htmlContent))
        {
            WebView.CoreWebView2.NavigateToString(_htmlContent);
            return;
        }

        if (string.IsNullOrWhiteSpace(_filePath))
            return;

        if (!File.Exists(_filePath))
        {
            WebView.CoreWebView2.NavigateToString("<p>File không tồn tại: " + System.Net.WebUtility.HtmlEncode(_filePath) + "</p>");
            return;
        }

        var pathForUri = "file:///" + _filePath.Replace("\\", "/").TrimStart('/');
        WebView.Source = new Uri(pathForUri);
    }

    private async void OnNavigationCompletedForAutoFill(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompletedForAutoFill;
        }
        catch
        {
            // ignore detach errors
        }

        if (!e.IsSuccess || string.IsNullOrWhiteSpace(_autoFillSearchCode))
            return;

        try
        {
            // Chờ thêm một chút để DOM/JS trang tra cứu khởi tạo xong.
            await Task.Delay(800).ConfigureAwait(true);

            var script = """
                (function(code) {
                    try {
                        var input = document.querySelector('input#txtMaTraCuu, input[name="txtMaTraCuu"]');
                        if (!input) return;
                        input.focus();
                        input.value = code;
                        var evtInput = new Event('input', { bubbles: true });
                        var evtChange = new Event('change', { bubbles: true });
                        input.dispatchEvent(evtInput);
                        input.dispatchEvent(evtChange);
                    } catch (e) {
                        // ignore
                    }
                })
                """;

            var encoded = System.Text.Json.JsonSerializer.Serialize(_autoFillSearchCode);
            await WebView.CoreWebView2.ExecuteScriptAsync($"{script}({encoded});").ConfigureAwait(false);
        }
        catch
        {
            // best-effort auto-fill, không chặn người dùng nếu có lỗi
        }
    }

    private async void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureWebViewReadyAsync();
            if (WebView.CoreWebView2 == null)
            {
                MessageBox.Show(this, "Không khởi tạo được trình xem hóa đơn.", "In hóa đơn", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var (path, err) = await UsePrintTemplateOrCurrentAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(err))
            {
                MessageBox.Show(this, err, "In hóa đơn", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (path != null)
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, "File template in không tồn tại.", "In hóa đơn", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var loaded = await NavigateToFileAndWaitAsync(path).ConfigureAwait(true);
                if (!loaded)
                {
                    MessageBox.Show(this, "Không tải được trang in. Vui lòng thử lại.", "In hóa đơn", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                await Task.Delay(400).ConfigureAwait(true);
            }
            WebView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            if (path != null)
            {
                _isShowingPrintTemplate = true;
                BackToViewButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không thể in hóa đơn: " + ex.Message, "In hóa đơn",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackToViewButton_Click(object sender, RoutedEventArgs e)
    {
        _isShowingPrintTemplate = false;
        BackToViewButton.Visibility = Visibility.Collapsed;
        NavigateIfReady();
    }

    private async void SavePdfButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureWebViewReadyAsync();
            if (WebView.CoreWebView2 == null)
            {
                MessageBox.Show(this, "Không khởi tạo được trình xem hóa đơn.", "Lưu PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var (printPath, printError) = await UsePrintTemplateOrCurrentAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(printError))
            {
                MessageBox.Show(this, printError, "Lưu PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (printPath != null)
            {
                if (!File.Exists(printPath))
                {
                    MessageBox.Show(this, "File template in không tồn tại.", "Lưu PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var loaded = await NavigateToFileAndWaitAsync(printPath).ConfigureAwait(true);
                if (!loaded)
                {
                    MessageBox.Show(this, "Không tải được trang in để xuất PDF. Vui lòng thử lại.", "Lưu PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var smartInvoiceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartInvoice");
            var companyFolderName = SanitizeFileName(
                !string.IsNullOrWhiteSpace(_companyCode) ? _companyCode! :
                !string.IsNullOrWhiteSpace(_companyName) ? _companyName! : "CongTy");
            var companyRoot = Path.Combine(smartInvoiceRoot, companyFolderName);

            dynamic? inv = _invoice;
            DateTime date =
                (inv?.NgayLap is DateTime nl) ? nl :
                (inv?.NgayKy is DateTime nk) ? nk :
                DateTime.Now;
            var monthFolderName = date.ToString("yyyy_MM");

            var pdfFolder = Path.Combine(companyRoot, "Pdf", monthFolderName);
            Directory.CreateDirectory(pdfFolder);

            string kh = inv?.KyHieu != null ? (string)inv.KyHieu : string.Empty;
            string khm = inv?.Khmshdon != null ? Convert.ToString(inv.Khmshdon) ?? string.Empty : string.Empty;
            string so = inv?.SoHoaDon != null ? Convert.ToString(inv.SoHoaDon) ?? string.Empty : string.Empty;
            var fileNameRaw = $"{kh}_{khm}_{so}.pdf";
            var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(fileNameRaw) ? "Invoice.pdf" : fileNameRaw);
            var pdfPath = Path.Combine(pdfFolder, fileName);

            await InjectPrintBackgroundStyleAsync();

            var settings = WebView.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            await WebView.CoreWebView2.PrintToPdfAsync(pdfPath, settings);

            if (printPath != null)
                NavigateIfReady();

            new SavedPathDialog(pdfPath) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Lưu PDF lỗi: " + ex.Message, "Lưu PDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Nếu có provider: gọi lấy đường dẫn template in; trả về (path, error). path null = dùng nội dung hiện tại.</summary>
    private async Task<(string? path, string? error)> UsePrintTemplateOrCurrentAsync()
    {
        if (_getPrintPathAsync == null)
            return (null, null);
        return await _getPrintPathAsync().ConfigureAwait(false);
    }

    /// <summary>Chèn CSS ép in màu nền / hình nền khi xuất PDF (print-color-adjust: exact).</summary>
    private async Task InjectPrintBackgroundStyleAsync()
    {
        try
        {
            const string script = """
                (function() {
                    var style = document.getElementById('smartinvoice-print-bg') || document.createElement('style');
                    style.id = 'smartinvoice-print-bg';
                    style.textContent = '*, *::before, *::after { -webkit-print-color-adjust: exact !important; print-color-adjust: exact !important; }';
                    if (!style.parentNode) document.head.appendChild(style);
                })();
                """;
            await WebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { /* best-effort */ }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
