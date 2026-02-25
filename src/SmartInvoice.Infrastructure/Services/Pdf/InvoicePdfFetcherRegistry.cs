using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Registry map key tvandnkntt → IInvoicePdfFetcher. Key null/empty hoặc chưa đăng ký thì dùng fallback.
/// </summary>
public sealed class InvoicePdfFetcherRegistry : IInvoicePdfFetcherRegistry
{
    private readonly IInvoicePdfFetcher _fallbackFetcher;
    private readonly IReadOnlyDictionary<string, IInvoicePdfFetcher> _map;
    private readonly ILogger _logger;

    public InvoicePdfFetcherRegistry(
        IInvoicePdfFallbackFetcher fallbackFetcher,
        IKeyedInvoicePdfFetcherProvider keyedFetcherProvider,
        ILoggerFactory loggerFactory)
    {
        _fallbackFetcher = fallbackFetcher ?? throw new ArgumentNullException(nameof(fallbackFetcher));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(nameof(InvoicePdfFetcherRegistry));
        var map = new Dictionary<string, IInvoicePdfFetcher>(StringComparer.OrdinalIgnoreCase);
        var keyedFetchers = keyedFetcherProvider?.GetFetchers();
        if (keyedFetchers != null)
        {
            foreach (var k in keyedFetchers)
            {
                if (string.IsNullOrWhiteSpace(k.ProviderKey)) continue;
                var normalized = NormalizeKey(k.ProviderKey);
                if (string.IsNullOrEmpty(normalized)) continue;
                map[normalized] = k;
            }
        }
        _map = map;
        _logger.LogInformation("Invoice PDF registry: {Count} provider(s), fallback when key missing.", _map.Count);
    }

    public IInvoicePdfFetcher GetFetcher(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            return _fallbackFetcher;
        var keyOriginal = providerKey.Trim();
        var key = NormalizeKey(keyOriginal);
        if (_map.TryGetValue(key, out var fetcher))
            return fetcher;
        _logger.LogDebug("No PDF fetcher for key '{Key}', using fallback.", keyOriginal);
        return _fallbackFetcher;
    }

    /// <summary>
    /// Chuẩn hóa key nhà cung cấp: trim, nếu toàn ký tự số thì bỏ 0 ở đầu (\"0101360697\" → \"101360697\").
    /// Giúp map đúng dù payload có/không có số 0 đầu.
    /// </summary>
    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var trimmed = key.Trim();
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
}
