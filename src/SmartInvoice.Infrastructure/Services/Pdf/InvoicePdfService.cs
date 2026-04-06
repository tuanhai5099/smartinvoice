using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Services.Pdf;

/// <summary>
/// Facade: chuẩn hóa payload/XML → <see cref="InvoiceContentContext"/>; tra cứu và tải PDF qua <see cref="IInvoiceProviderOrchestrator"/>.
/// </summary>
public sealed class InvoicePdfService : IInvoicePdfService
{
    private readonly IInvoiceProviderOrchestrator _orchestrator;
    private readonly IInvoicePdfProviderResolver _providerResolver;
    private readonly IUnitOfWork _uow;
    private readonly IInvoiceXmlPreparationService _xmlPreparationService;
    private readonly ILogger _logger;

    public InvoicePdfService(
        IInvoiceProviderOrchestrator orchestrator,
        IInvoicePdfProviderResolver providerResolver,
        IUnitOfWork uow,
        IInvoiceXmlPreparationService xmlPreparationService,
        ILoggerFactory loggerFactory)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _uow = uow ?? throw new ArgumentNullException(nameof(uow));
        _xmlPreparationService = xmlPreparationService ?? throw new ArgumentNullException(nameof(xmlPreparationService));
        _logger = loggerFactory.CreateLogger(nameof(InvoicePdfService));
    }

    public async Task<InvoicePdfResult> GetPdfForInvoiceByExternalIdAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return new InvoicePdfResult.Failure("Mã hóa đơn trống.");
        var invoice = await _uow.Invoices.GetByExternalIdAsync(companyId, externalId, cancellationToken).ConfigureAwait(false);
        if (invoice == null || string.IsNullOrWhiteSpace(invoice.PayloadJson))
            return new InvoicePdfResult.Failure("Không tìm thấy hóa đơn hoặc dữ liệu liên quan.");

        var metadata = _providerResolver.ResolveMetadata(invoice.PayloadJson!);
        var ctx = await TryBuildContentForPdfAsync(companyId, invoice, metadata, cancellationToken).ConfigureAwait(false);
        if (ctx == null)
            return new InvoicePdfResult.Failure("Không thể chuẩn bị dữ liệu XML để tải PDF cho hóa đơn này.");

        var result = await _orchestrator.AcquirePdfAsync(ctx, cancellationToken).ConfigureAwait(false);
        if (result is InvoicePdfResult.Success)
        {
            _logger.LogInformation(
                "PDF acquired thành công cho invoice {ExternalId}. ContentKind={ContentKind}, JsonFallback={JsonFallback}, Provider={ProviderTaxCode}, Fetcher={FetcherType}",
                invoice.ExternalId,
                ctx.ContentKind,
                ctx.UsedJsonFallbackAfterXmlFailure,
                metadata.ProviderTaxCode ?? "(null)",
                metadata.FetcherTypeName ?? "(unknown)");
            return result;
        }

        if (result is InvoicePdfResult.Failure failureRaw)
        {
            _logger.LogWarning(
                "PDF acquire thất bại cho invoice {ExternalId}. ContentKind={ContentKind}, JsonFallback={JsonFallback}, Provider={ProviderTaxCode}, Fetcher={FetcherType}, Error={Error}",
                invoice.ExternalId,
                ctx.ContentKind,
                ctx.UsedJsonFallbackAfterXmlFailure,
                metadata.ProviderTaxCode ?? "(null)",
                metadata.FetcherTypeName ?? "(unknown)",
                failureRaw.ErrorMessage);
        }

        if (result is InvoicePdfResult.Failure failure && ctx.UsedJsonFallbackAfterXmlFailure)
        {
            var reason = string.IsNullOrWhiteSpace(ctx.XmlPreparationFailureReason)
                ? "Chuẩn bị XML không thành công"
                : $"Chuẩn bị XML thất bại: {ctx.XmlPreparationFailureReason}";
            return new InvoicePdfResult.Failure($"{reason}. Đã fallback sang JSON nhưng vẫn lỗi: {failure.ErrorMessage}");
        }

        return result;
    }

    public async Task<InvoiceLookupSuggestion?> GetLookupSuggestionAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        var invoice = await _uow.Invoices.GetByExternalIdAsync(companyId, externalId, cancellationToken).ConfigureAwait(false);
        if (invoice == null || string.IsNullOrWhiteSpace(invoice.PayloadJson))
            return null;

        var json = invoice.PayloadJson;
        var providerKey = GetProviderKeyFromPayload(json);
        if (IsEasyInvoiceProvider(json))
            providerKey = EasyInvoiceProviderKey;
        var ctx = new InvoiceContentContext(json, json, invoice.NbMst, providerKey);
        return _orchestrator.ResolveLookup(ctx);
    }

    public Task<InvoicePdfResult> GetPdfForInvoiceAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Task.FromResult<InvoicePdfResult>(new InvoicePdfResult.Failure("Payload hóa đơn trống."));

        var providerKey = GetProviderKeyFromPayload(payloadJson);
        if (IsEasyInvoiceProvider(payloadJson))
            providerKey = EasyInvoiceProviderKey;
        var ctx = new InvoiceContentContext(payloadJson, payloadJson, null, providerKey);
        return _orchestrator.AcquirePdfAsync(ctx, cancellationToken);
    }

    private async Task<InvoiceContentContext?> TryBuildContentForPdfAsync(
        Guid companyId,
        Invoice invoice,
        InvoicePdfProviderMetadata metadata,
        CancellationToken cancellationToken)
    {
        var json = invoice.PayloadJson!;
        var providerKey = GetProviderKeyFromPayload(json);
        if (IsEasyInvoiceProvider(json))
            providerKey = EasyInvoiceProviderKey;
        var xmlPreparation = await _xmlPreparationService
            .PrepareXmlForInvoiceAsync(companyId, invoice, cancellationToken)
            .ConfigureAwait(false);

        if (xmlPreparation.HasXml)
        {
            _logger.LogDebug(
                "PDF XML-first: sử dụng XML cho invoice {ExternalId} (status={Status}, provider={ProviderTaxCode}, fetcher={FetcherType}).",
                invoice.ExternalId,
                xmlPreparation.Status,
                metadata.ProviderTaxCode ?? providerKey ?? "(null)",
                metadata.FetcherTypeName ?? "(unknown)");
            return new InvoiceContentContext(
                xmlPreparation.XmlContent!,
                json,
                invoice.NbMst,
                providerKey,
                InvoiceFetcherContentKind.Xml,
                false,
                null);
        }

        var failureReason = string.IsNullOrWhiteSpace(xmlPreparation.FailureReason)
            ? "Không tìm thấy XML"
            : xmlPreparation.FailureReason;
        if (metadata.RequiresXml)
        {
            _logger.LogWarning(
                "PDF XML-first: provider yêu cầu XML nhưng chuẩn bị XML thất bại cho invoice {ExternalId}. Provider={ProviderTaxCode}, Fetcher={FetcherType}, Reason={Reason}",
                invoice.ExternalId,
                metadata.ProviderTaxCode ?? providerKey ?? "(null)",
                metadata.FetcherTypeName ?? "(unknown)",
                failureReason);
            return null;
        }

        _logger.LogInformation(
            "PDF XML-first: fallback JSON cho invoice {ExternalId}. Provider={ProviderTaxCode}, Fetcher={FetcherType}, XmlReason={Reason}",
            invoice.ExternalId,
            metadata.ProviderTaxCode ?? providerKey ?? "(null)",
            metadata.FetcherTypeName ?? "(unknown)",
            failureReason);

        return new InvoiceContentContext(
            json,
            json,
            invoice.NbMst,
            providerKey,
            InvoiceFetcherContentKind.Json,
            true,
            failureReason);
    }

    private const string EasyInvoiceProviderKey = "0105987432";

    private static string? GetProviderKeyFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            foreach (var prop in r.EnumerateObject())
            {
                if (string.Equals(prop.Name, "msttcgp", StringComparison.OrdinalIgnoreCase))
                {
                    var s = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEasyInvoiceProvider(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

            string? portalLink = null;

            if (r.TryGetProperty("cttkhac", out var cttkhac) && cttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("ttruong", out var tt) || tt.ValueKind != JsonValueKind.String) continue;
                    var ttStr = tt.GetString();
                    if (string.IsNullOrWhiteSpace(ttStr)) continue;
                    if (!string.Equals(ttStr.Trim(), "PortalLink", StringComparison.OrdinalIgnoreCase)) continue;

                    var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                        : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        portalLink = raw.Trim();
                        break;
                    }
                }
            }

            if (portalLink == null && r.TryGetProperty("ttkhac", out var ttkhac) && ttkhac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ttkhac.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("ttruong", out var tt) && tt.ValueKind == JsonValueKind.String)
                    {
                        var ttStr = tt.GetString();
                        if (!string.IsNullOrWhiteSpace(ttStr) &&
                            string.Equals(ttStr.Trim(), "PortalLink", StringComparison.OrdinalIgnoreCase))
                        {
                            var raw = item.TryGetProperty("dlieu", out var dl) ? dl.GetString()
                                : (item.TryGetProperty("dLieu", out var dL) ? dL.GetString() : null);
                            if (!string.IsNullOrWhiteSpace(raw))
                            {
                                portalLink = raw.Trim();
                                break;
                            }
                        }
                    }

                    if (portalLink != null) break;

                    if (item.TryGetProperty("ttchung", out var ttchung) && ttchung.ValueKind == JsonValueKind.Object)
                    {
                        if (ttchung.TryGetProperty("PortalLink", out var p) && p.ValueKind == JsonValueKind.String)
                        {
                            var s = p.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                portalLink = s.Trim();
                                break;
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(portalLink)) return false;
            var link = portalLink.Trim();
            return link.Contains("easyinvoice.vn", StringComparison.OrdinalIgnoreCase)
                   || link.Contains("easy-invoice.com", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

}
