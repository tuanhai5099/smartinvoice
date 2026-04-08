using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartInvoice.Core.Domain;
using SmartInvoice.Infrastructure.Serialization;

namespace SmartInvoice.Infrastructure.Persistence;

/// <summary>
/// Lightweight schema migration for <see cref="AppDbContext"/> when using SQLite + EnsureCreated.
/// Adds missing columns (e.g. LastSyncedAt) without requiring full EF migrations.
/// Host (Bootstrapper) gọi Migrate(db) sau EnsureCreated() để cập nhật schema DB hiện có.
/// </summary>
public static class AppDbContextSchemaMigrator
{
    public static void Migrate(AppDbContext db)
    {
        EnsureCompanyLastSyncedAtColumn(db);
        EnsureCompanyCodeColumn(db);
        EnsureInvoicesTable(db);
        EnsureInvoiceIsSoldColumn(db);
        EnsureInvoiceDenormalizedColumns(db);
        EnsureBackgroundJobsTable(db);
        EnsureBackgroundJobResultPathColumn(db);
        EnsureBackgroundJobReportColumns(db);
        EnsureBackgroundJobPdfPhaseCountColumns(db);
        EnsureBackgroundJobExportColumns(db);
        EnsureBackgroundJobPayloadJsonColumn(db);
        EnsureBackgroundJobFailureSummaryJsonColumn(db);
        EnsureProviderDomainMappingsTable(db);
        ResetInterruptedRunningBackgroundJobs(db);
    }

    private static void EnsureProviderDomainMappingsTable(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var createCmd = connection.CreateCommand();
            createCmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS ProviderDomainMappings (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CompanyId TEXT NOT NULL,
                    ProviderTaxCode TEXT NOT NULL,
                    SellerTaxCode TEXT NOT NULL,
                    SearchUrl TEXT NOT NULL,
                    ProviderName TEXT NULL,
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            createCmd.ExecuteNonQuery();

            using var idxCmd = connection.CreateCommand();
            idxCmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_ProviderDomainMappings_Company_Provider_Seller ON ProviderDomainMappings(CompanyId, ProviderTaxCode, SellerTaxCode);";
            idxCmd.ExecuteNonQuery();
        }
        catch
        {
            // best effort
        }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    /// <summary>Job còn <see cref="BackgroundJobStatus.Running"/> sau khi tắt app / crash — đánh dấu thất bại để hàng đợi không kẹt.</summary>
    private static void ResetInterruptedRunningBackgroundJobs(AppDbContext db)
    {
        const string msg = "Ứng dụng đã thoát khi job đang chạy (hoặc bị ngắt bất thường).";
        db.Database.ExecuteSqlInterpolated(
            $"UPDATE BackgroundJobs SET Status = {(int)BackgroundJobStatus.Failed}, FinishedAt = datetime('now','localtime'), LastError = {msg} WHERE Status = {(int)BackgroundJobStatus.Running}");
    }

    private static void EnsureBackgroundJobFailureSummaryJsonColumn(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('BackgroundJobs');";
            using var reader = checkCmd.ExecuteReader();
            var hasCol = false;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "FailureSummaryJson", StringComparison.OrdinalIgnoreCase))
                {
                    hasCol = true;
                    break;
                }
            }
            if (!hasCol)
            {
                reader.Close();
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE BackgroundJobs ADD COLUMN FailureSummaryJson TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureBackgroundJobPayloadJsonColumn(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('BackgroundJobs');";
            using var reader = checkCmd.ExecuteReader();
            var hasPayloadJson = false;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "PayloadJson", StringComparison.OrdinalIgnoreCase))
                {
                    hasPayloadJson = true;
                    break;
                }
            }
            if (!hasPayloadJson)
            {
                reader.Close();
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE BackgroundJobs ADD COLUMN PayloadJson TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureBackgroundJobExportColumns(DbContext db)
    {
        var columns = new[] { "ExportKey", "IsSummaryOnly" };
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('BackgroundJobs');";
            using var reader = checkCmd.ExecuteReader();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                existing.Add(reader.GetString(1));
            reader.Close();
            var alterSql = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ExportKey"] = "ALTER TABLE BackgroundJobs ADD COLUMN ExportKey TEXT NULL;",
                ["IsSummaryOnly"] = "ALTER TABLE BackgroundJobs ADD COLUMN IsSummaryOnly INTEGER NOT NULL DEFAULT 0;"
            };
            foreach (var col in columns)
            {
                if (existing.Contains(col)) continue;
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = alterSql[col];
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureBackgroundJobReportColumns(DbContext db)
    {
        var columns = new[] { "SyncCount", "XmlTotal", "XmlDownloadedCount", "XmlFailedCount", "XmlNoXmlCount" };
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('BackgroundJobs');";
            using var reader = checkCmd.ExecuteReader();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                existing.Add(reader.GetString(1));
            reader.Close();
            var alterSql = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SyncCount"] = "ALTER TABLE BackgroundJobs ADD COLUMN SyncCount INTEGER NOT NULL DEFAULT 0;",
                ["XmlTotal"] = "ALTER TABLE BackgroundJobs ADD COLUMN XmlTotal INTEGER NOT NULL DEFAULT 0;",
                ["XmlDownloadedCount"] = "ALTER TABLE BackgroundJobs ADD COLUMN XmlDownloadedCount INTEGER NOT NULL DEFAULT 0;",
                ["XmlFailedCount"] = "ALTER TABLE BackgroundJobs ADD COLUMN XmlFailedCount INTEGER NOT NULL DEFAULT 0;",
                ["XmlNoXmlCount"] = "ALTER TABLE BackgroundJobs ADD COLUMN XmlNoXmlCount INTEGER NOT NULL DEFAULT 0;"
            };
            foreach (var col in columns)
            {
                if (existing.Contains(col)) continue;
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = alterSql[col];
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureBackgroundJobPdfPhaseCountColumns(DbContext db)
    {
        var columns = new[] { "PdfDownloadedCount", "PdfFailedCount", "PdfSkippedCount" };
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('BackgroundJobs');";
            using var reader = checkCmd.ExecuteReader();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                existing.Add(reader.GetString(1));
            reader.Close();
            var alterSql = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PdfDownloadedCount"] = "ALTER TABLE BackgroundJobs ADD COLUMN PdfDownloadedCount INTEGER NOT NULL DEFAULT 0;",
                ["PdfFailedCount"] = "ALTER TABLE BackgroundJobs ADD COLUMN PdfFailedCount INTEGER NOT NULL DEFAULT 0;",
                ["PdfSkippedCount"] = "ALTER TABLE BackgroundJobs ADD COLUMN PdfSkippedCount INTEGER NOT NULL DEFAULT 0;"
            };
            foreach (var col in columns)
            {
                if (existing.Contains(col)) continue;
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = alterSql[col];
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureBackgroundJobResultPathColumn(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('BackgroundJobs');";
            using var reader = checkCmd.ExecuteReader();
            var hasResultPath = false;
            while (reader.Read())
            {
                var columnName = reader.GetString(1);
                if (string.Equals(columnName, "ResultPath", StringComparison.OrdinalIgnoreCase))
                {
                    hasResultPath = true;
                    break;
                }
            }
            if (!hasResultPath)
            {
                reader.Close();
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE BackgroundJobs ADD COLUMN ResultPath TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureCompanyCodeColumn(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('Companies');";
            using var reader = checkCmd.ExecuteReader();
            var hasCompanyCode = false;
            while (reader.Read())
            {
                var columnName = reader.GetString(1);
                if (string.Equals(columnName, "CompanyCode", StringComparison.OrdinalIgnoreCase))
                {
                    hasCompanyCode = true;
                    break;
                }
            }
            if (!hasCompanyCode)
            {
                reader.Close();
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Companies ADD COLUMN CompanyCode TEXT NULL;";
                alterCmd.ExecuteNonQuery();
                using var idxCmd = connection.CreateCommand();
                idxCmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Companies_CompanyCode ON Companies(CompanyCode);";
                idxCmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureInvoicesTable(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Invoices (
    Id TEXT NOT NULL PRIMARY KEY,
    CompanyId TEXT NOT NULL,
    ExternalId TEXT NOT NULL,
    PayloadJson TEXT NOT NULL,
    LineItemsJson TEXT NULL,
    SyncedAt TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    IsSold INTEGER NOT NULL DEFAULT 1,
    UNIQUE(CompanyId, ExternalId)
);";
            cmd.ExecuteNonQuery();
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureCompanyLastSyncedAtColumn(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        try
        {
            if (shouldClose)
                connection.Open();

            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('Companies');";

            using var reader = checkCmd.ExecuteReader();
            var hasLastSyncedAt = false;

            while (reader.Read())
            {
                // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
                var columnName = reader.GetString(1);
                if (string.Equals(columnName, "LastSyncedAt", StringComparison.OrdinalIgnoreCase))
                {
                    hasLastSyncedAt = true;
                    break;
                }
            }

            if (!hasLastSyncedAt)
            {
                using var alterCmd = connection.CreateCommand();
                // DateTime is mapped to TEXT by EF Core for SQLite by default.
                alterCmd.CommandText = "ALTER TABLE Companies ADD COLUMN LastSyncedAt TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // Best-effort migration: ignore failures, app will still run (but column may be missing).
        }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureInvoiceIsSoldColumn(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('Invoices');";
            using var reader = checkCmd.ExecuteReader();
            var hasIsSold = false;
            while (reader.Read())
            {
                var columnName = reader.GetString(1);
                if (string.Equals(columnName, "IsSold", StringComparison.OrdinalIgnoreCase))
                {
                    hasIsSold = true;
                    break;
                }
            }
            if (!hasIsSold)
            {
                reader.Close();
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Invoices ADD COLUMN IsSold INTEGER NOT NULL DEFAULT 1;";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureInvoiceDenormalizedColumns(DbContext db)
    {
        var columns = new[]
        {
            "NgayLap", "Tthai", "Tgtcthue", "Tgtthue", "TongTien",
            "CoMa", "MayTinhTien", "KyHieu", "SoHoaDon",
            "NbMst", "NguoiBan", "NguoiMua", "MstMua",
            "Dvtte",
            "XmlStatus", "XmlBaseName",
            "ProviderTaxCode", "TvanTaxCode"
        };
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('Invoices');";
            using var reader = checkCmd.ExecuteReader();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                existing.Add(reader.GetString(1));
            reader.Close();
            var alterSql = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NgayLap"] = "ALTER TABLE Invoices ADD COLUMN NgayLap TEXT NULL;",
                ["Tthai"] = "ALTER TABLE Invoices ADD COLUMN Tthai INTEGER NOT NULL DEFAULT 0;",
                ["Tgtcthue"] = "ALTER TABLE Invoices ADD COLUMN Tgtcthue REAL NULL;",
                ["Tgtthue"] = "ALTER TABLE Invoices ADD COLUMN Tgtthue REAL NULL;",
                ["TongTien"] = "ALTER TABLE Invoices ADD COLUMN TongTien REAL NULL;",
                ["CoMa"] = "ALTER TABLE Invoices ADD COLUMN CoMa INTEGER NOT NULL DEFAULT 0;",
                ["MayTinhTien"] = "ALTER TABLE Invoices ADD COLUMN MayTinhTien INTEGER NOT NULL DEFAULT 0;",
                ["KyHieu"] = "ALTER TABLE Invoices ADD COLUMN KyHieu TEXT NULL;",
                ["SoHoaDon"] = "ALTER TABLE Invoices ADD COLUMN SoHoaDon INTEGER NOT NULL DEFAULT 0;",
                ["NbMst"] = "ALTER TABLE Invoices ADD COLUMN NbMst TEXT NULL;",
                ["NguoiBan"] = "ALTER TABLE Invoices ADD COLUMN NguoiBan TEXT NULL;",
                ["NguoiMua"] = "ALTER TABLE Invoices ADD COLUMN NguoiMua TEXT NULL;",
                ["MstMua"] = "ALTER TABLE Invoices ADD COLUMN MstMua TEXT NULL;",
                ["Dvtte"] = "ALTER TABLE Invoices ADD COLUMN Dvtte TEXT NULL;",
                ["XmlStatus"] = "ALTER TABLE Invoices ADD COLUMN XmlStatus INTEGER NOT NULL DEFAULT 0;",
                ["XmlBaseName"] = "ALTER TABLE Invoices ADD COLUMN XmlBaseName TEXT NULL;",
                ["ProviderTaxCode"] = "ALTER TABLE Invoices ADD COLUMN ProviderTaxCode TEXT NULL;",
                ["TvanTaxCode"] = "ALTER TABLE Invoices ADD COLUMN TvanTaxCode TEXT NULL;"
            };
            foreach (var col in columns)
            {
                if (existing.Contains(col)) continue;
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = alterSql[col];
                alterCmd.ExecuteNonQuery();
            }

            // Sau khi đảm bảo cột tồn tại, backfill ProviderTaxCode/TvanTaxCode từ PayloadJson cho các hóa đơn cũ (best-effort).
            try
            {
                using var selectCmd = connection.CreateCommand();
                selectCmd.CommandText = "SELECT Id, PayloadJson FROM Invoices WHERE ProviderTaxCode IS NULL OR TvanTaxCode IS NULL;";
                using var reader2 = selectCmd.ExecuteReader();
                var toUpdate = new List<(string Id, string Payload)>();
                while (reader2.Read())
                {
                    var id = reader2.GetString(0);
                    var payload = reader2.GetString(1);
                    toUpdate.Add((id, payload));
                }
                reader2.Close();

                foreach (var (id, payload) in toUpdate)
                {
                    string? providerTax = null;
                    string? tvanTax = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        foreach (var obj in EnumeratePayloadObjectsForBackfill(doc.RootElement))
                        {
                            if (obj.ValueKind != JsonValueKind.Object) continue;
                            if (string.IsNullOrWhiteSpace(providerTax) &&
                                obj.TryGetProperty("msttcgp", out var mstProp))
                                providerTax = JsonTaxFieldReader.CoerceToTrimmedString(mstProp);
                            if (string.IsNullOrWhiteSpace(tvanTax))
                            {
                                if (obj.TryGetProperty("tvanDnKntt", out var tv1))
                                    tvanTax = JsonTaxFieldReader.CoerceToTrimmedString(tv1);
                                if (string.IsNullOrWhiteSpace(tvanTax) && obj.TryGetProperty("tvandnkntt", out var tv2))
                                    tvanTax = JsonTaxFieldReader.CoerceToTrimmedString(tv2);
                            }
                            if (!string.IsNullOrWhiteSpace(providerTax) && !string.IsNullOrWhiteSpace(tvanTax))
                                break;
                        }
                    }
                    catch
                    {
                        // ignore parse error, best-effort only
                    }

                    if (string.IsNullOrWhiteSpace(providerTax) && string.IsNullOrWhiteSpace(tvanTax))
                        continue;

                    using var updateCmd = connection.CreateCommand();
                    updateCmd.CommandText = "UPDATE Invoices SET ProviderTaxCode = COALESCE(ProviderTaxCode, @p1), TvanTaxCode = COALESCE(TvanTaxCode, @p2) WHERE Id = @id;";
                    var p1 = updateCmd.CreateParameter();
                    p1.ParameterName = "@p1";
                    p1.Value = (object?)providerTax ?? DBNull.Value;
                    var p2 = updateCmd.CreateParameter();
                    p2.ParameterName = "@p2";
                    p2.Value = (object?)tvanTax ?? DBNull.Value;
                    var pid = updateCmd.CreateParameter();
                    pid.ParameterName = "@id";
                    pid.Value = id;
                    updateCmd.Parameters.Add(p1);
                    updateCmd.Parameters.Add(p2);
                    updateCmd.Parameters.Add(pid);
                    updateCmd.ExecuteNonQuery();
                }
            }
            catch
            {
                // best-effort backfill; nếu lỗi thì bỏ qua, không chặn khởi động app
            }
        }
        catch { /* best-effort */ }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static void EnsureBackgroundJobsTable(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose)
                connection.Open();
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='BackgroundJobs';";
            var exists = checkCmd.ExecuteScalar() != null;
            if (exists) return;

            using var createCmd = connection.CreateCommand();
            createCmd.CommandText =
                """
                CREATE TABLE BackgroundJobs (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Type INTEGER NOT NULL,
                    Status INTEGER NOT NULL,
                    CompanyId TEXT NOT NULL,
                    IsSold INTEGER NOT NULL,
                    FromDate TEXT NOT NULL,
                    ToDate TEXT NOT NULL,
                    IncludeDetail INTEGER NOT NULL,
                    DownloadXml INTEGER NOT NULL,
                    DownloadPdf INTEGER NOT NULL,
                    ProgressCurrent INTEGER NOT NULL,
                    ProgressTotal INTEGER NOT NULL,
                    Description TEXT NULL,
                    LastError TEXT NULL,
                    ResultPath TEXT NULL,
                    SyncCount INTEGER NOT NULL DEFAULT 0,
                    XmlTotal INTEGER NOT NULL DEFAULT 0,
                    XmlDownloadedCount INTEGER NOT NULL DEFAULT 0,
                    XmlFailedCount INTEGER NOT NULL DEFAULT 0,
                    XmlNoXmlCount INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    StartedAt TEXT NULL,
                    FinishedAt TEXT NULL
                );
                """;
            createCmd.ExecuteNonQuery();
        }
        catch
        {
            // best-effort
        }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                connection.Close();
        }
    }

    private static IEnumerable<JsonElement> EnumeratePayloadObjectsForBackfill(JsonElement root)
    {
        var r = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
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

