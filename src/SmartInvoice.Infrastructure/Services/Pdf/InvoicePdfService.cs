using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;
using SmartInvoice.Infrastructure.Serialization;

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
    private readonly IProviderDomainDiscoveryService _providerDomainDiscovery;
    private readonly ILogger _logger;

    public InvoicePdfService(
        IInvoiceProviderOrchestrator orchestrator,
        IInvoicePdfProviderResolver providerResolver,
        IUnitOfWork uow,
        IInvoiceXmlPreparationService xmlPreparationService,
        IProviderDomainDiscoveryService providerDomainDiscovery,
        ILoggerFactory loggerFactory)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _uow = uow ?? throw new ArgumentNullException(nameof(uow));
        _xmlPreparationService = xmlPreparationService ?? throw new ArgumentNullException(nameof(xmlPreparationService));
        _providerDomainDiscovery = providerDomainDiscovery ?? throw new ArgumentNullException(nameof(providerDomainDiscovery));
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
        var ctx = new InvoiceContentContext(json, json, invoice.NbMst, providerKey, companyId);
        var suggestion = _orchestrator.ResolveLookup(ctx);
        if (suggestion == null)
            return null;

        var providerForDiscovery = suggestion.ProviderTaxCode ?? providerKey ?? invoice.ProviderTaxCode;
        var sellerForDiscovery = suggestion.SellerTaxCode ?? invoice.NbMst;
        if (!string.IsNullOrWhiteSpace(providerForDiscovery) &&
            !string.IsNullOrWhiteSpace(sellerForDiscovery))
        {
            var discovery = await _providerDomainDiscovery
                .ResolveAsync(companyId, providerForDiscovery, sellerForDiscovery, cancellationToken)
                .ConfigureAwait(false);
            if (discovery.Found && !string.IsNullOrWhiteSpace(discovery.SearchUrl))
                return suggestion with
                {
                    SearchUrl = discovery.SearchUrl,
                    RequiresDomainInput = false,
                    ProviderTaxCode = providerForDiscovery,
                    SellerTaxCode = sellerForDiscovery
                };
            if (discovery.RequiresUserInput)
                return suggestion with
                {
                    RequiresDomainInput = true,
                    ProviderTaxCode = providerForDiscovery,
                    SellerTaxCode = sellerForDiscovery
                };
        }
        return suggestion;
    }

    public Task<InvoicePdfResult> GetPdfForInvoiceAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Task.FromResult<InvoicePdfResult>(new InvoicePdfResult.Failure("Payload hóa đơn trống."));

        var providerKey = GetProviderKeyFromPayload(payloadJson);
        var ctx = new InvoiceContentContext(payloadJson, payloadJson, null, providerKey, null);
        return _orchestrator.AcquirePdfAsync(ctx, cancellationToken);
    }

    public Task SaveProviderDomainOverrideAsync(
        Guid companyId,
        string providerTaxCode,
        string sellerTaxCode,
        string searchUrl,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        return _providerDomainDiscovery.SaveOverrideAsync(
            companyId,
            providerTaxCode,
            sellerTaxCode,
            searchUrl,
            providerName,
            cancellationToken);
    }

    private async Task<InvoiceContentContext?> TryBuildContentForPdfAsync(
        Guid companyId,
        Invoice invoice,
        InvoicePdfProviderMetadata metadata,
        CancellationToken cancellationToken)
    {
        var json = invoice.PayloadJson!;
        var providerKey = metadata.ProviderTaxCode ?? GetProviderKeyFromPayload(json) ?? invoice.ProviderTaxCode;

        // Mặc định truyền JSON payload; chỉ fetcher khai báo RequiresXml mới dùng XML.
        if (!metadata.RequiresXml)
        {
            return new InvoiceContentContext(
                json,
                json,
                invoice.NbMst,
                providerKey,
                companyId,
                InvoiceFetcherContentKind.Json,
                false,
                null);
        }

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
                companyId,
                InvoiceFetcherContentKind.Xml,
                false,
                null);
        }

        var failureReason = string.IsNullOrWhiteSpace(xmlPreparation.FailureReason)
            ? "Không tìm thấy XML"
            : xmlPreparation.FailureReason;
        _logger.LogInformation(
            "PDF XML-required: không thể chuẩn bị XML cho invoice {ExternalId}. Provider={ProviderTaxCode}, Fetcher={FetcherType}, XmlReason={Reason}",
            invoice.ExternalId,
            metadata.ProviderTaxCode ?? providerKey ?? "(null)",
            metadata.FetcherTypeName ?? "(unknown)",
            failureReason);
        return null;
    }

    private static string? GetProviderKeyFromPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            foreach (var candidate in GetProviderRootCandidates(r))
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var prop in candidate.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, "msttcgp", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(prop.Name, "tvanDnKntt", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(prop.Name, "tvandnkntt", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var s = JsonTaxFieldReader.CoerceToTrimmedString(prop.Value);
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> GetProviderRootCandidates(JsonElement r)
    {
        yield return r;
        if (r.ValueKind != JsonValueKind.Object) yield break;

        if (r.TryGetProperty("ndhdon", out var ndhdon) && ndhdon.ValueKind == JsonValueKind.Object)
            yield return ndhdon;
        if (r.TryGetProperty("hdon", out var hdon) && hdon.ValueKind == JsonValueKind.Object)
            yield return hdon;
        if (r.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
                yield return data;
            else if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                yield return data[0];
        }
    }

}
