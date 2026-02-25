using SmartInvoice.Captcha.Preprocessing;
using SmartInvoice.Captcha.Prediction;

namespace SmartInvoice.Captcha.Vnpt;

/// <summary>
/// Tiện ích giải captcha cho cổng VNPT merchant:
/// - Luôn preprocess Contrast (không dùng các hiệu ứng khác).
/// - Ẩn chi tiết OpenCV/PaddleOCR khỏi caller.
/// </summary>
public static class VnptCaptchaHelper
{
    public static string SolveFromFileWithContrast(string imagePath)
    {
        var options = new PreprocessOptions
        {
            Contrast = true
        };

        using var solver = new CaptchaSolver(options);
        solver.Initialize();
        return solver.Predict(imagePath);
    }
}

