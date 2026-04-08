using Microsoft.Extensions.Logging.Abstractions;
using SmartInvoice.Application.Services;
using Xunit;
using SmartInvoice.Infrastructure.Services.Pdf;
using SmartInvoice.Infrastructure.Services.Pdf.Lookup;
using SmartInvoice.Infrastructure.Services.Pdf.Lookup.Rules;

namespace SmartInvoice.Infrastructure.Tests;

public sealed class InvoiceLookupCatalogTests
{
    private static InvoiceLookupCatalog CreateCatalog(IInvoicePdfProviderResolver resolver) =>
        new(resolver, CreateDefaultRules(), NullLoggerFactory.Instance);

    private static ILookupResolutionRule[] CreateDefaultRules() =>
    [
        new EasyInvoiceLookupRule(),
        new WinInvoiceLookupRule(),
        new EhoadonInvoiceLookupRule(),
        new EinvoiceInvoiceLookupRule(),
        new FastInvoiceLookupRule(),
        new HtInvoiceLookupRule(),
        new MeinvoiceInvoiceLookupRule(),
        new VietinvoiceLookupRule(),
        new ViettelInvoiceLookupRule(),
    ];

    private sealed class StubResolver : IInvoicePdfProviderResolver
    {
        public required Func<string, InvoicePdfProviderMetadata> Metadata { get; init; }

        public IInvoicePdfFetcher ResolveFetcher(string payloadJson) =>
            throw new NotSupportedException();
        public IInvoicePdfFetcher ResolveFetcher(InvoiceContentContext context) =>
            throw new NotSupportedException();

        public InvoicePdfProviderMetadata ResolveMetadata(string payloadJson) => Metadata(payloadJson);
    }

    [Fact]
    public void Resolve_ViettelPayload_ReturnsViettelSuggestionWithSecret()
    {
        var resolver = new StubResolver
        {
            Metadata = _ => new InvoicePdfProviderMetadata(
                "0100109106",
                false,
                false,
                "0100109106",
                "0123456789",
                "0100109106",
                "ViettelInvoicePdfFetcher")
        };
        var catalog = CreateCatalog(resolver);
        const string json = """
            [{"nbmst":"0100109106","cttkhac":[{"ttruong":"Mã số bí mật","dlieu":"RES123"}]}]
            """;
        var ctx = new InvoiceContentContext(json, json, "0123456789", "0100109106");
        var s = catalog.Resolve(ctx);
        Assert.NotNull(s);
        Assert.Equal("0100109106", s.ProviderKey);
        Assert.Equal("RES123", s.SecretCode);
        Assert.Contains("viettel", s.SearchUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_EasyPortalPayload_ReturnsEasySuggestion()
    {
        var resolver = new StubResolver
        {
            Metadata = _ => new InvoicePdfProviderMetadata(
                "0105987432",
                false,
                false,
                "0105987432",
                "0999999999",
                "0105987432",
                "EasyInvoicePdfFetcher")
        };
        var catalog = CreateCatalog(resolver);
        const string json = """
            [{"cttkhac":[
              {"ttruong":"PortalLink","dlieu":"https://portal.easyinvoice.vn/x"},
              {"ttruong":"Fkey","dlieu":"FK1"}
            ]}]
            """;
        var ctx = new InvoiceContentContext(json, json, "0999999999", "0105987432");
        var s = catalog.Resolve(ctx);
        Assert.NotNull(s);
        Assert.Equal("0105987432", s.ProviderKey);
        Assert.Equal("FK1", s.SecretCode);
        Assert.Equal("https://portal.easyinvoice.vn/x", s.SearchUrl);
    }

    [Fact]
    public void Resolve_NoMatchingRule_ReturnsGdtFallback()
    {
        var resolver = new StubResolver
        {
            Metadata = _ => new InvoicePdfProviderMetadata(
                "9999999999",
                false,
                false,
                "9999999999",
                "0111111111",
                "9999999999",
                null)
        };
        var catalog = CreateCatalog(resolver);
        const string json = """[{"msttcgp":"9999999999","nbmst":"0111111111","mhdon":"ABC"}]""";
        var ctx = new InvoiceContentContext(json, json, "0111111111", "9999999999");
        var s = catalog.Resolve(ctx);
        Assert.NotNull(s);
        Assert.Equal(string.Empty, s.ProviderKey);
        Assert.Equal("ABC", s.SecretCode);
        Assert.Contains("gdt.gov.vn", s.SearchUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_EmptyJsonPayload_ReturnsNull()
    {
        var resolver = new StubResolver { Metadata = _ => throw new InvalidOperationException() };
        var catalog = CreateCatalog(resolver);
        var ctx = new InvoiceContentContext("", "", null, null);
        Assert.Null(catalog.Resolve(ctx));
    }

    [Fact]
    public void Resolve_WinInvoicePayload_ReturnsSecretAndAuxiliaryCompanyKey()
    {
        var resolver = new StubResolver
        {
            Metadata = _ => new InvoicePdfProviderMetadata(
                "0312303803",
                false,
                false,
                "0312303803",
                "0104918404",
                "0104918404",
                "WinInvoicePdfFetcher")
        };
        var catalog = CreateCatalog(resolver);
        const string json = """
            [{"nbmst":"0104918404","cttkhac":[
              {"ttruong":"Mã tra cứu hóa đơn","dlieu":"PCODE"},
              {"ttruong":"Mã công ty","dlieu":"COMPANY"}]}]
            """;
        var ctx = new InvoiceContentContext(json, json, "0104918404", "0312303803");
        var s = catalog.Resolve(ctx);
        Assert.NotNull(s);
        Assert.Equal("PCODE", s.SecretCode);
        Assert.Equal("COMPANY", s.AuxiliaryLookupCode);
    }

    [Fact]
    public void Resolve_VietinvoicePayload_ReturnsVietinvoiceSuggestion()
    {
        var resolver = new StubResolver
        {
            Metadata = _ => new InvoicePdfProviderMetadata(
                "0106870211",
                false,
                false,
                "0106870211",
                "0317098812",
                "0106870211",
                "VietinvoiceInvoicePdfFetcher")
        };
        var catalog = CreateCatalog(resolver);
        const string json = """
            [{"cttkhac":[{"ttruong":"Mã tra cứu","dlieu":"WB4C4K99Q01I"}]}]
            """;
        var ctx = new InvoiceContentContext(json, json, "0317098812", "0106870211");
        var s = catalog.Resolve(ctx);
        Assert.NotNull(s);
        Assert.Equal("0106870211", s.ProviderKey);
        Assert.Equal("WB4C4K99Q01I", s.SecretCode);
        Assert.Contains("tracuuhoadon.vietinvoice.vn", s.SearchUrl, StringComparison.OrdinalIgnoreCase);
    }
}
