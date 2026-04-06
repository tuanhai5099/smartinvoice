using Microsoft.Extensions.Logging.Abstractions;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;
using SmartInvoice.Infrastructure.Services.Pdf;
using Xunit;

namespace SmartInvoice.Infrastructure.Tests;

public sealed class InvoicePdfServiceXmlFirstTests
{
    private static readonly Guid CompanyId = Guid.NewGuid();

    [Fact]
    public async Task GetPdfByExternalId_UsesXml_WhenExistingXmlFound()
    {
        var orchestrator = new StubOrchestrator(new InvoicePdfResult.Success([1, 2, 3], "a.pdf"));
        var resolver = new StubResolver(requiresXml: false);
        var xmlPrep = new StubXmlPreparationService(new InvoiceXmlPreparationResult(
            InvoiceXmlPreparationStatus.FoundExisting,
            "<xml>ok</xml>",
            "x.xml",
            null));
        var service = CreateService(orchestrator, resolver, xmlPrep);

        var result = await service.GetPdfForInvoiceByExternalIdAsync(CompanyId, "inv-1");

        var success = Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.Equal("a.pdf", success.SuggestedFileName);
        Assert.NotNull(orchestrator.LastContext);
        Assert.Equal(InvoiceFetcherContentKind.Xml, orchestrator.LastContext!.ContentKind);
        Assert.Equal("<xml>ok</xml>", orchestrator.LastContext.ContentForFetcher);
    }

    [Fact]
    public async Task GetPdfByExternalId_UsesXml_WhenXmlDownloadedInPreparation()
    {
        var orchestrator = new StubOrchestrator(new InvoicePdfResult.Success([9], "b.pdf"));
        var resolver = new StubResolver(requiresXml: true);
        var xmlPrep = new StubXmlPreparationService(new InvoiceXmlPreparationResult(
            InvoiceXmlPreparationStatus.Downloaded,
            "<xml>downloaded</xml>",
            "y.xml",
            null));
        var service = CreateService(orchestrator, resolver, xmlPrep);

        var result = await service.GetPdfForInvoiceByExternalIdAsync(CompanyId, "inv-1");

        Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.NotNull(orchestrator.LastContext);
        Assert.Equal(InvoiceFetcherContentKind.Xml, orchestrator.LastContext!.ContentKind);
        Assert.Equal("<xml>downloaded</xml>", orchestrator.LastContext.ContentForFetcher);
    }

    [Fact]
    public async Task GetPdfByExternalId_FallbacksToJson_WhenXmlPreparationFails_AndProviderNotRequireXml()
    {
        var orchestrator = new StubOrchestrator(new InvoicePdfResult.Success([5], "c.pdf"));
        var resolver = new StubResolver(requiresXml: false);
        var xmlPrep = new StubXmlPreparationService(new InvoiceXmlPreparationResult(
            InvoiceXmlPreparationStatus.Failed,
            null,
            null,
            "API timeout"));
        var service = CreateService(orchestrator, resolver, xmlPrep);

        var result = await service.GetPdfForInvoiceByExternalIdAsync(CompanyId, "inv-1");

        Assert.IsType<InvoicePdfResult.Success>(result);
        Assert.NotNull(orchestrator.LastContext);
        Assert.Equal(InvoiceFetcherContentKind.Json, orchestrator.LastContext!.ContentKind);
        Assert.True(orchestrator.LastContext.UsedJsonFallbackAfterXmlFailure);
        Assert.Equal("API timeout", orchestrator.LastContext.XmlPreparationFailureReason);
    }

    [Fact]
    public async Task GetPdfByExternalId_FailsEarly_WhenXmlPreparationFails_AndProviderRequiresXml()
    {
        var orchestrator = new StubOrchestrator(new InvoicePdfResult.Success([6], "d.pdf"));
        var resolver = new StubResolver(requiresXml: true);
        var xmlPrep = new StubXmlPreparationService(new InvoiceXmlPreparationResult(
            InvoiceXmlPreparationStatus.Failed,
            null,
            null,
            "No XML"));
        var service = CreateService(orchestrator, resolver, xmlPrep);

        var result = await service.GetPdfForInvoiceByExternalIdAsync(CompanyId, "inv-1");

        var failure = Assert.IsType<InvoicePdfResult.Failure>(result);
        Assert.Contains("chuẩn bị dữ liệu XML", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(orchestrator.LastContext);
    }

    private static InvoicePdfService CreateService(
        StubOrchestrator orchestrator,
        StubResolver resolver,
        StubXmlPreparationService xmlPreparationService)
    {
        var invoice = new Invoice
        {
            CompanyId = CompanyId,
            ExternalId = "inv-1",
            NbMst = "0102030405",
            PayloadJson = """{"msttcgp":"0100109106","nbmst":"0102030405"}""",
            KyHieu = "KH",
            SoHoaDon = 1,
            NgayLap = DateTime.Today
        };
        var company = new Company
        {
            Id = CompanyId,
            CompanyCode = "TEST"
        };

        var uow = new StubUnitOfWork(invoice, company);
        return new InvoicePdfService(
            orchestrator,
            resolver,
            uow,
            xmlPreparationService,
            NullLoggerFactory.Instance);
    }

    private sealed class StubResolver : IInvoicePdfProviderResolver
    {
        private readonly bool _requiresXml;
        public StubResolver(bool requiresXml) => _requiresXml = requiresXml;
        public IInvoicePdfFetcher ResolveFetcher(string payloadJson) => throw new NotSupportedException();
        public InvoicePdfProviderMetadata ResolveMetadata(string payloadJson) =>
            new("0100109106", false, _requiresXml, "0100109106", "0102030405", "0100109106", "StubFetcher");
    }

    private sealed class StubOrchestrator : IInvoiceProviderOrchestrator
    {
        private readonly InvoicePdfResult _result;
        public InvoiceContentContext? LastContext { get; private set; }
        public StubOrchestrator(InvoicePdfResult result) => _result = result;
        public InvoiceLookupSuggestion? ResolveLookup(InvoiceContentContext context) => null;
        public Task<InvoicePdfResult> AcquirePdfAsync(InvoiceContentContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(_result);
        }
    }

    private sealed class StubXmlPreparationService : IInvoiceXmlPreparationService
    {
        private readonly InvoiceXmlPreparationResult _result;
        public StubXmlPreparationService(InvoiceXmlPreparationResult result) => _result = result;
        public Task<InvoiceXmlPreparationResult> PrepareXmlForInvoiceAsync(Guid companyId, Invoice invoice, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class StubUnitOfWork : IUnitOfWork
    {
        public StubUnitOfWork(Invoice invoice, Company company)
        {
            Invoices = new StubInvoiceRepository(invoice);
            Companies = new StubCompanyRepository(company);
            BackgroundJobs = new StubBackgroundJobRepository();
        }

        public ICompanyRepository Companies { get; }
        public IInvoiceRepository Invoices { get; }
        public IBackgroundJobRepository BackgroundJobs { get; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubCompanyRepository(Company company) : ICompanyRepository
    {
        public Task<Company?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Company?>(company);
        public Task<Company?> GetByIdTrackedAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Company?>(company);
        public Task<IReadOnlyList<Company>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Company>>([company]);
        public Task<Company> AddAsync(Company company, CancellationToken cancellationToken = default) => Task.FromResult(company);
        public Task UpdateAsync(Company company, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Company?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default) => Task.FromResult<Company?>(company);
        public Task<Company?> GetByCompanyCodeAsync(string companyCode, CancellationToken cancellationToken = default) => Task.FromResult<Company?>(company);
    }

    private sealed class StubInvoiceRepository(Invoice invoice) : IInvoiceRepository
    {
        public Task<Invoice?> GetByExternalIdAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default) => Task.FromResult<Invoice?>(invoice);
        public Task<IReadOnlyList<Invoice>> GetByCompanyAndExternalIdsAsync(Guid companyId, IReadOnlyList<string> externalIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Invoice>>([invoice]);
        public Task<IReadOnlyList<Invoice>> GetByCompanyIdAsync(Guid companyId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Invoice>>([invoice]);
        public Task<(IReadOnlyList<Invoice> Page, int TotalCount)> GetPagedAsync(Guid companyId, Core.InvoiceListFilter filter, int skip, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult(((IReadOnlyList<Invoice>)[invoice], 1));
        public Task<Core.InvoiceSummary> GetSummaryAsync(Guid companyId, Core.InvoiceListFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Core.InvoiceSummary(1, 1, 0, 0, 0m, 0m, 0m));
        public Task<Invoice> AddAsync(Invoice invoice, CancellationToken cancellationToken = default) => Task.FromResult(invoice);
        public Task UpdateAsync(Invoice invoice, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertAsync(Invoice invoice, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubBackgroundJobRepository : IBackgroundJobRepository
    {
        public Task<BackgroundJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<BackgroundJob?>(null);
        public Task<IReadOnlyList<BackgroundJob>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BackgroundJob>>([]);
        public Task<BackgroundJob?> TryClaimNextRunnableJobAsync(int maxConcurrentGlobal, CancellationToken cancellationToken = default) => Task.FromResult<BackgroundJob?>(null);
        public Task<IReadOnlyList<BackgroundJob>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BackgroundJob>>([]);
        public Task<BackgroundJob?> FindActiveScoRecoveryJobAsync(Guid companyId, DateTime fromDate, DateTime toDate, bool isSold, CancellationToken cancellationToken = default) => Task.FromResult<BackgroundJob?>(null);
        public Task<BackgroundJob> AddAsync(BackgroundJob job, CancellationToken cancellationToken = default) => Task.FromResult(job);
        public Task UpdateAsync(BackgroundJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
