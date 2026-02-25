using SmartInvoice.Core.Domain;
using SmartInvoice.Core;

namespace SmartInvoice.Core.Repositories;

public interface IInvoiceRepository
{
    Task<Invoice?> GetByExternalIdAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Invoice>> GetByCompanyIdAsync(Guid companyId, CancellationToken cancellationToken = default);

    /// <summary>Lấy một trang hóa đơn theo bộ lọc và tổng số bản ghi (không load hết vào RAM).</summary>
    Task<(IReadOnlyList<Invoice> Page, int TotalCount)> GetPagedAsync(Guid companyId, InvoiceListFilter filter, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Tính tổng hợp (count, sum) theo bộ lọc bằng SQL, không load toàn bộ.</summary>
    Task<InvoiceSummary> GetSummaryAsync(Guid companyId, InvoiceListFilter filter, CancellationToken cancellationToken = default);

    Task<Invoice> AddAsync(Invoice invoice, CancellationToken cancellationToken = default);
    Task UpdateAsync(Invoice invoice, CancellationToken cancellationToken = default);
    Task UpsertAsync(Invoice invoice, CancellationToken cancellationToken = default);
}
