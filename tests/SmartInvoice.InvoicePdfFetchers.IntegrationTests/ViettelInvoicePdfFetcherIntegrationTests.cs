using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.InvoicePdfFetchers;
using Xunit;

namespace SmartInvoice.InvoicePdfFetchers.IntegrationTests;

/// <summary>
/// Integration test cho Viettel (vinvoice.viettel.vn) PDF fetcher: generate → offsetX từ response → verify → download PDF.
/// Payload bắt buộc có nbmst và cttkhac "Mã số bí mật"; test truyền hai mã này vào payload.
/// Chạy: dotnet test --filter "FullyQualifiedName~ViettelInvoicePdfFetcherIntegrationTests"
/// </summary>
public sealed class ViettelInvoicePdfFetcherIntegrationTests
{
    /// <summary>
    /// Payload có nbmst (mã số thuế NCC) và cttkhac chứa "Mã số bí mật" để gọi API download PDF.
    /// </summary>
    private const string PayloadWithNbmstAndReservationCode = """
        {
            "nbmst": "0303121814",
            "cttkhac": [
                { "ttruong": "Mã số bí mật", "dlieu": "EQ6KP509HH123IS" }
            ]
        }
        """;

    /// <summary>
    /// Gọi API thật: generate → offsetX từ response → verify → downloadPDF; kiểm tra trả về PDF.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchPdfAsync_WithNbmstAndReservationCodeInPayload_ReturnsPdf()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var httpClient = new HttpClient();
        var fetcher = new ViettelInvoicePdfFetcher(httpClient, loggerFactory);

        var result = await fetcher.FetchPdfAsync(PayloadWithNbmstAndReservationCode);

        if (result is InvoicePdfResult.Failure f)
        {
            throw new Exception($"Integration test thất bại: {f.ErrorMessage}");
        }

        var success = Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.NotNull(success.PdfBytes);
        Assert.True(success.PdfBytes.Length > 0);

        var header = System.Text.Encoding.ASCII.GetString(success.PdfBytes.AsSpan(0, Math.Min(5, success.PdfBytes.Length)));
        Assert.Equal("%PDF-", header);
    }
}
