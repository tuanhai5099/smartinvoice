using Microsoft.Extensions.Logging.Abstractions;
using SmartInvoice.Application.Services;
using SmartInvoice.Infrastructure.Services.Pdf;
using Xunit;

namespace SmartInvoice.Infrastructure.Tests;

public sealed class InvoiceProviderResolverCompatibilityTests
{
    [Fact]
    public void ResolveFetcher_WithJsonPayload_MsttcgpAsNumber_MatchesProviderAttributeAfterNormalization()
    {
        var registry = new RecordingRegistry();
        var sut = new InvoicePdfProviderResolver(registry, NullLoggerFactory.Instance);
        // API đôi khi trả msttcgp là number — chuẩn hóa MST phải khớp attribute (0108971656) như registry.
        const string json = """{"msttcgp":108971656,"nbmst":"0100000000"}""";

        sut.ResolveFetcher(json);

        Assert.Equal("0108971656", registry.LastRequestedKey);
    }

    [Fact]
    public void ResolveFetcher_WithJsonPayload_KeepsLegacyProviderRouting()
    {
        var registry = new RecordingRegistry();
        var sut = new InvoicePdfProviderResolver(registry, NullLoggerFactory.Instance);
        const string json = """{"msttcgp":"0100109106","nbmst":"0304741634"}""";

        sut.ResolveFetcher(json);

        Assert.Equal("0100109106", registry.LastRequestedKey);
    }

    [Fact]
    public void ResolveFetcher_WithXmlContext_PrioritizesSellerOverProvider()
    {
        var registry = new RecordingRegistry();
        var sut = new InvoicePdfProviderResolver(registry, NullLoggerFactory.Instance);
        const string xml = """
            <HDon>
              <DLHDon>
                <TTChung>
                  <NBMST>0104918404</NBMST>
                  <MSTTCGP>0312303803</MSTTCGP>
                </TTChung>
              </DLHDon>
            </HDon>
            """;
        var context = new InvoiceContentContext(xml, """{"msttcgp":"0312303803","nbmst":"0104918404"}""", "0104918404", "0312303803", Guid.NewGuid(), InvoiceFetcherContentKind.Xml);

        sut.ResolveFetcher(context);

        // WinInvoice has both seller+provider mapping; seller must win.
        Assert.Equal("0104918404", registry.LastRequestedKey);
    }

    [Fact]
    public void ResolveFetcher_WithXmlContext_FallsBackToProviderWhenSellerNotMapped()
    {
        var registry = new RecordingRegistry();
        var sut = new InvoicePdfProviderResolver(registry, NullLoggerFactory.Instance);
        const string xml = """
            <HDon>
              <DLHDon>
                <TTChung>
                  <NBMST>9999999999</NBMST>
                  <MSTTCGP>0108971656</MSTTCGP>
                </TTChung>
              </DLHDon>
            </HDon>
            """;
        var context = new InvoiceContentContext(xml, """{"msttcgp":"0108971656","nbmst":"9999999999"}""", "9999999999", "0108971656", Guid.NewGuid(), InvoiceFetcherContentKind.Xml);

        sut.ResolveFetcher(context);

        Assert.Equal("0108971656", registry.LastRequestedKey);
    }

    [Fact]
    public void ResolveFetcher_WithUppercaseMsttcgpTag_RoutesMyinvoiceProvider()
    {
        var registry = new RecordingRegistry();
        var sut = new InvoicePdfProviderResolver(registry, NullLoggerFactory.Instance);
        const string xml = """
            <HDon>
              <DLHDon>
                <TTChung>
                  <MSTTCGP>0108971656</MSTTCGP>
                </TTChung>
                <NDHDon>
                  <NBan>
                    <MST>0317596247</MST>
                  </NBan>
                </NDHDon>
              </DLHDon>
            </HDon>
            """;
        var context = new InvoiceContentContext(xml, "{}", null, null, Guid.NewGuid(), InvoiceFetcherContentKind.Xml);

        sut.ResolveFetcher(context);

        Assert.Equal("0108971656", registry.LastRequestedKey);
    }

    [Fact]
    public void ResolveFetcher_UsesTvanTaxCode_WhenMsttcgpMissing()
    {
        var registry = new RecordingRegistry();
        var sut = new InvoicePdfProviderResolver(registry, NullLoggerFactory.Instance);
        const string json = """{"tvanDnKntt":"0108971656","nbmst":"9999999999"}""";

        sut.ResolveFetcher(json);

        Assert.Equal("0108971656", registry.LastRequestedKey);
    }

    [Fact]
    public void ResolveFetcher_SellerNbmst_NormalizesLeadingZeros_ToMatchSellerAttribute()
    {
        var registry = new RecordingRegistry();
        var sut = new InvoicePdfProviderResolver(registry, NullLoggerFactory.Instance);
        const string json = """{"msttcgp":"0312303803","nbmst":"104918404"}""";

        sut.ResolveFetcher(json);

        Assert.Equal("0104918404", registry.LastRequestedKey);
    }

    [Fact]
    public void ResolveFetcher_WithXmlContext_UsesTvanWhenMsttcgpMissing()
    {
        var registry = new RecordingRegistry();
        var sut = new InvoicePdfProviderResolver(registry, NullLoggerFactory.Instance);
        const string xml = """
            <HDon>
              <DLHDon>
                <TTChung>
                  <TVANDNKNTT>0108971656</TVANDNKNTT>
                </TTChung>
                <NDHDon>
                  <NBan><MST>0317596247</MST></NBan>
                </NDHDon>
              </DLHDon>
            </HDon>
            """;
        var context = new InvoiceContentContext(xml, "{}", null, null, Guid.NewGuid(), InvoiceFetcherContentKind.Xml);

        sut.ResolveFetcher(context);

        Assert.Equal("0108971656", registry.LastRequestedKey);
    }

    private sealed class RecordingRegistry : IInvoicePdfFetcherRegistry
    {
        public string? LastRequestedKey { get; private set; }

        public IInvoicePdfFetcher GetFetcher(string? providerKey)
        {
            LastRequestedKey = providerKey;
            return new StubFetcher();
        }
    }

    private sealed class StubFetcher : IInvoicePdfFetcher
    {
        public Task<InvoicePdfResult> FetchPdfAsync(string payloadJson, CancellationToken cancellationToken = default) =>
            Task.FromResult<InvoicePdfResult>(new InvoicePdfResult.Failure("n/a"));
    }
}
