using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.InvoicePdfFetchers;
using Xunit;

namespace SmartInvoice.InvoicePdfFetchers.IntegrationTests;

/// <summary>
/// Integration test cho WinInvoice (tracuu.wininvoice.vn) PDF fetcher.
/// Payload chứa cttkhac với:
/// - "Mã tra cứu hóa đơn" = INYTL8OGWK (private_code)
/// - "Mã công ty"        = 0312258501 (cmpn_key)
/// Chạy: dotnet test --filter "FullyQualifiedName~WinInvoicePdfFetcherIntegrationTests"
/// </summary>
public sealed class WinInvoicePdfFetcherIntegrationTests
{
    /// <summary>
    /// Payload mẫu dùng cho tra cứu WinInvoice.
    /// </summary>
    private const string TestPayloadJson = """
        {
            "cttkhac": [
                { "ttruong": "Mã tra cứu hóa đơn", "dlieu": "INYTL8OGWK" },
                { "ttruong": "Mã công ty", "dlieu": "0312258501" }
            ]
        }
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchPdfAsync_WithPrivateCodeAndCompanyKey_ReturnsPdf()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var fetcher = new WinInvoicePdfFetcher(loggerFactory);

        var result = await fetcher.FetchPdfAsync(TestPayloadJson);

        if (result is InvoicePdfResult.Failure f)
        {
            throw new Exception($"Integration test WinInvoice thất bại: {f.ErrorMessage}");
        }

        var success = Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.NotNull(success.PdfBytes);
        Assert.True(success.PdfBytes.Length > 0);

        var header = System.Text.Encoding.ASCII.GetString(success.PdfBytes.AsSpan(0, Math.Min(5, success.PdfBytes.Length)));
        Assert.Equal("%PDF-", header);
    }
}

