using SmartInvoice.Captcha.Config;

namespace SmartInvoice.Captcha.Postprocessing;

public static class CaptchaPostprocessor
{
    public static string Postprocess(IEnumerable<TextWithCenter> textTuples)
    {
        var ordered = textTuples
            .OrderBy(x => x.CenterX)
            .Select(x => x.Text ?? string.Empty);
        var merged = string.Concat(ordered).ToUpperInvariant();
        var filtered = string.Concat(merged.Where(c => CaptchaOptions.AllowedChars.Contains(c)));
        return filtered.Length > CaptchaOptions.MaxLabelLength
            ? filtered[..CaptchaOptions.MaxLabelLength]
            : filtered;
    }
}
