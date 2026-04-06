using Microsoft.Extensions.Logging.Abstractions;
using SmartInvoice.Application.Services;
using SmartInvoice.Infrastructure.Services.Pdf;
using SmartInvoice.InvoicePdfFetchers;
using Xunit;

namespace SmartInvoice.Infrastructure.Tests;

public sealed class InvoicePdfFetcherRegistryTests
{
    [Fact]
    public void GetFetcher_UsesInvoiceProviderAttributeAlias_WhenProviderKeyDiffersFromFetcherKey()
    {
        var fetcher = new AliasedWinInvoiceFetcher();
        var registry = new InvoicePdfFetcherRegistry(
            new StubFallbackFetcher(),
            new StubProvider(fetcher),
            NullLoggerFactory.Instance);

        var resolved = registry.GetFetcher("0312303803");
        Assert.Same(fetcher, resolved);
    }

    [Fact]
    public void GetFetcher_UnknownKey_UsesFallback()
    {
        var registry = new InvoicePdfFetcherRegistry(
            new StubFallbackFetcher(),
            new StubProvider(new AliasedWinInvoiceFetcher()),
            NullLoggerFactory.Instance);

        var resolved = registry.GetFetcher("0000000000");
        Assert.IsType<StubFallbackFetcher>(resolved);
    }

    private sealed class StubProvider : IKeyedInvoicePdfFetcherProvider
    {
        private readonly IKeyedInvoicePdfFetcher[] _fetchers;
        public StubProvider(params IKeyedInvoicePdfFetcher[] fetchers) => _fetchers = fetchers;
        public IEnumerable<IKeyedInvoicePdfFetcher> GetFetchers() => _fetchers;
    }

    private sealed class StubFallbackFetcher : IInvoicePdfFallbackFetcher
    {
        public Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default) =>
            Task.FromResult<InvoicePdfResult>(new InvoicePdfResult.Failure("fallback"));
    }

    [InvoiceProvider("0104918404", InvoiceProviderMatchKind.SellerTaxCode)]
    [InvoiceProvider("0312303803", InvoiceProviderMatchKind.ProviderTaxCode)]
    private sealed class AliasedWinInvoiceFetcher : IKeyedInvoicePdfFetcher
    {
        public string ProviderKey => "0104918404";
        public Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default) =>
            Task.FromResult<InvoicePdfResult>(new InvoicePdfResult.Failure("n/a"));
    }
}
