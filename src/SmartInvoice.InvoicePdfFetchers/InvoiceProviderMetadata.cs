using System;

namespace SmartInvoice.InvoicePdfFetchers;

/// <summary>Cách match provider: theo MST NCC (msttcgp), MST người bán (nbmst) hoặc pattern trong JSON.</summary>
public enum InvoiceProviderMatchKind
{
    ProviderTaxCode = 0,
    SellerTaxCode = 1,
    JsonPattern = 2
}

/// <summary>
/// Khai báo metadata cho một PDF fetcher:
/// - Key: mã nhận diện (mst NCC, MST người bán hoặc logical key như "VNPT-PORTAL").
/// - MatchKind: cách dùng key (msttcgp, nbmst hay pattern JSON).
/// - RequiresXml: fetcher cần XML thay vì JSON.
/// - RequiresSellerPortalUrl: cần URL portal theo MST người bán (VNPT portal).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class InvoiceProviderAttribute : Attribute
{
    public string Key { get; }
    public InvoiceProviderMatchKind MatchKind { get; }

    /// <summary>Key đăng ký gợi ý tra cứu (mst NCC); bắt buộc khi <see cref="MatchKind"/> là SellerTaxCode và khác portal mặc định.</summary>
    public string? InvoiceLookupRegistryKey { get; init; }

    /// <summary>Fetcher thường cần người dùng tự tra cứu / captcha / trình duyệt (gợi ý bulk + popup).</summary>
    public bool MayRequireUserIntervention { get; init; }

    public bool RequiresXml { get; init; }
    public bool RequiresSellerPortalUrl { get; init; }

    public InvoiceProviderAttribute(string key, InvoiceProviderMatchKind matchKind)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        MatchKind = matchKind;
    }
}

