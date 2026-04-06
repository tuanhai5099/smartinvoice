using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf.Lookup;

/// <summary>
/// Chọn rule theo cùng hint với PDF (resolver + EasyInvoice); fallback GDT khi không có gợi ý.
/// </summary>
public sealed class InvoiceLookupCatalog : IInvoiceLookupCatalog
{
    private readonly IInvoicePdfProviderResolver _resolver;
    private readonly IReadOnlyList<ILookupResolutionRule> _rules;
    private readonly ILogger<InvoiceLookupCatalog> _logger;

    public InvoiceLookupCatalog(
        IInvoicePdfProviderResolver resolver,
        IEnumerable<ILookupResolutionRule> rules,
        ILoggerFactory loggerFactory)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _rules = (rules ?? throw new ArgumentNullException(nameof(rules)))
            .OrderBy(r => r.Priority)
            .ToList();
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger<InvoiceLookupCatalog>();
    }

    public InvoiceLookupSuggestion? Resolve(InvoiceContentContext context)
    {
        if (string.IsNullOrWhiteSpace(context.InvoiceJsonPayload))
            return null;

        var json = context.InvoiceJsonPayload;
        var hint = BuildHint(json);

        foreach (var rule in _rules)
        {
            if (!rule.CanHandle(hint)) continue;
            var built = rule.Build(context);
            if (built != null)
            {
                var ptc = built.ProviderTaxCode ?? context.ProviderTaxCode;
                var stc = built.SellerTaxCode ?? context.SellerTaxCode;
                _logger.LogDebug("Lookup from rule {Type}.", rule.GetType().Name);
                return built with { ProviderTaxCode = ptc, SellerTaxCode = stc };
            }

            return GdtFallback(context);
        }

        return GdtFallback(context);
    }

    private InvoiceLookupResolutionHint BuildHint(string json)
    {
        var meta = _resolver.ResolveMetadata(json);
        return new InvoiceLookupResolutionHint(
            InvoicePayloadRouting.IsEasyInvoiceProvider(json),
            meta.FetcherRegistryKeyUsed,
            meta.LookupRegistryKey,
            meta.ProviderTaxCode,
            meta.SellerTaxCodeFromPayload);
    }

    private InvoiceLookupSuggestion GdtFallback(InvoiceContentContext context)
    {
        var secret = TryGetGdtLookupSecretFromPayload(context.InvoiceJsonPayload);
        return new InvoiceLookupSuggestion(
            string.Empty,
            "Cổng tra cứu Tổng cục Thuế",
            "https://tracuunnt.gdt.gov.vn",
            secret,
            context.SellerTaxCode,
            context.ProviderTaxCode);
    }

    private static string? TryGetGdtLookupSecretFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (r.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "mhdon", "mccqt", "MaHoaDon", "mahoadon" })
            {
                if (r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }
}
