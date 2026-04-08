using System.Diagnostics.CodeAnalysis;

namespace SmartInvoice.Application.Services;

/// <summary>
/// URL tra cứu tĩnh theo MST người bán cho cổng VNPT merchant — dùng chung PDF fetcher, popup gợi ý và domain discovery.
/// </summary>
public static class VnptMerchantSearchUrlCatalog
{
    /// <summary>Thử khớp MST người bán (trim, bỏ khoảng trắng).</summary>
    public static bool TryGetSearchUrlBySellerTaxCode(
        string? sellerTaxCode,
        [NotNullWhen(true)] out string? searchUrl)
    {
        searchUrl = null;
        if (string.IsNullOrWhiteSpace(sellerTaxCode))
            return false;

        var normalized = sellerTaxCode.Trim().Replace(" ", string.Empty);

        if (string.Equals(normalized, "0304741634-003", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://lottemart-bdg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0304741634", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "http://lottemart-nsg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0702101089", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://lottemart-nsg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0304741634-005", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://lottemart-vtu-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0304741634-008", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://lottemart-bdh-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0304741634-007", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://lottemart-cto-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0304741634-011", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://lottemart-ntg-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0304741634-001", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://lottemart-dni-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0304741634-002", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://lottemart-btn-tt78.vnpt-invoice.com.vn/HomeNoLogin/SearchByFkey";
            return true;
        }

        if (string.Equals(normalized, "0300555450", StringComparison.OrdinalIgnoreCase))
        {
            searchUrl = "https://hoadon.petrolimex.com.vn/";
            return true;
        }

        return false;
    }
}
