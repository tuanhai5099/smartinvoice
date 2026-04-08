using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.InvoicePdfFetchers;
using SmartInvoice.Infrastructure.Serialization;

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
    IInvoicePdfFetcher ResolveFetcher(InvoiceContentContext context);

    /// <summary>Cùng quy tắc match với <see cref="ResolveFetcher"/>; dùng cho gợi ý tra cứu và phân loại can thiệp người dùng.</summary>
    InvoicePdfProviderMetadata ResolveMetadata(string payloadJson);
}

/// <summary>
/// Resolver trung tâm: đọc attribute trên các fetcher để build cấu hình provider,
/// rồi chọn fetcher theo thứ tự cố định:
/// (1) nbmst (MST người bán) — nếu có bản ghi <see cref="InvoiceProviderMatchKind.SellerTaxCode"/> thì dùng trước;
/// (2) msttcgp / TVAN (MST nhà cung cấp dịch vụ) — <see cref="InvoiceProviderMatchKind.ProviderTaxCode"/>.
/// Chuẩn hóa MST khi so khớp phải khớp với <see cref="InvoicePdfFetcherRegistry"/> (trim, hậu tố chi nhánh sau '-', bỏ số 0 đầu nếu toàn chữ số).
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
        if (InvoicePayloadRouting.IsEasyInvoiceProvider(payloadJson))
            return _registry.GetFetcher(InvoicePayloadRouting.EasyInvoiceProviderKey);

        var (providerKey, sellerTaxCode) = ParsePayloadKeys(payloadJson);
        var cfg = MatchConfig(providerKey, sellerTaxCode);
        if (cfg != null)
        {
            _logger.LogDebug("Resolved PDF provider → {Fetcher} (key={Key}).", cfg.FetcherType.Name, cfg.Key);
            return _registry.GetFetcher(cfg.Key);
        }

        _logger.LogDebug("PDF provider resolver falling back to registry by providerKey='{Key}'.", providerKey ?? "(null)");
        return _registry.GetFetcher(providerKey);
    }

    public IInvoicePdfFetcher ResolveFetcher(InvoiceContentContext context)
    {
        if (context.ContentKind == InvoiceFetcherContentKind.Xml)
        {
            var (providerKey, sellerTaxCode) = ParseXmlKeys(context.ContentForFetcher);
            var cfg = MatchConfig(sellerTaxCode: sellerTaxCode, providerKey: providerKey) ??
                      MatchConfig(context.ProviderTaxCode, context.SellerTaxCode);
            if (cfg != null)
                return _registry.GetFetcher(cfg.Key);
        }

        return ResolveFetcher(context.InvoiceJsonPayload);
    }

    public InvoicePdfProviderMetadata ResolveMetadata(string payloadJson)
    {
        if (InvoicePayloadRouting.IsEasyInvoiceProvider(payloadJson))
        {
            return new InvoicePdfProviderMetadata(
                InvoicePayloadRouting.EasyInvoiceProviderKey,
                false,
                false,
                InvoicePayloadRouting.EasyInvoiceProviderKey,
                null,
                InvoicePayloadRouting.EasyInvoiceProviderKey,
                nameof(EasyInvoicePdfFetcher));
        }

        var (providerKey, sellerTaxCode) = ParsePayloadKeys(payloadJson);
        var cfg = MatchConfig(providerKey, sellerTaxCode);
        if (cfg != null)
        {
            var lookupRegistryKey = cfg.InvoiceLookupRegistryKey
                ?? (cfg.MatchKind == InvoiceProviderMatchKind.ProviderTaxCode ? cfg.Key : null);
            var fetcherKey = cfg.Key;
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
            var normalizedSeller = NormalizeTaxIdentifier(sellerTaxCode);
            var bySeller = _configs.FirstOrDefault(c =>
                c.MatchKind == InvoiceProviderMatchKind.SellerTaxCode &&
                NormalizeTaxIdentifier(c.Key).Equals(normalizedSeller, StringComparison.OrdinalIgnoreCase));
            if (bySeller != null)
                return bySeller;
        }

        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            var normalized = NormalizeTaxIdentifier(providerKey);
            var byProvider = _configs.FirstOrDefault(c =>
                c.MatchKind == InvoiceProviderMatchKind.ProviderTaxCode &&
                NormalizeTaxIdentifier(c.Key).Equals(normalized, StringComparison.OrdinalIgnoreCase));
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
                    candidate.TryGetProperty("nbmst", out var nbmstProp))
                    sellerTaxCode = JsonTaxFieldReader.CoerceToTrimmedString(nbmstProp);

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
            if (string.Equals(prop.Name, "msttcgp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "tvanDnKntt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "tvandnkntt", StringComparison.OrdinalIgnoreCase))
            {
                var s = JsonTaxFieldReader.CoerceToTrimmedString(prop.Value);
                if (!string.IsNullOrWhiteSpace(s)) return s;
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

    /// <summary>
    /// Cùng quy tắc với <see cref="InvoicePdfFetcherRegistry"/> khi map key → fetcher:
    /// trim, bỏ khoảng trắng, cắt hậu tố sau dấu '-', với chuỗi toàn chữ số thì bỏ số 0 đầu.
    /// </summary>
    private static string NormalizeTaxIdentifier(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var trimmed = code.Trim().Replace(" ", string.Empty);
        var dashIndex = trimmed.IndexOf('-');
        if (dashIndex > 0)
            trimmed = trimmed[..dashIndex];
        var allDigits = true;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (!char.IsDigit(trimmed[i]))
            {
                allDigits = false;
                break;
            }
        }
        if (!allDigits) return trimmed;
        var withoutLeadingZeros = trimmed.TrimStart('0');
        return string.IsNullOrEmpty(withoutLeadingZeros) ? trimmed : withoutLeadingZeros;
    }

    private static (string? ProviderTaxCode, string? SellerTaxCode) ParseXmlKeys(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return (null, null);
        try
        {
            var doc = XDocument.Parse(xml);
            string? provider = doc.Descendants().FirstOrDefault(x =>
                string.Equals(x.Name.LocalName, "msttcgp", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(provider))
            {
                foreach (var tag in new[] { "tvandnkntt", "tvan" })
                {
                    provider = doc.Descendants().FirstOrDefault(x =>
                        string.Equals(x.Name.LocalName, tag, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(provider)) break;
                }
            }
            string? seller = doc.Descendants().FirstOrDefault(x =>
                string.Equals(x.Name.LocalName, "nbmst", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            // TT78 XML thường để MST người bán ở <NBan><MST>...</MST></NBan>
            if (string.IsNullOrWhiteSpace(seller))
            {
                seller = doc.Descendants().FirstOrDefault(x =>
                    string.Equals(x.Name.LocalName, "MST", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Parent?.Name.LocalName, "NBan", StringComparison.OrdinalIgnoreCase))
                    ?.Value?.Trim();
            }
            return (provider, seller);
        }
        catch
        {
            // Fallback khi XML có đoạn ký số/cấu trúc làm parser fail:
            // ưu tiên vẫn bóc được MSTTCGP để route fetcher đúng.
            var provider = ExtractTagValueByRegex(xml, "msttcgp")
                ?? ExtractTagValueByRegex(xml, "tvandnkntt")
                ?? ExtractTagValueByRegex(xml, "tvan");
            var seller = ExtractTagValueByRegex(xml, "nbmst") ?? ExtractSellerTaxFromNbanByRegex(xml);
            return (provider, seller);
        }
    }

    private static string? ExtractTagValueByRegex(string xml, string tagName)
    {
        var m = Regex.Match(
            xml,
            $@"<\s*{Regex.Escape(tagName)}\b[^>]*>\s*(?<v>[^<]+?)\s*<\s*/\s*{Regex.Escape(tagName)}\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["v"].Value.Trim() : null;
    }

    private static string? ExtractSellerTaxFromNbanByRegex(string xml)
    {
        var m = Regex.Match(
            xml,
            @"<\s*NBan\b[^>]*>[\s\S]*?<\s*MST\b[^>]*>\s*(?<v>[^<]+?)\s*<\s*/\s*MST\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups["v"].Value.Trim() : null;
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

