using System.Text;

namespace SmartInvoice.Core;

/// <summary>
/// Chuẩn hóa chuỗi tiếng Việt để so sánh tham chiếu: chữ thường, bỏ khoảng trắng, bỏ dấu.
/// Dùng chung khi cần so khớp tên trường / nhãn không phụ thuộc hoa thường, khoảng trắng hay dấu (ví dụ "Mã Số Bí Mật" → "masobimat").
/// </summary>
public static class StringNormalization
{
    /// <summary>Chuẩn hóa để so sánh: Trim, chữ thường, bỏ mọi khoảng trắng, bỏ dấu tiếng Việt (ă, â, đ, ơ, ư, … → a, a, d, o, u).</summary>
    /// <example>NormalizeForComparison("Mã Số Bí Mật") → "masobimat"; NormalizeForComparison("  Ma so bi mat  ") → "masobimat".</example>
    public static string NormalizeForComparison(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(RemoveVietnameseDiacritic(c));
        }
        return sb.ToString();
    }

    /// <summary>Chuyển ký tự tiếng Việt có dấu sang chữ cái gốc (a, e, i, o, u, y, d); ký tự khác giữ nguyên (ASCII hoặc lower).</summary>
    public static char RemoveVietnameseDiacritic(char c)
    {
        if (c < 128) return c;
        return c switch
        {
            'đ' or 'Đ' => 'd',
            'ă' or 'Ă' or 'ằ' or 'Ằ' or 'ắ' or 'Ắ' or 'ẳ' or 'Ẳ' or 'ẵ' or 'Ẵ' or 'ặ' or 'Ặ' or 'â' or 'Â' or 'ầ' or 'Ầ' or 'ấ' or 'Ấ' or 'ẩ' or 'Ẩ' or 'ẫ' or 'Ẫ' or 'ậ' or 'Ậ' or 'à' or 'À' or 'á' or 'Á' or 'ả' or 'Ả' or 'ã' or 'Ã' or 'ạ' or 'Ạ' => 'a',
            'è' or 'È' or 'é' or 'É' or 'ẻ' or 'Ẻ' or 'ẽ' or 'Ẽ' or 'ẹ' or 'Ẹ' or 'ê' or 'Ê' or 'ề' or 'Ề' or 'ế' or 'Ế' or 'ể' or 'Ể' or 'ễ' or 'Ễ' or 'ệ' or 'Ệ' => 'e',
            'ì' or 'Ì' or 'í' or 'Í' or 'ỉ' or 'Ỉ' or 'ĩ' or 'Ĩ' or 'ị' or 'Ị' => 'i',
            'ò' or 'Ò' or 'ó' or 'Ó' or 'ỏ' or 'Ỏ' or 'õ' or 'Õ' or 'ọ' or 'Ọ' or 'ô' or 'Ô' or 'ồ' or 'Ồ' or 'ố' or 'Ố' or 'ổ' or 'Ổ' or 'ỗ' or 'Ỗ' or 'ộ' or 'Ộ' or 'ơ' or 'Ơ' or 'ờ' or 'Ờ' or 'ớ' or 'Ớ' or 'ở' or 'Ở' or 'ỡ' or 'Ỡ' or 'ợ' or 'Ợ' => 'o',
            'ù' or 'Ù' or 'ú' or 'Ú' or 'ủ' or 'Ủ' or 'ũ' or 'Ũ' or 'ụ' or 'Ụ' or 'ư' or 'Ư' or 'ừ' or 'Ừ' or 'ứ' or 'Ứ' or 'ử' or 'Ử' or 'ữ' or 'Ữ' or 'ự' or 'Ự' => 'u',
            'ỳ' or 'Ỳ' or 'ý' or 'Ý' or 'ỷ' or 'Ỷ' or 'ỹ' or 'Ỹ' or 'ỵ' or 'Ỵ' => 'y',
            _ => char.IsLetter(c) ? char.ToLowerInvariant(c) : c
        };
    }
}
