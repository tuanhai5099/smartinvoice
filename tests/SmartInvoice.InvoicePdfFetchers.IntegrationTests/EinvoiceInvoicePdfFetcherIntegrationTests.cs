using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Captcha.Preprocessing;
using SmartInvoice.Infrastructure.Captcha;
using SmartInvoice.InvoicePdfFetchers;
using Xunit;

namespace SmartInvoice.InvoicePdfFetchers.IntegrationTests;

/// <summary>
/// Integration test cho Einvoice (einvoice.vn) PDF fetcher.
/// Payload chứa cttkhac với:
/// - "DC TC" = URL tra cứu (thường là https://einvoice.vn/tra-cuu)
/// - "Mã TC" = Mã nhận hóa đơn (MaNhanHoaDon) dùng để tra cứu.
/// Chạy: dotnet test --filter "FullyQualifiedName~EinvoiceInvoicePdfFetcherIntegrationTests"
/// </summary>
public sealed class EinvoiceInvoicePdfFetcherIntegrationTests
{
    /// <summary>
    /// Payload mẫu dùng cho tra cứu Einvoice.
    /// Ghi chú: Mã TC bên dưới là ví dụ; khi dùng thực tế, thay bằng mã đang còn hiệu lực.
    /// </summary>
    private const string TestPayloadJson = """
        {
            "cttkhac": [
                { "ttruong": "DC TC", "dlieu": "https://einvoice.vn/tra-cuu" },
                { "ttruong": "Mã TC", "dlieu": "0C0EBDK9HK8" }
            ]
        }
        """;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchPdfAsync_WithSearchUrlAndMaTc_ReturnsPdf()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var captchaSolver = new CaptchaSolverService(loggerFactory, PreprocessOptions.None);
        var fetcher = new EinvoiceInvoicePdfFetcher(captchaSolver, loggerFactory);

        var result = await fetcher.FetchPdfAsync(TestPayloadJson);

        if (result is InvoicePdfResult.Failure f)
        {
            throw new Exception($"Integration test Einvoice thất bại: {f.ErrorMessage}");
        }

        var success = Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.NotNull(success.PdfBytes);
        Assert.True(success.PdfBytes.Length > 0);

        var header = System.Text.Encoding.ASCII.GetString(success.PdfBytes.AsSpan(0, Math.Min(5, success.PdfBytes.Length)));
        Assert.Equal("%PDF-", header);
    }
}

