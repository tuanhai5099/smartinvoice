namespace SmartInvoice.Application.Services;

/// <summary>
/// Giải captcha từ ảnh. Có thể có preprocess tùy loại trang (hoặc không preprocess cho hóa đơn điện tử).
/// </summary>
public interface ICaptchaSolverService
{
    /// <summary>
    /// Giải captcha từ file ảnh. Preprocess tùy cấu hình (hoặc không nếu nền trắng).
    /// </summary>
    Task<string> SolveFromFileAsync(string imagePath, CancellationToken cancellationToken = default);
    /// <summary>
    /// Giải captcha từ stream ảnh (vd. SVG đã render ra PNG).
    /// </summary>
    Task<string> SolveFromStreamAsync(Stream imageStream, CancellationToken cancellationToken = default);
}
