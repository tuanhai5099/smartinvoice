using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.InvoicePdfFetchers;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Thông tin cấu hình provider đã được resolve từ attribute trên fetcher.
/// </summary>
internal sealed record InvoiceProviderConfig(
    string Key,
    InvoiceProviderMatchKind MatchKind,
    string DisplayName,
    Type FetcherType,
    string? InvoiceLookupRegistryKey,
    bool MayRequireUserIntervention,
    bool RequiresXml,
    bool RequiresSellerPortalUrl);

public interface IInvoicePdfProviderResolver
{
    /// <summary>
    /// Chọn fetcher phù hợp cho payload JSON. Nếu không tìm được thì trả về fallback fetcher.
    /// </summary>
    IInvoicePdfFetcher ResolveFetcher(string payloadJson);

    /// <summary>Cùng quy tắc match với <see cref="ResolveFetcher"/>; dùng cho gợi ý tra cứu và phân loại can thiệp người dùng.</summary>
    InvoicePdfProviderMetadata ResolveMetadata(string payloadJson);
}

/// <summary>
/// Resolver trung tâm: đọc attribute trên các fetcher để build cấu hình provider,
/// sau đó chọn fetcher theo msttcgp (provider key), nbmst (MST người bán) hoặc pattern JSON.
/// Hiện tại pattern JSON vẫn dùng logic cũ; sau này có thể mở rộng JsonMatcher riêng.
/// </summary>
public sealed class InvoicePdfProviderResolver : IInvoicePdfProviderResolver
{
    private readonly IInvoicePdfFetcherRegistry _registry;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<InvoiceProviderConfig> _configs;

    public InvoicePdfProviderResolver(
        IInvoicePdfFetcherRegistry registry,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _logger = loggerFactory.CreateLogger<InvoicePdfProviderResolver>();
        _configs = BuildConfigsFromAttributes();
    }

    public IInvoicePdfFetcher ResolveFetcher(string payloadJson)
    {
        var (providerKey, sellerTaxCode) = ParsePayloadKeys(payloadJson);
        var cfg = MatchConfig(providerKey, sellerTaxCode);
        if (cfg != null)
        {
            _logger.LogDebug("Resolved PDF provider → {Fetcher} (key={Key}).", cfg.FetcherType.Name, cfg.Key);
            if (cfg.MatchKind == InvoiceProviderMatchKind.SellerTaxCode)
                return _registry.GetFetcher(cfg.Key);
            return _registry.GetFetcher(providerKey);
        }

        _logger.LogDebug("PDF provider resolver falling back to registry by providerKey='{Key}'.", providerKey ?? "(null)");
        return _registry.GetFetcher(providerKey);
    }

    public InvoicePdfProviderMetadata ResolveMetadata(string payloadJson)
    {
        var (providerKey, sellerTaxCode) = ParsePayloadKeys(payloadJson);
        var cfg = MatchConfig(providerKey, sellerTaxCode);
        if (cfg != null)
        {
            var lookupRegistryKey = cfg.InvoiceLookupRegistryKey
                ?? (cfg.MatchKind == InvoiceProviderMatchKind.ProviderTaxCode ? cfg.Key : null);
            var fetcherKey = cfg.MatchKind == InvoiceProviderMatchKind.SellerTaxCode ? cfg.Key : providerKey;
            return new InvoicePdfProviderMetadata(
                lookupRegistryKey,
                cfg.MayRequireUserIntervention,
                cfg.RequiresXml,
                providerKey,
                sellerTaxCode,
                fetcherKey,
                cfg.FetcherType.Name);
        }

        return new InvoicePdfProviderMetadata(
            providerKey,
            false,
            false,
            providerKey,
            sellerTaxCode,
            providerKey,
            null);
    }

    private InvoiceProviderConfig? MatchConfig(string? providerKey, string? sellerTaxCode)
    {
        if (!string.IsNullOrWhiteSpace(sellerTaxCode))
        {
            var normalizedSeller = NormalizeSellerTaxCode(sellerTaxCode);
            var bySeller = _configs.FirstOrDefault(c =>
                c.MatchKind == InvoiceProviderMatchKind.SellerTaxCode &&
                NormalizeSellerTaxCode(c.Key).Equals(normalizedSeller, StringComparison.OrdinalIgnoreCase));
            if (bySeller != null)
                return bySeller;
        }

        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            var normalized = NormalizeTaxCode(providerKey);
            var byProvider = _configs.FirstOrDefault(c =>
                c.MatchKind == InvoiceProviderMatchKind.ProviderTaxCode &&
                NormalizeTaxCode(c.Key).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (byProvider != null)
                return byProvider;
        }

        return null;
    }

    private static (string? ProviderTaxCode, string? SellerTaxCode) ParsePayloadKeys(string payloadJson)
    {
        string? providerKey = null;
        string? sellerTaxCode = null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            foreach (var candidate in GetInvoiceRootCandidates(r))
            {
                if (string.IsNullOrWhiteSpace(providerKey))
                    providerKey = GetProviderKeyFromRoot(candidate);

                if (string.IsNullOrWhiteSpace(sellerTaxCode) &&
                    candidate.TryGetProperty("nbmst", out var nbmstProp) &&
                    nbmstProp.ValueKind == JsonValueKind.String)
                    sellerTaxCode = nbmstProp.GetString();

                if (!string.IsNullOrWhiteSpace(providerKey) && !string.IsNullOrWhiteSpace(sellerTaxCode))
                    break;
            }
        }
        catch
        {
            // ignored
        }

        return (providerKey, sellerTaxCode);
    }

    private static string? GetProviderKeyFromRoot(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in r.EnumerateObject())
        {
            if (string.Equals(prop.Name, "msttcgp", StringComparison.OrdinalIgnoreCase))
            {
                var s = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    private static IEnumerable<JsonElement> GetInvoiceRootCandidates(JsonElement r)
    {
        yield return r;
        if (r.ValueKind != JsonValueKind.Object) yield break;

        if (r.TryGetProperty("ndhdon", out var ndhdon) && ndhdon.ValueKind == JsonValueKind.Object)
            yield return ndhdon;

        if (r.TryGetProperty("hdon", out var hdon) && hdon.ValueKind == JsonValueKind.Object)
            yield return hdon;

        if (r.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
                yield return data;
            else if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                yield return data[0];
        }
    }

    private static string NormalizeTaxCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var trimmed = code.Trim();
        var dash = trimmed.IndexOf('-');
        if (dash > 0)
            trimmed = trimmed[..dash];
        return trimmed;
    }

    private static string NormalizeSellerTaxCode(string? taxCode)
    {
        if (string.IsNullOrWhiteSpace(taxCode))
            return string.Empty;
        return taxCode.Trim().Replace(" ", string.Empty);
    }

    private static IReadOnlyList<InvoiceProviderConfig> BuildConfigsFromAttributes()
    {
        var list = new List<InvoiceProviderConfig>();
        var asm = typeof(EhoadonInvoicePdfFetcher).Assembly;
        var fetcherTypes = asm.GetTypes()
            .Where(t => typeof(IInvoicePdfFetcher).IsAssignableFrom(t) && !t.IsAbstract && t.IsClass);

        foreach (var type in fetcherTypes)
        {
            var attrs = type.GetCustomAttributes<InvoiceProviderAttribute>(inherit: false);
            foreach (var attr in attrs)
            {
                var displayName = type.Name;
                list.Add(new InvoiceProviderConfig(
                    attr.Key,
                    attr.MatchKind,
                    displayName,
                    type,
                    attr.InvoiceLookupRegistryKey,
                    attr.MayRequireUserIntervention,
                    attr.RequiresXml,
                    attr.RequiresSellerPortalUrl));
            }
        }

        return list;
    }
}

