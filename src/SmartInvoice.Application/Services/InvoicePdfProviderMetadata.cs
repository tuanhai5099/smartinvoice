namespace SmartInvoice.Application.Services;

/// <summary>Kết quả resolve provider PDF + key tra cứu (cùng thứ tự ưu tiên với chọn fetcher).</summary>
public sealed record InvoicePdfProviderMetadata(
    /// <summary>Metadata key (router tra cứu dùng cùng quy tắc với fetcher PDF). Null = chỉ dùng ProviderTaxCode từ payload.</summary>
    string? LookupRegistryKey,
    bool MayRequireUserIntervention,
    bool RequiresXml,
    string? ProviderTaxCode,
    string? SellerTaxCodeFromPayload,
    string? FetcherRegistryKeyUsed,
    string? FetcherTypeName);
