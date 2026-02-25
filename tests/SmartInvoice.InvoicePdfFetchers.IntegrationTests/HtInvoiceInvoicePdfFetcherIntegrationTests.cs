using Microsoft.Extensions.Logging;
using SmartInvoice.InvoicePdfFetchers;
using SmartInvoice.Application.Services;
using Xunit;

namespace SmartInvoice.InvoicePdfFetchers.IntegrationTests;

/// <summary>
/// Integration test cho HTInvoice PDF fetcher:
/// - Mở https://laphoadon.htinvoice.vn/TraCuu
/// - Giả lập click reCAPTCHA, điền mã tra cứu và tải PDF.
/// Chạy: dotnet test --filter "FullyQualifiedName~HtInvoiceInvoicePdfFetcherIntegrationTests"
/// </summary>
public sealed class HtInvoiceInvoicePdfFetcherIntegrationTests
{
    /// <summary>
    /// Payload mẫu cho HTInvoice: DC TC là URL tra cứu, Mã TC là mã tra cứu hóa đơn.
    /// </summary>
    private const string TestPayloadJson = """
        {
            "cttkhac": [
                { "ttruong": "DC TC", "dlieu": "https://laphoadon.htinvoice.vn/TraCuu" },
                { "ttruong": "Mã TC", "dlieu": "9F624C97EBDA412AAC508D7E58D67DDD" }
            ]
        }
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchPdfAsync_WithSearchUrlAndMaTc_ClicksRecaptchaAndDownloadsPdf()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var fetcher = new HtInvoiceInvoicePdfFetcher(loggerFactory);

        var result = await fetcher.FetchPdfAsync(TestPayloadJson);

        if (result is InvoicePdfResult.Failure f)
        {
            throw new Exception($"Integration test HTInvoice thất bại: {f.ErrorMessage}");
        }

        var success = Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.NotNull(success.PdfBytes);
        Assert.True(success.PdfBytes.Length > 0);

        var header = System.Text.Encoding.ASCII.GetString(success.PdfBytes.AsSpan(0, Math.Min(5, success.PdfBytes.Length)));
        Assert.Equal("%PDF-", header);
    }
}

