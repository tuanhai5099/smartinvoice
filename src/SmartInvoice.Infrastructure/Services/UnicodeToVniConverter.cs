using System.Text;

namespace SmartInvoice.Infrastructure.Services;

/// <summary>
/// Chuyển chuỗi tiếng Việt Unicode (precomposed) sang bảng mã VNI Win/Unix
/// để hiển thị đúng với font VNI (ví dụ VNI-Times) trong Excel.
/// </summary>
/// <remarks>
/// Bảng mapping dựa trên hai nguồn uy tín:
/// 1. Viet Unicode – Unicode &amp; Existing Vietnamese Character Encodings (vietunicode.sourceforge.net/charset/)
///    Cột "VNI Hex": mã hex từng ký tự (base + dấu hoặc single-byte).
/// 2. VNI Character Sets – vietunicode.sourceforge.net/charset/vni.html (excerpt từ vnisoft.com)
///    Cột "ANSI Win/Unix" (Decimal): chuẩn VNI cho Windows/Unix.
/// Một số ký tự (Đ, đ, Ơ, ơ, Ư, ư, Ỉ, ỉ, Ị, ị, Ỵ, ỵ, Ĩ, ĩ) trong VNI là single-byte (0x80–0xFF).
/// </remarks>
public static class UnicodeToVniConverter
{
    /// <summary>Chuyển chuỗi Unicode sang chuỗi VNI (mỗi ký tự VNI là 1–2 byte, lưu thành char 0–255 để font VNI hiển thị đúng).</summary>
    public static string ToVniString(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder(input.Length * 2);
        foreach (var c in input)
        {
            if (_unicodeToVni.TryGetValue(c, out var vni))
            {
                sb.Append((char)vni.Byte1);
                if (vni.Byte2.HasValue)
                    sb.Append((char)vni.Byte2.Value);
            }
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private readonly struct VniBytes
    {
        public byte Byte1 { get; }
        public byte? Byte2 { get; }
        public VniBytes(byte b1, byte? b2 = null) { Byte1 = b1; Byte2 = b2; }
    }

    /// <summary>Bảng Unicode (precomposed) → VNI Win/Unix. Nguồn: vietunicode charset + vni.html (Win/Unix column).</summary>
    private static readonly Dictionary<char, VniBytes> _unicodeToVni = new()
    {
        { '\u00C0', new VniBytes(65, 216) },  // À
        { '\u00C1', new VniBytes(65, 217) },  // Á
        { '\u00C2', new VniBytes(65, 194) },  // Â
        { '\u00C3', new VniBytes(65, 213) },  // Ã
        { '\u00C4', new VniBytes(65, 207) },  // Ä (A dot below)
        { '\u00C5', new VniBytes(65, 197) },  // Å (A circumflex hook)
        { '\u00C8', new VniBytes(69, 216) },  // È
        { '\u00C9', new VniBytes(69, 217) },  // É
        { '\u00CA', new VniBytes(69, 194) },  // Ê
        { '\u00CC', new VniBytes(73, 216) },  // Ì
        { '\u00CD', new VniBytes(73, 217) },  // Í
        { '\u00D2', new VniBytes(79, 216) },  // Ò
        { '\u00D3', new VniBytes(79, 217) },  // Ó
        { '\u00D4', new VniBytes(79, 194) },  // Ô
        { '\u00D5', new VniBytes(79, 213) },  // Õ
        { '\u00D6', new VniBytes(79, 207) },  // Ö (O dot below)
        { '\u00D9', new VniBytes(85, 216) },  // Ù
        { '\u00DA', new VniBytes(85, 217) },  // Ú
        { '\u00DB', new VniBytes(85, 219) },  // Û
        { '\u00DC', new VniBytes(85, 207) },  // Ü (U dot below)
        { '\u00DD', new VniBytes(89, 217) },  // Ý
        { '\u00E0', new VniBytes(97, 248) },  // à
        { '\u00E1', new VniBytes(97, 249) },  // á
        { '\u00E2', new VniBytes(97, 226) },  // â
        { '\u00E3', new VniBytes(97, 245) },  // ã
        { '\u00E4', new VniBytes(97, 239) },  // ä (a dot below)
        { '\u00E5', new VniBytes(97, 229) },  // å (a circumflex hook)
        { '\u00E8', new VniBytes(101, 248) }, // è
        { '\u00E9', new VniBytes(101, 249) }, // é
        { '\u00EA', new VniBytes(101, 226) }, // ê
        { '\u00EC', new VniBytes(105, 248) }, // ì
        { '\u00ED', new VniBytes(105, 249) }, // í
        { '\u00F2', new VniBytes(111, 248) }, // ò
        { '\u00F3', new VniBytes(111, 249) }, // ó
        { '\u00F4', new VniBytes(111, 226) }, // ô
        { '\u00F5', new VniBytes(111, 245) }, // õ
        { '\u00F6', new VniBytes(111, 239) }, // ö (o dot below)
        { '\u00F9', new VniBytes(117, 248) }, // ù
        { '\u00FA', new VniBytes(117, 249) }, // ú
        { '\u00FB', new VniBytes(117, 251) }, // û
        { '\u00FC', new VniBytes(117, 239) }, // ü (u dot below)
        { '\u00FD', new VniBytes(121, 249) }, // ý
        { '\u0110', new VniBytes(209) },      // Đ (VNI Ñ = 0xD1, vietunicode)
        { '\u0111', new VniBytes(241) },      // đ (VNI ñ = 0xF1, vietunicode)
        { '\u01A0', new VniBytes(212) },      // Ơ (O horn) - VNI single byte
        { '\u01A1', new VniBytes(244) },      // ơ
        { '\u01AF', new VniBytes(214) },      // Ư (U horn, 0xD6)
        { '\u01B0', new VniBytes(246) },      // ư (0xF6)
        { '\u0128', new VniBytes(211) },      // Ĩ (I tilde, VNI Ó = 0xD3)
        { '\u0129', new VniBytes(243) },      // ĩ (VNI ó = 0xF3)
        { '\u1EA0', new VniBytes(65, 207) },  // Ạ (A dot below)
        { '\u1EA1', new VniBytes(97, 239) },  // ạ
        { '\u1EA2', new VniBytes(65, 219) },  // Ả (A hook above)
        { '\u1EA3', new VniBytes(97, 251) },  // ả
        { '\u1EA4', new VniBytes(65, 193) },  // Ấ
        { '\u1EA5', new VniBytes(97, 225) },  // ấ
        { '\u1EA6', new VniBytes(65, 192) },  // Ầ
        { '\u1EA7', new VniBytes(97, 224) },  // ầ
        { '\u1EA8', new VniBytes(65, 197) },  // Ẩ
        { '\u1EA9', new VniBytes(97, 229) },  // ẩ
        { '\u1EAA', new VniBytes(65, 195) },  // Ẫ
        { '\u1EAB', new VniBytes(97, 227) },  // ẫ
        { '\u1EAC', new VniBytes(65, 196) },  // Ậ
        { '\u1EAD', new VniBytes(97, 228) },  // ậ
        { '\u1EAE', new VniBytes(65, 200) },  // Ắ (A breve grave)
        { '\u1EAF', new VniBytes(97, 232) },  // ắ
        { '\u1EB0', new VniBytes(65, 201) },  // Ằ
        { '\u1EB1', new VniBytes(97, 233) },  // ằ
        { '\u1EB2', new VniBytes(65, 218) },  // Ẳ
        { '\u1EB3', new VniBytes(97, 250) },  // ẳ
        { '\u1EB4', new VniBytes(65, 220) },  // Ẵ
        { '\u1EB5', new VniBytes(97, 252) },  // ẵ
        { '\u1EB6', new VniBytes(65, 203) },  // Ặ
        { '\u1EB7', new VniBytes(97, 235) },  // ặ
        { '\u1EB8', new VniBytes(69, 207) },  // Ẹ
        { '\u1EB9', new VniBytes(101, 239) }, // ẹ
        { '\u1EBA', new VniBytes(69, 216) },  // Ẻ
        { '\u1EBB', new VniBytes(101, 248) }, // ẻ
        { '\u1EBC', new VniBytes(69, 213) },  // Ẽ
        { '\u1EBD', new VniBytes(101, 245) }, // ẽ
        { '\u1EBE', new VniBytes(69, 193) },  // Ế
        { '\u1EBF', new VniBytes(101, 225) }, // ế
        { '\u1EC0', new VniBytes(69, 192) },  // Ề
        { '\u1EC1', new VniBytes(101, 224) }, // ề
        { '\u1EC2', new VniBytes(69, 197) },  // Ể
        { '\u1EC3', new VniBytes(101, 229) }, // ể
        { '\u1EC4', new VniBytes(69, 195) },  // Ễ
        { '\u1EC5', new VniBytes(101, 227) }, // ễ
        { '\u1EC6', new VniBytes(69, 196) },  // Ệ
        { '\u1EC7', new VniBytes(101, 228) }, // ệ
        { '\u1EC8', new VniBytes(198) },      // Ỉ (I hook above, VNI Æ = 0xC6 single)
        { '\u1EC9', new VniBytes(230) },      // ỉ (VNI æ = 0xE6 single)
        { '\u1ECA', new VniBytes(210) },      // Ị (I dot below, VNI Ò = 0xD2 single)
        { '\u1ECB', new VniBytes(242) },      // ị (VNI ò = 0xF2 single)
        { '\u1ECC', new VniBytes(79, 207) },  // Ọ
        { '\u1ECD', new VniBytes(111, 239) }, // ọ
        { '\u1ECE', new VniBytes(79, 219) },  // Ỏ
        { '\u1ECF', new VniBytes(111, 251) }, // ỏ
        { '\u1ED0', new VniBytes(79, 213) },  // Ố
        { '\u1ED1', new VniBytes(111, 245) }, // ố
        { '\u1ED2', new VniBytes(79, 192) },  // Ồ
        { '\u1ED3', new VniBytes(111, 224) }, // ồ
        { '\u1ED4', new VniBytes(79, 197) },  // Ổ
        { '\u1ED5', new VniBytes(111, 229) }, // ổ
        { '\u1ED6', new VniBytes(79, 195) },  // Ỗ
        { '\u1ED7', new VniBytes(111, 227) }, // ỗ
        { '\u1ED8', new VniBytes(79, 196) },  // Ộ
        { '\u1ED9', new VniBytes(111, 228) }, // ộ
        { '\u1EDA', new VniBytes(212, 217) }, // Ớ (O horn acute) - 212,217
        { '\u1EDB', new VniBytes(244, 249) }, // ớ
        { '\u1EDC', new VniBytes(212, 216) }, // Ờ
        { '\u1EDD', new VniBytes(244, 248) }, // ờ
        { '\u1EDE', new VniBytes(212, 219) }, // Ở
        { '\u1EDF', new VniBytes(244, 251) }, // ở
        { '\u1EE0', new VniBytes(212, 213) }, // Ỡ
        { '\u1EE1', new VniBytes(244, 245) },  // ỡ
        { '\u1EE2', new VniBytes(212, 207) }, // Ợ
        { '\u1EE3', new VniBytes(244, 239) }, // ợ
        { '\u1EE4', new VniBytes(85, 207) },  // Ụ
        { '\u1EE5', new VniBytes(117, 239) }, // ụ
        { '\u1EE6', new VniBytes(85, 219) },  // Ủ
        { '\u1EE7', new VniBytes(117, 251) }, // ủ
        { '\u1EE8', new VniBytes(85, 213) },  // Ứ
        { '\u1EE9', new VniBytes(117, 245) }, // ứ
        { '\u1EEA', new VniBytes(214, 216) }, // Ừ (U horn grave)
        { '\u1EEB', new VniBytes(246, 248) }, // ừ
        { '\u1EEC', new VniBytes(214, 217) }, // Ử
        { '\u1EED', new VniBytes(246, 249) }, // ử
        { '\u1EEE', new VniBytes(214, 219) }, // Ữ
        { '\u1EEF', new VniBytes(246, 251) }, // ữ
        { '\u1EF0', new VniBytes(214, 207) }, // Ự
        { '\u1EF1', new VniBytes(246, 239) }, // ự
        { '\u1EF2', new VniBytes(89, 216) },  // Ỳ
        { '\u1EF3', new VniBytes(121, 248) }, // ỳ
        { '\u1EF4', new VniBytes(206) },      // Ỵ (Y dot below, VNI Î = 0xCE single)
        { '\u1EF5', new VniBytes(238) },      // ỵ (VNI î = 0xEE single)
        { '\u1EF6', new VniBytes(89, 219) },  // Ỷ
        { '\u1EF7', new VniBytes(121, 251) }, // ỷ
        { '\u1EF8', new VniBytes(89, 213) },  // Ỹ
        { '\u1EF9', new VniBytes(121, 245) }, // ỹ
    };
}
