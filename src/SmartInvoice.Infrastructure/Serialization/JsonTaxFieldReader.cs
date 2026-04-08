using System.Globalization;
using System.Text.Json;

namespace SmartInvoice.Infrastructure.Serialization;

/// <summary>
/// Đọc các trường MST/mã số từ JSON: API đôi khi trả string, đôi khi number — cả hai đều phải map được sang fetcher/registry.
/// </summary>
internal static class JsonTaxFieldReader
{
    public static string? CoerceToTrimmedString(JsonElement p)
    {
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString()?.Trim(),
            JsonValueKind.Number => FormatNumber(p),
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static string? FormatNumber(JsonElement p)
    {
        if (p.TryGetInt64(out var l))
            return l.ToString(CultureInfo.InvariantCulture);
        if (p.TryGetDouble(out var d))
            return d.ToString(CultureInfo.InvariantCulture);
        return p.GetRawText();
    }
}
