using System.Net;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Services.Pdf;

public sealed class ProviderDomainDiscoveryService : IProviderDomainDiscoveryService
{
    private readonly IUnitOfWork _uow;
    private readonly HttpClient _httpClient;

    public ProviderDomainDiscoveryService(IUnitOfWork uow, HttpClient httpClient)
    {
        _uow = uow;
        _httpClient = httpClient;
    }

    public async Task<ProviderDomainDiscoveryResult> ResolveAsync(
        Guid companyId,
        string providerTaxCode,
        string sellerTaxCode,
        CancellationToken cancellationToken = default)
    {
        var provider = Normalize(providerTaxCode);
        var seller = Normalize(sellerTaxCode);

        var mapped = await _uow.ProviderDomainMappings
            .GetActiveAsync(companyId, provider, seller, cancellationToken)
            .ConfigureAwait(false);
        if (mapped != null && !string.IsNullOrWhiteSpace(mapped.SearchUrl))
            return new ProviderDomainDiscoveryResult(true, mapped.SearchUrl, false, "configured");

        if (string.Equals(provider, "0100684378", StringComparison.OrdinalIgnoreCase))
        {
            if (VnptMerchantSearchUrlCatalog.TryGetSearchUrlBySellerTaxCode(seller, out var staticUrl))
                return new ProviderDomainDiscoveryResult(true, staticUrl, false, "vnpt-seller-catalog");

            var url = $"https://{seller}-tt78.vnpt-invoice.com.vn/Portal/Index/";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.OK)
                    return new ProviderDomainDiscoveryResult(true, url, false, "vnpt-probe");
            }
            catch
            {
                // ignored
            }
            return new ProviderDomainDiscoveryResult(false, null, true, "vnpt-unresolved");
        }

        return new ProviderDomainDiscoveryResult(false, null, false, "not-supported");
    }

    public async Task SaveOverrideAsync(
        Guid companyId,
        string providerTaxCode,
        string sellerTaxCode,
        string searchUrl,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        var mapping = new ProviderDomainMapping
        {
            CompanyId = companyId,
            ProviderTaxCode = Normalize(providerTaxCode),
            SellerTaxCode = Normalize(sellerTaxCode),
            SearchUrl = searchUrl.Trim(),
            ProviderName = providerName,
            IsActive = true
        };
        await _uow.ProviderDomainMappings.UpsertAsync(mapping, cancellationToken).ConfigureAwait(false);
    }

    private static string Normalize(string s) => (s ?? string.Empty).Trim().Replace(" ", string.Empty);
}
