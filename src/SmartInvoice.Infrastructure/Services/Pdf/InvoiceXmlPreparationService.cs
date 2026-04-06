using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;
using SmartInvoice.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace SmartInvoice.Infrastructure.Services.Pdf;

public enum InvoiceXmlPreparationStatus
{
    FoundExisting = 0,
    Downloaded = 1,
    Failed = 2
}

public sealed record InvoiceXmlPreparationResult(
    InvoiceXmlPreparationStatus Status,
    string? XmlContent,
    string? XmlFilePath,
    string? FailureReason)
{
    public bool HasXml => !string.IsNullOrWhiteSpace(XmlContent);
}

public interface IInvoiceXmlPreparationService
{
    Task<InvoiceXmlPreparationResult> PrepareXmlForInvoiceAsync(
        Guid companyId,
        Invoice invoice,
        CancellationToken cancellationToken = default);
}

public sealed class InvoiceXmlPreparationService : IInvoiceXmlPreparationService
{
    private readonly IUnitOfWork _uow;
    private readonly IInvoiceSyncService _invoiceSyncService;
    private readonly ILogger _logger;

    public InvoiceXmlPreparationService(
        IUnitOfWork uow,
        IInvoiceSyncService invoiceSyncService,
        ILoggerFactory loggerFactory)
    {
        _uow = uow ?? throw new ArgumentNullException(nameof(uow));
        _invoiceSyncService = invoiceSyncService ?? throw new ArgumentNullException(nameof(invoiceSyncService));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(nameof(InvoiceXmlPreparationService));
    }

    public async Task<InvoiceXmlPreparationResult> PrepareXmlForInvoiceAsync(
        Guid companyId,
        Invoice invoice,
        CancellationToken cancellationToken = default)
    {
        var company = await _uow.Companies.GetByIdTrackedAsync(companyId, cancellationToken).ConfigureAwait(false);
        var companyCode = company?.CompanyCode ?? company?.CompanyName ?? company?.TaxCode ?? companyId.ToString("N")[..8];
        var xmlRoot = InvoiceFileStoragePathHelper.GetCompanyXmlRootPath(companyCode);
        var companyRoot = InvoiceFileStoragePathHelper.GetCompanyRootPath(companyCode);
        var exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartInvoice", "ExportXml");

        var existing = await TryReadFromKnownRootsAsync(
            new[] { xmlRoot, companyRoot, exportRoot },
            invoice,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing.XmlContent))
            return new InvoiceXmlPreparationResult(InvoiceXmlPreparationStatus.FoundExisting, existing.XmlContent, existing.XmlPath, null);

        try
        {
            var displayList = await _invoiceSyncService
                .GetInvoicesByIdsAsync(companyId, new[] { invoice.ExternalId }, cancellationToken)
                .ConfigureAwait(false);
            if (displayList.Count == 0)
            {
                return new InvoiceXmlPreparationResult(
                    InvoiceXmlPreparationStatus.Failed,
                    null,
                    null,
                    "Không tìm thấy hóa đơn để tải XML.");
            }

            var dl = await _invoiceSyncService
                .DownloadInvoicesXmlAsync(
                    companyId,
                    displayList,
                    xmlRoot,
                    progress: null,
                    cancellationToken,
                    zipOutputDirectory: companyRoot)
                .ConfigureAwait(false);

            var afterDownload = await TryReadFromKnownRootsAsync(
                new[] { xmlRoot, companyRoot, exportRoot },
                invoice,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(afterDownload.XmlContent))
            {
                return new InvoiceXmlPreparationResult(
                    InvoiceXmlPreparationStatus.Downloaded,
                    afterDownload.XmlContent,
                    afterDownload.XmlPath,
                    null);
            }

            var reason = string.IsNullOrWhiteSpace(dl.Message)
                ? "Không tìm thấy file XML sau khi gọi tải XML."
                : dl.Message;
            return new InvoiceXmlPreparationResult(InvoiceXmlPreparationStatus.Failed, null, null, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prepare XML thất bại cho invoice {ExternalId}", invoice.ExternalId);
            return new InvoiceXmlPreparationResult(InvoiceXmlPreparationStatus.Failed, null, null, ex.Message);
        }
    }

    private static async Task<(string? XmlContent, string? XmlPath)> TryReadFromKnownRootsAsync(
        IReadOnlyList<string> roots,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var found = await TryReadInvoiceXmlAsync(root, invoice, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(found.XmlContent))
                return found;
        }

        return (null, null);
    }

    private static async Task<(string? XmlContent, string? XmlPath)> TryReadInvoiceXmlAsync(
        string companyRoot,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        static string GetBaseName(Invoice inv)
        {
            var kh = inv.KyHieu ?? "";
            kh = InvoiceFileStoragePathHelper.SanitizeFileName(kh);
            return $"{kh}-{inv.SoHoaDon}";
        }

        var baseName = string.IsNullOrWhiteSpace(invoice.XmlBaseName)
            ? GetBaseName(invoice)
            : invoice.XmlBaseName!;

        var monthFolder = InvoiceFileStoragePathHelper.GetMonthYearPath(companyRoot, invoice.NgayLap);
        string? xmlPath = null;

        var destDir = Path.Combine(monthFolder, baseName);
        if (Directory.Exists(destDir))
            xmlPath = Directory.EnumerateFiles(destDir, "*.xml", SearchOption.AllDirectories).FirstOrDefault();

        if (xmlPath == null)
        {
            var rawXmlPath = Path.Combine(monthFolder, baseName + ".xml");
            if (File.Exists(rawXmlPath))
                xmlPath = rawXmlPath;
        }

        if (xmlPath == null)
        {
            try
            {
                if (Directory.Exists(companyRoot))
                {
                    var pattern = baseName + "*.xml";
                    xmlPath = Directory.GetFiles(companyRoot, pattern, SearchOption.AllDirectories).FirstOrDefault();
                }
            }
            catch
            {
                // ignored
            }
        }

        if (xmlPath == null || !File.Exists(xmlPath))
            return (null, null);

        var xml = await File.ReadAllTextAsync(xmlPath, cancellationToken).ConfigureAwait(false);
        return (xml, xmlPath);
    }
}
