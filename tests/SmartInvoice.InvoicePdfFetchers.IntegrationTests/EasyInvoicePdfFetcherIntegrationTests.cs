using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Captcha.Preprocessing;
using SmartInvoice.Infrastructure.Captcha;
using SmartInvoice.InvoicePdfFetchers;
using Xunit;

namespace SmartInvoice.InvoicePdfFetchers.IntegrationTests;

/// <summary>
/// Integration test cho EasyInvoice PDF fetcher: gọi portal thật với PortalLink và Fkey cố định,
/// giải captcha bằng hệ thống hiện tại và kiểm tra tải được file PDF.
/// Chạy: dotnet test --filter "FullyQualifiedName~EasyInvoicePdfFetcherIntegrationTests"
/// </summary>
public sealed class EasyInvoicePdfFetcherIntegrationTests
{
    /// <summary>
    /// Payload mẫu với cttkhac chứa PortalLink và Fkey dùng cho tra cứu EasyInvoice.
    /// PortalLink và Fkey tương ứng cổng 0301445926hd.easyinvoice.vn.
    /// </summary>
    private const string TestPayloadJson = """
        {
            "cttkhac": [
                { "ttruong": "PortalLink", "dlieu": "https://0301445926hd.easyinvoice.vn" },
                { "ttruong": "Fkey", "dlieu": "6ONUDB4RS" }
            ]
        }
        """;

    /// <summary>
    /// Chạy: dotnet test -p tests/SmartInvoice.InvoicePdfFetchers.IntegrationTests --filter "FullyQualifiedName~EasyInvoicePdfFetcherIntegrationTests"
    /// Test gọi portal thật, giải captcha (PaddleOCR) và tải zip → PDF; có thể mất 1–2 phút.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FetchPdfAsync_WithFixedPortalLinkAndFkey_SolvesCaptchaAndReturnsPdf()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var captchaSolver = new CaptchaSolverService(loggerFactory, PreprocessOptions.None);
        var fetcher = new EasyInvoicePdfFetcher(captchaSolver, loggerFactory);

        var result = await fetcher.FetchPdfAsync(TestPayloadJson);

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
