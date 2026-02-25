using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>Registry map key tvandnkntt → IInvoiceLookupProvider. Dùng cho gợi ý tra cứu (không gọi PDF fetcher).</summary>
public sealed class InvoiceLookupProviderRegistry : IInvoiceLookupProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IInvoiceLookupProvider> _map;
    private readonly ILogger _logger;

    public InvoiceLookupProviderRegistry(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(nameof(InvoiceLookupProviderRegistry));

        static string Normalize(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            var trimmed = key.Trim();
            var noZero = trimmed.TrimStart('0');
            return string.IsNullOrEmpty(noZero) ? trimmed : noZero;
        }

        var providers = new IInvoiceLookupProvider[]
        {
            new EasyInvoiceLookupProvider(),
            new EinvoiceLookupProvider(),
            new WinInvoiceLookupProvider(),
            new HtInvoiceLookupProvider(),
            new MeinvoiceLookupProvider(),
            new EhoadonLookupProvider(),
            new ViettelLookupProvider(),
            new FastInvoiceLookupProvider()
        };

        var dict = new Dictionary<string, IInvoiceLookupProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            var key = Normalize(p.ProviderKey);
            if (string.IsNullOrEmpty(key)) continue;
            dict[key] = p;
        }

        _map = dict;
        _logger.LogInformation("Invoice lookup registry: {Count} provider(s).", _map.Count);
    }

    public IInvoiceLookupProvider? GetProvider(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return null;
        var trimmed = providerKey.Trim();
        var dash = trimmed.IndexOf('-');
        if (dash > 0)
            trimmed = trimmed[..dash].Trim();
        var norm = trimmed.TrimStart('0');
        if (string.IsNullOrEmpty(norm)) norm = trimmed;
        return _map.TryGetValue(norm, out var provider) ? provider : null;
    }
}

