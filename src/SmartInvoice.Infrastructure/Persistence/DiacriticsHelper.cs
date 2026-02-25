using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace SmartInvoice.Infrastructure.Persistence;

/// <summary>
/// Bỏ dấu tiếng Việt (và ký tự kết hợp) để tìm kiếm không phân biệt dấu.
/// Dùng cho tìm theo tên người bán/người mua: "nguyen" khớp "Nguyễn".
/// </summary>
public static class DiacriticsHelper
{
    /// <summary>Bỏ dấu: FormD → bỏ NonSpacingMark → FormC, đ/Đ → d/D.</summary>
    public static string RemoveDiacritics(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString()
            .Normalize(NormalizationForm.FormC)
            .Replace('đ', 'd').Replace('Đ', 'D');
    }

    /// <summary>Hàm SQLite remove_diacritics – EF dùng để sinh SQL, SQLite gọi delegate đăng ký trên connection.</summary>
    [DbFunction("remove_diacritics", IsBuiltIn = false)]
    public static string RemoveDiacriticsSql(string? s) => RemoveDiacritics(s);
}
