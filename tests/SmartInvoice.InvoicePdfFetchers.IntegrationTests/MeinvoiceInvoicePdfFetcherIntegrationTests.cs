using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.InvoicePdfFetchers;
using Xunit;

namespace SmartInvoice.InvoicePdfFetchers.IntegrationTests;

/// <summary>
/// Integration test cho MISA meInvoice (NCC 0101243150) PDF fetcher.
/// Payload chứa cttkhac với:
/// - "transaction id" = GJFJTDMXKQ6M (mã giao dịch dùng để tải PDF).
/// Chạy: dotnet test --filter "FullyQualifiedName~MeinvoiceInvoicePdfFetcherIntegrationTests"
/// </summary>
public sealed class MeinvoiceInvoicePdfFetcherIntegrationTests
{
    /// <summary>
    /// Payload mẫu dùng cho tra cứu meInvoice.
    /// </summary>
    private const string TestPayloadJson = """
        {
            "cttkhac": [
                { "ttruong": "transaction id", "dlieu": "GJFJTDMXKQ6M" }
            ]
        }
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchPdfAsync_WithTransactionIdInPayload_ReturnsPdf()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var httpClient = new HttpClient();
        var fetcher = new MeinvoiceInvoicePdfFetcher(httpClient, loggerFactory);

        var result = await fetcher.FetchPdfAsync(TestPayloadJson);

        if (result is InvoicePdfResult.Failure f)
        {
            throw new Exception($"Integration test meInvoice thất bại: {f.ErrorMessage}");
        }

        var success = Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.NotNull(success.PdfBytes);
        Assert.True(success.PdfBytes.Length > 0);

        var header = System.Text.Encoding.ASCII.GetString(success.PdfBytes.AsSpan(0, Math.Min(5, success.PdfBytes.Length)));
        Assert.Equal("%PDF-", header);
    }
}

