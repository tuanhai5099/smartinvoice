using System.Net;
using System.Net.Http;
using SmartInvoice.Application.Services;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;
using SmartInvoice.Infrastructure.Services.Pdf;
using Xunit;

namespace SmartInvoice.Infrastructure.Tests;

public sealed class ProviderDomainDiscoveryServiceTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsConfiguredMapping_First()
    {
        var repo = new StubProviderDomainMappingRepository
        {
            Existing = new ProviderDomainMapping
            {
                CompanyId = Guid.NewGuid(),
                ProviderTaxCode = "0100684378",
                SellerTaxCode = "0304741634",
                SearchUrl = "https://configured.vnpt-invoice.com.vn/Portal/Index/",
                IsActive = true
            }
        };
        var uow = new StubUnitOfWork(repo);
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var sut = new ProviderDomainDiscoveryService(uow, http);

        var result = await sut.ResolveAsync(repo.Existing.CompanyId, "0100684378", "0304741634");
        Assert.True(result.Found);
        Assert.Equal("configured", result.Source);
        Assert.Equal(repo.Existing.SearchUrl, result.SearchUrl);
    }

    [Fact]
    public async Task ResolveAsync_Vnpt_UsesStaticCatalog_ForPetrolimexSeller()
    {
        var repo = new StubProviderDomainMappingRepository();
        var uow = new StubUnitOfWork(repo);
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var sut = new ProviderDomainDiscoveryService(uow, http);

        var result = await sut.ResolveAsync(Guid.NewGuid(), "0100684378", "0300555450");
        Assert.True(result.Found);
        Assert.Equal("vnpt-seller-catalog", result.Source);
        Assert.Equal("https://hoadon.petrolimex.com.vn/", result.SearchUrl);
    }

    [Fact]
    public async Task ResolveAsync_Vnpt_UsesProbe_WhenNoConfig()
    {
        var repo = new StubProviderDomainMappingRepository();
        var uow = new StubUnitOfWork(repo);
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var sut = new ProviderDomainDiscoveryService(uow, http);

        // MST không có trong catalog tĩnh → probe subdomain VNPT.
        var result = await sut.ResolveAsync(Guid.NewGuid(), "0100684378", "0998877665");
        Assert.True(result.Found);
        Assert.Equal("vnpt-probe", result.Source);
        Assert.Contains("0998877665-tt78.vnpt-invoice.com.vn", result.SearchUrl);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubUnitOfWork : IUnitOfWork
    {
        public StubUnitOfWork(IProviderDomainMappingRepository mappings) => ProviderDomainMappings = mappings;
        public ICompanyRepository Companies => throw new NotSupportedException();
        public IInvoiceRepository Invoices => throw new NotSupportedException();
        public IBackgroundJobRepository BackgroundJobs => throw new NotSupportedException();
        public IProviderDomainMappingRepository ProviderDomainMappings { get; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubProviderDomainMappingRepository : IProviderDomainMappingRepository
    {
        public ProviderDomainMapping? Existing { get; set; }
        public Task<ProviderDomainMapping?> GetActiveAsync(Guid companyId, string providerTaxCode, string sellerTaxCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(Existing is not null &&
                            Existing.CompanyId == companyId &&
                            Existing.ProviderTaxCode == providerTaxCode &&
                            Existing.SellerTaxCode == sellerTaxCode
                ? Existing
                : null);
        public Task UpsertAsync(ProviderDomainMapping mapping, CancellationToken cancellationToken = default)
        {
            Existing = mapping;
            return Task.CompletedTask;
        }
    }
}
