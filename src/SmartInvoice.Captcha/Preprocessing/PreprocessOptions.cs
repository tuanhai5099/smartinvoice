namespace SmartInvoice.Captcha.Preprocessing;

/// <summary>
/// Tùy chọn tiền xử lý ảnh. Với captcha hóa đơn điện tử (nền trắng) không cần bật gì.
/// Các trang khác có thể bật Contrast, Denoise, Binarize, RemoveLines hoặc preset GreenStriped/GrayLines.
/// </summary>
public class PreprocessOptions
{
    public bool Contrast { get; set; }
    public bool Denoise { get; set; }
    public bool Binarize { get; set; }
    public bool RemoveLines { get; set; }
    public bool GreenStriped { get; set; }
    public bool GrayLines { get; set; }
    public bool AnyEnabled => Contrast || Denoise || Binarize || RemoveLines || GreenStriped || GrayLines;

    /// <summary>
    /// Không preprocess - dùng cho captcha nền trắng (hóa đơn điện tử).
    /// </summary>
    public static PreprocessOptions None => new();
}
