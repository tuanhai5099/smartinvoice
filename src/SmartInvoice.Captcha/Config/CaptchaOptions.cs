namespace SmartInvoice.Captcha.Config;

/// <summary>
/// Cấu hình bảng ký tự và độ dài tối đa cho captcha (giống paddlesharp).
/// </summary>
public static class CaptchaOptions
{
    public const string Charset = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    public const int MaxLabelLength = 6;
    public static readonly ISet<char> AllowedChars = new HashSet<char>(Charset);
}
