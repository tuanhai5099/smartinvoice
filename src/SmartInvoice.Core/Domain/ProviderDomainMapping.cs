namespace SmartInvoice.Core.Domain;

/// <summary>
/// Cấu hình domain tra cứu theo cặp (NCC, MST người bán) của từng công ty.
/// </summary>
public sealed class ProviderDomainMapping
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string ProviderTaxCode { get; set; } = string.Empty;
    public string SellerTaxCode { get; set; } = string.Empty;
    public string SearchUrl { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
