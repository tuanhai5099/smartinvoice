using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.InvoicePdfFetchers;
using Xunit;

namespace SmartInvoice.InvoicePdfFetchers.IntegrationTests;

/// <summary>
/// Integration test cho VNPT merchant PDF fetcher:
/// - Mở cổng VNPT SearchByFkey cho LOTTE MART BDG.
/// - Điền MCCQT (mhdon), giải captcha và tải PDF.
/// Chạy: dotnet test --filter "FullyQualifiedName~MerchantVnptInvoiceFetcherIntegrationTests"
/// </summary>
public sealed class MerchantVnptInvoiceFetcherIntegrationTests
{
    /// <summary>
    /// Payload mẫu cho merchant VNPT:
    /// - mhdon: MCCQT / mã tra cứu hóa đơn.
    /// - nbmst: Mã số thuế người bán (LOTTE MART BDG: 0304741634-003).
    /// </summary>
    private const string TestPayloadJson = """
        {
          "mhdon": "M1-26-AF2RB-12200002717",
          "nbmst": "0304741634"
        }
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchPdfAsync_WithMerchantVnptPayload_SolvesCaptchaAndDownloadsPdf()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var fetcher = new MerchantVnptInvoiceFetcher(new StubProviderDomainDiscoveryService(), loggerFactory);

        var result = await fetcher.FetchPdfAsync(TestPayloadJson);

        if (result is InvoicePdfResult.Failure f)
        {
            throw new Exception($"Integration test VNPT merchant thất bại: {f.ErrorMessage}");
        }

        var success = Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.NotNull(success.PdfBytes);
        Assert.True(success.PdfBytes.Length > 0);

        var header = System.Text.Encoding.ASCII.GetString(success.PdfBytes.AsSpan(0, Math.Min(5, success.PdfBytes.Length)));
        Assert.Equal("%PDF-", header);
    }
}

file sealed class StubProviderDomainDiscoveryService : IProviderDomainDiscoveryService
{
    public Task<ProviderDomainDiscoveryResult> ResolveAsync(Guid companyId, string providerTaxCode, string sellerTaxCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderDomainDiscoveryResult(false, null, false, null));

    public Task SaveOverrideAsync(Guid companyId, string providerTaxCode, string sellerTaxCode, string searchUrl, string? providerName = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

