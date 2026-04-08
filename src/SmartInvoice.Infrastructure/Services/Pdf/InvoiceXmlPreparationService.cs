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
    private readonly IInvoiceXmlLocator _xmlLocator;
    private readonly IInvoiceXmlFileNamingStrategy _xmlNamingStrategy;
    private readonly IInvoiceStoragePathPolicy _storagePathPolicy;
    private readonly ILogger _logger;

    public InvoiceXmlPreparationService(
        IUnitOfWork uow,
        IInvoiceSyncService invoiceSyncService,
        IInvoiceXmlLocator xmlLocator,
        IInvoiceXmlFileNamingStrategy xmlNamingStrategy,
        IInvoiceStoragePathPolicy storagePathPolicy,
        ILoggerFactory loggerFactory)
    {
        _uow = uow ?? throw new ArgumentNullException(nameof(uow));
        _invoiceSyncService = invoiceSyncService ?? throw new ArgumentNullException(nameof(invoiceSyncService));
        _xmlLocator = xmlLocator ?? throw new ArgumentNullException(nameof(xmlLocator));
        _xmlNamingStrategy = xmlNamingStrategy ?? throw new ArgumentNullException(nameof(xmlNamingStrategy));
        _storagePathPolicy = storagePathPolicy ?? throw new ArgumentNullException(nameof(storagePathPolicy));
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
        var xmlRoot = _storagePathPolicy.GetCompanyXmlRoot(companyCode);
        var companyRoot = _storagePathPolicy.GetCompanyRoot(companyCode);
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

            // Đồng bộ ngay base-name vừa dùng để tải XML vào entity hiện tại,
            // tránh mismatch khi object invoice chưa được refresh từ DB.
            var downloadedInvoice = displayList.FirstOrDefault();
            if (downloadedInvoice != null)
            {
                // Luôn cập nhật base-name theo invoice vừa tải để locator không bị lệch state entity cũ.
                invoice.XmlBaseName = _xmlNamingStrategy.BuildBaseName(downloadedInvoice);
            }

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

            // Fallback cuối cùng: vì vừa tải đúng 1 hóa đơn, thử đọc trực tiếp đường dẫn expected theo display invoice.
            if (downloadedInvoice != null)
            {
                var expectedBaseName = _xmlNamingStrategy.BuildBaseName(downloadedInvoice);
                var expectedMonthFolder = Path.Combine(xmlRoot, InvoiceFileStoragePathHelper.GetMonthYearFolderName(downloadedInvoice.NgayLap));
                var expectedXmlPath = Path.Combine(expectedMonthFolder, expectedBaseName + ".xml");
                if (File.Exists(expectedXmlPath))
                {
                    var xml = await File.ReadAllTextAsync(expectedXmlPath, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(xml))
                    {
                        return new InvoiceXmlPreparationResult(
                            InvoiceXmlPreparationStatus.Downloaded,
                            xml,
                            expectedXmlPath,
                            null);
                    }
                }
            }

            if (dl.NoXmlCount > 0 && dl.DownloadedCount == 0)
            {
                return new InvoiceXmlPreparationResult(
                    InvoiceXmlPreparationStatus.Failed,
                    null,
                    null,
                    "API không trả XML cho hóa đơn này (không tồn tại hồ sơ gốc hoặc XML chưa sẵn sàng).");
            }

            if (dl.FailedCount > 0 && dl.DownloadedCount == 0)
            {
                return new InvoiceXmlPreparationResult(
                    InvoiceXmlPreparationStatus.Failed,
                    null,
                    null,
                    string.IsNullOrWhiteSpace(dl.Message) ? "Tải XML thất bại." : dl.Message);
            }

            var reason = string.IsNullOrWhiteSpace(dl.Message)
                ? "Đã gọi tải XML nhưng không đọc được file XML từ thư mục lưu trữ."
                : dl.Message;
            return new InvoiceXmlPreparationResult(InvoiceXmlPreparationStatus.Failed, null, null, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prepare XML thất bại cho invoice {ExternalId}", invoice.ExternalId);
            return new InvoiceXmlPreparationResult(InvoiceXmlPreparationStatus.Failed, null, null, ex.Message);
        }
    }

    private async Task<(string? XmlContent, string? XmlPath)> TryReadFromKnownRootsAsync(
        IReadOnlyList<string> roots,
        Invoice invoice,
        CancellationToken cancellationToken)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var found = await _xmlLocator.TryReadInvoiceXmlAsync(root, invoice, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(found.XmlContent))
                return found;
        }

        return (null, null);
    }
}
