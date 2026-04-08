using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartInvoice.Core;
using SmartInvoice.Core.Domain;
using SmartInvoice.Core.Repositories;

namespace SmartInvoice.Infrastructure.Persistence;

public class InvoiceRepository : IInvoiceRepository, IBackgroundJobRepository
{
    private readonly AppDbContext _db;

    private static void EnsureSqliteDiacriticsFunction(DbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            conn.Open();
        if (conn is SqliteConnection sqlite)
            sqlite.CreateFunction("remove_diacritics", (string? s) => DiacriticsHelper.RemoveDiacritics(s));
    }

    public InvoiceRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Invoice?> GetByExternalIdAsync(Guid companyId, string externalId, CancellationToken cancellationToken = default)
    {
        return await _db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(i => i.CompanyId == companyId && i.ExternalId == externalId, cancellationToken);
    }

    public async Task<IReadOnlyList<Invoice>> GetByCompanyAndExternalIdsAsync(Guid companyId, IReadOnlyList<string> externalIds, CancellationToken cancellationToken = default)
    {
        if (externalIds == null || externalIds.Count == 0)
            return Array.Empty<Invoice>();
        var ids = externalIds.Distinct().ToList();
        return await _db.Invoices.AsNoTracking()
            .Where(i => i.CompanyId == companyId && ids.Contains(i.ExternalId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Invoice>> GetByCompanyIdAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        return await _db.Invoices.AsNoTracking()
            .Where(i => i.CompanyId == companyId)
            .OrderByDescending(i => i.SyncedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Invoice> Page, int TotalCount)> GetPagedAsync(Guid companyId, InvoiceListFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        var q = BuildFilteredQuery(companyId, filter);
        var totalCount = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        q = ApplyOrderBy(q, filter.SortBy, filter.SortDescending);
        var page = await q
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return (page, totalCount);
    }

    /// <summary>Sắp xếp theo cột: ngày theo DateTime, số theo number, chữ theo string. Null = mặc định NgayLap giảm dần.</summary>
    private static IQueryable<Invoice> ApplyOrderBy(IQueryable<Invoice> q, string? sortBy, bool sortDescending)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
        {
            return q.OrderByDescending(i => i.NgayLap ?? i.SyncedAt).ThenByDescending(i => i.SyncedAt);
        }
        return sortBy switch
        {
            "NgayLap" => sortDescending
                ? q.OrderByDescending(i => i.NgayLap ?? i.SyncedAt).ThenByDescending(i => i.SyncedAt)
                : q.OrderBy(i => i.NgayLap ?? i.SyncedAt).ThenBy(i => i.SyncedAt),
            "KyHieu" => sortDescending ? q.OrderByDescending(i => i.KyHieu) : q.OrderBy(i => i.KyHieu),
            "SoHoaDon" => sortDescending ? q.OrderByDescending(i => i.SoHoaDon) : q.OrderBy(i => i.SoHoaDon),
            "NguoiBan" => sortDescending ? q.OrderByDescending(i => i.NguoiBan) : q.OrderBy(i => i.NguoiBan),
            "NguoiMua" => sortDescending ? q.OrderByDescending(i => i.NguoiMua) : q.OrderBy(i => i.NguoiMua),
            "Tthai" => sortDescending ? q.OrderByDescending(i => i.Tthai) : q.OrderBy(i => i.Tthai),
            // SQLite không hỗ trợ ORDER BY trực tiếp trên decimal, nên cast sang double giống phần SUM.
            "Tgtcthue" => sortDescending
                ? q.OrderByDescending(i => (double)(i.Tgtcthue ?? 0)).ThenBy(i => i.Id)
                : q.OrderBy(i => (double)(i.Tgtcthue ?? 0)).ThenBy(i => i.Id),
            "Tgtthue" => sortDescending
                ? q.OrderByDescending(i => (double)(i.Tgtthue ?? 0)).ThenBy(i => i.Id)
                : q.OrderBy(i => (double)(i.Tgtthue ?? 0)).ThenBy(i => i.Id),
            "TongTien" => sortDescending
                ? q.OrderByDescending(i => (double)(i.TongTien ?? 0)).ThenBy(i => i.Id)
                : q.OrderBy(i => (double)(i.TongTien ?? 0)).ThenBy(i => i.Id),
            _ => q.OrderByDescending(i => i.NgayLap ?? i.SyncedAt).ThenByDescending(i => i.SyncedAt)
        };
    }

    public async Task<InvoiceSummary> GetSummaryAsync(Guid companyId, InvoiceListFilter filter, CancellationToken cancellationToken = default)
    {
        var q = BuildFilteredQuery(companyId, filter);
        var totalCount = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var countCoMa = await q.CountAsync(i => i.CoMa, cancellationToken).ConfigureAwait(false);
        var countMayTinhTien = await q.CountAsync(i => i.MayTinhTien, cancellationToken).ConfigureAwait(false);
        var countKhongMa = await q.CountAsync(i => !i.CoMa && !i.MayTinhTien, cancellationToken).ConfigureAwait(false);
        // SQLite không hỗ trợ SUM(decimal) — dùng double trong query rồi ép về decimal
        var totalChuaThue = (decimal)await q.SumAsync(i => (double)(i.Tgtcthue ?? 0), cancellationToken).ConfigureAwait(false);
        var totalTienThue = (decimal)await q.SumAsync(i => (double)(i.Tgtthue ?? 0), cancellationToken).ConfigureAwait(false);
        var totalThanhTien = (decimal)await q.SumAsync(i => (double)(i.TongTien ?? 0), cancellationToken).ConfigureAwait(false);
        return new InvoiceSummary(totalCount, countCoMa, countKhongMa, countMayTinhTien, totalChuaThue, totalTienThue, totalThanhTien);
    }

    private IQueryable<Invoice> BuildFilteredQuery(Guid companyId, InvoiceListFilter filter)
    {
        var needDiacritics = !string.IsNullOrWhiteSpace(filter.SearchText) ||
                             !string.IsNullOrWhiteSpace(filter.FilterTenNguoiBan) ||
                             !string.IsNullOrWhiteSpace(filter.FilterLoaiTruBenBan);
        if (needDiacritics)
            EnsureSqliteDiacriticsFunction(_db);

        var q = _db.Invoices.AsNoTracking().Where(i => i.CompanyId == companyId);

        if (filter.FromDate.HasValue || filter.ToDate.HasValue)
        {
            if (filter.FromDate.HasValue && filter.ToDate.HasValue)
                q = q.Where(i => i.NgayLap == null || (i.NgayLap >= filter.FromDate && i.NgayLap <= filter.ToDate));
            else if (filter.FromDate.HasValue)
                q = q.Where(i => i.NgayLap == null || i.NgayLap >= filter.FromDate);
            else
                q = q.Where(i => i.NgayLap == null || i.NgayLap <= filter.ToDate);
        }

        if (filter.IsSold.HasValue)
            q = q.Where(i => i.IsSold == filter.IsSold.Value);

        if (filter.Tthai.HasValue)
            q = q.Where(i => i.Tthai == filter.Tthai.Value);

        switch (filter.LoaiHoaDon)
        {
            case 1: q = q.Where(i => i.CoMa); break;
            case 2: q = q.Where(i => !i.CoMa && !i.MayTinhTien); break;
            case 3: q = q.Where(i => i.MayTinhTien); break;
            case 4: q = q.Where(i => i.Dvtte != null && i.Dvtte.ToLower() != "vnd" && i.Dvtte.ToLower() != "vnđ"); break;
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var s = filter.SearchText.Trim();
            var sLower = s.ToLowerInvariant();
            var sNorm = DiacriticsHelper.RemoveDiacritics(s).ToLowerInvariant();
            var addSoHoaDon = int.TryParse(s, out var searchNum);
            q = q.Where(i =>
                (i.KyHieu != null && i.KyHieu.ToLower().Contains(sLower)) ||
                (i.NbMst != null && i.NbMst.ToLower().Contains(sLower)) ||
                (i.ProviderTaxCode != null && i.ProviderTaxCode.ToLower().Contains(sLower)) ||
                (i.TvanTaxCode != null && i.TvanTaxCode.ToLower().Contains(sLower)) ||
                (i.NguoiBan != null && DiacriticsHelper.RemoveDiacriticsSql(i.NguoiBan).ToLower().Contains(sNorm)) ||
                (i.NguoiMua != null && DiacriticsHelper.RemoveDiacriticsSql(i.NguoiMua).ToLower().Contains(sNorm)) ||
                (i.MstMua != null && i.MstMua.ToLower().Contains(sLower)) ||
                (addSoHoaDon && i.SoHoaDon == searchNum));
        }

        if (!string.IsNullOrWhiteSpace(filter.FilterKyHieu))
        {
            var k = filter.FilterKyHieu.Trim();
            q = q.Where(i => i.KyHieu != null && i.KyHieu.ToLower().Contains(k.ToLowerInvariant()));
        }
        if (!string.IsNullOrWhiteSpace(filter.FilterSoHoaDon))
        {
            var so = filter.FilterSoHoaDon.Trim();
            if (int.TryParse(so, out var soNum))
                q = q.Where(i => i.SoHoaDon == soNum);
        }
        if (!string.IsNullOrWhiteSpace(filter.FilterMstNguoiBan))
        {
            var m = filter.FilterMstNguoiBan.Trim().ToLowerInvariant();
            q = q.Where(i => i.NbMst != null && i.NbMst.ToLower().Contains(m));
        }
        if (!string.IsNullOrWhiteSpace(filter.FilterTenNguoiBan))
        {
            var t = filter.FilterTenNguoiBan.Trim();
            var tNorm = DiacriticsHelper.RemoveDiacritics(t).ToLowerInvariant();
            q = q.Where(i => i.NguoiBan != null && DiacriticsHelper.RemoveDiacriticsSql(i.NguoiBan).ToLower().Contains(tNorm));
        }
        if (!string.IsNullOrWhiteSpace(filter.FilterMstLoaiTru))
        {
            var excludeList = filter.FilterMstLoaiTru.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (excludeList.Count > 0)
                q = q.Where(i => i.NbMst == null || !excludeList.Contains(i.NbMst));
        }
        if (!string.IsNullOrWhiteSpace(filter.FilterLoaiTruBenBan))
        {
            var ex = filter.FilterLoaiTruBenBan.Trim();
            var exNorm = DiacriticsHelper.RemoveDiacritics(ex).ToLowerInvariant();
            q = q.Where(i => i.NguoiBan == null || !DiacriticsHelper.RemoveDiacriticsSql(i.NguoiBan).ToLower().Contains(exNorm));
        }

        if (!string.IsNullOrWhiteSpace(filter.FilterProviderTaxCode))
        {
            var p = filter.FilterProviderTaxCode.Trim();
            q = q.Where(i => i.ProviderTaxCode == p);
        }

        if (!string.IsNullOrWhiteSpace(filter.FilterTvanTaxCode))
        {
            var t = filter.FilterTvanTaxCode.Trim();
            q = q.Where(i => i.TvanTaxCode == t);
        }

        return q;
    }

    public async Task<Invoice> AddAsync(Invoice invoice, CancellationToken cancellationToken = default)
    {
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(cancellationToken);
        return invoice;
    }

    public async Task UpdateAsync(Invoice invoice, CancellationToken cancellationToken = default)
    {
        // Nếu trong DbContext đã có một entity Invoice với cùng Id đang được track,
        // thì cập nhật giá trị trên entity đó để tránh lỗi "same key is already being tracked".
        var tracked = _db.Invoices.Local.FirstOrDefault(i => i.Id == invoice.Id);
        if (tracked != null)
        {
            _db.Entry(tracked).CurrentValues.SetValues(invoice);
        }
        else
        {
            var entry = _db.Invoices.Attach(invoice);
            entry.State = EntityState.Modified;
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertAsync(Invoice invoice, CancellationToken cancellationToken = default)
    {
        var existing = await _db.Invoices
            .FirstOrDefaultAsync(i => i.CompanyId == invoice.CompanyId && i.ExternalId == invoice.ExternalId, cancellationToken);
        if (existing == null)
        {
            _db.Invoices.Add(invoice);
        }
        else
        {
            var oldStatus = existing.XmlStatus;
            var oldTthai = existing.Tthai;

            existing.PayloadJson = invoice.PayloadJson;
            existing.LineItemsJson = invoice.LineItemsJson;
            existing.SyncedAt = invoice.SyncedAt;
            existing.UpdatedAt = invoice.UpdatedAt;
            existing.IsSold = invoice.IsSold;
            existing.NgayLap = invoice.NgayLap;
            existing.Tthai = invoice.Tthai;
            existing.Tgtcthue = invoice.Tgtcthue;
            existing.Tgtthue = invoice.Tgtthue;
            existing.TongTien = invoice.TongTien;
            existing.CoMa = invoice.CoMa;
            existing.MayTinhTien = invoice.MayTinhTien;
            existing.KyHieu = invoice.KyHieu;
            existing.SoHoaDon = invoice.SoHoaDon;
            existing.NbMst = invoice.NbMst;
            existing.NguoiBan = invoice.NguoiBan;
            existing.NguoiMua = invoice.NguoiMua;
            existing.MstMua = invoice.MstMua;
            existing.Dvtte = invoice.Dvtte;
            existing.ProviderTaxCode = invoice.ProviderTaxCode;
            existing.TvanTaxCode = invoice.TvanTaxCode;

            // Nếu trạng thái hóa đơn thay đổi sau khi đồng bộ lại thì reset XML để lần tải sau biết phải tải lại.
            if (oldStatus is 1 or 2 && oldTthai != existing.Tthai)
            {
                existing.XmlStatus = 0;
                existing.XmlBaseName = null;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    // IBackgroundJobRepository implementation (dùng chung DbContext).

    public async Task<BackgroundJob> AddAsync(BackgroundJob job, CancellationToken cancellationToken = default)
    {
        _db.BackgroundJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task UpdateAsync(BackgroundJob job, CancellationToken cancellationToken = default)
    {
        var entry = _db.BackgroundJobs.Attach(job);
        entry.State = EntityState.Modified;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BackgroundJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.BackgroundJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackgroundJob>> GetPendingAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        return await _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Status == BackgroundJobStatus.Pending)
            .OrderBy(j => j.CreatedAt)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BackgroundJob?> TryClaimNextRunnableJobAsync(int maxConcurrentGlobal, CancellationToken cancellationToken = default)
    {
        if (maxConcurrentGlobal < 1)
            return null;

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runningCount = await _db.BackgroundJobs
                .AsNoTracking()
                .CountAsync(j => j.Status == BackgroundJobStatus.Running, cancellationToken)
                .ConfigureAwait(false);
            if (runningCount >= maxConcurrentGlobal)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var busyCompanyIds = await _db.BackgroundJobs
                .AsNoTracking()
                .Where(j => j.Status == BackgroundJobStatus.Running)
                .Select(j => j.CompanyId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var next = await _db.BackgroundJobs
                .Where(j => j.Status == BackgroundJobStatus.Pending && !busyCompanyIds.Contains(j.CompanyId))
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (next == null)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            next.Status = BackgroundJobStatus.Running;
            next.StartedAt = DateTime.Now;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return next;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<BackgroundJob>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        return await _db.BackgroundJobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BackgroundJob?> FindActiveScoRecoveryJobAsync(Guid companyId, DateTime fromDate, DateTime toDate, bool isSold, CancellationToken cancellationToken = default)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return await _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Type == BackgroundJobType.ScoRecovery
                        && j.CompanyId == companyId
                        && j.IsSold == isSold
                        && j.FromDate == from
                        && j.ToDate == to
                        && (j.Status == BackgroundJobStatus.Pending || j.Status == BackgroundJobStatus.Running))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var job = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (job == null) return;
        _db.BackgroundJobs.Remove(job);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
