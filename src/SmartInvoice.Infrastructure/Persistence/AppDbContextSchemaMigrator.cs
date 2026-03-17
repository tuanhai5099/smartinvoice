using System;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

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
        EnsureBackgroundJobExportColumns(db);
        EnsureBackgroundJobPayloadJsonColumn(db);
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
            "XmlStatus", "XmlBaseName"
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
                ["XmlBaseName"] = "ALTER TABLE Invoices ADD COLUMN XmlBaseName TEXT NULL;"
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
}

