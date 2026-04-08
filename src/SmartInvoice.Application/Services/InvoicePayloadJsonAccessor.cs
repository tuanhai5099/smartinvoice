using System.Diagnostics.CodeAnalysis;

namespace SmartInvoice.Application.Services;

/// <summary>
/// Khi tải PDF XML-first, <see cref="InvoiceContentContext.ContentForFetcher"/> là XML;
/// mọi trường tra cứu lấy từ cấu trúc API (cttkhac, mhdon, …) phải đọc từ
/// <see cref="InvoiceContentContext.InvoiceJsonPayload"/> (snapshot JSON đồng bộ trong DB).
/// </summary>
public static class InvoicePayloadJsonAccessor
{
    /// <summary>
    /// Trả về JSON để parse trường portal; false nếu không có JSON khi đang ở chế độ XML-first.
    /// </summary>
    public static bool TryGetInvoiceJsonForPortalFields(
        InvoiceContentContext context,
        [NotNullWhen(true)] out string? json)
    {
        json = null;
        if (!string.IsNullOrWhiteSpace(context.InvoiceJsonPayload))
        {
            json = context.InvoiceJsonPayload;
            return true;
        }

        if (context.ContentKind != InvoiceFetcherContentKind.Xml &&
            !string.IsNullOrWhiteSpace(context.ContentForFetcher))
        {
            json = context.ContentForFetcher;
            return true;
        }

        return false;
    }
}
