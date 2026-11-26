using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>What one full table copy did to the target.</summary>
public sealed record FullCopyResult(string Table, bool Truncated, long RowsDeleted, long RowsCopied, string? IdentityReseededTo);

/// <summary>
/// Replaces a target table's rows with the source's (M.SCHD-T66, ported from Sync-ToAzure.ps1's Copy-Rows and the
/// full branch of Invoke-MemberPlan): the target is emptied, the source streamed in through SqlBulkCopy with identity
/// values kept, and the target's identity reseeded to the source's last value. Used for the first run, a watermark
/// older than the source's change retention and a newly tracked table. Foreign keys into or out of the table should
/// be suspended by a <see cref="TargetConstraintGuard"/> around the copy; a system-versioned table needs its
/// versioning suspended first (TemporalPairSync), because its period columns are written too.
/// </summary>
public static class FullTableCopy
{
    /// <summary>Copies every row of <paramref name="table"/> (the source's catalog entry) from source to target.</summary>
    public static FullCopyResult Copy(SqlConnection source, SqlConnection target, CatalogTable table, DbSyncSettings settings)
    {
        if (table.CopyUnsupported.Count > 0)
            throw new InvalidOperationException($"{table.Key} has columns the sync cannot copy: {string.Join(", ", table.CopyUnsupported)}.");

        var (truncated, deleted) = Empty(target, table, settings);
        var copied = BulkCopy(source, target, table, settings);
        var reseeded = Reseed(source, target, table, settings);
        return new FullCopyResult(table.Key, truncated, deleted, copied, reseeded);
    }

    /// <summary>TRUNCATE when no foreign key references the table (and the server allows it), else DELETE in batches.</summary>
    private static (bool Truncated, long Deleted) Empty(SqlConnection target, CatalogTable table, DbSyncSettings settings)
    {
        using (var cmd = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.referenced_object_id = OBJECT_ID(@n)) THEN 1 ELSE 0 END", target) { CommandTimeout = settings.CommandTimeoutSeconds })
        {
            cmd.Parameters.Add("@n", SqlDbType.NVarChar, 600).Value = table.QName;
            if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                try
                {
                    Exec(target, settings, $"TRUNCATE TABLE {table.QName};");
                    return (true, 0);
                }
                catch (SqlException) { /* refused (permissions, an indexed view, replication): DELETE below */ }
            }
        }

        long total = 0;
        int n;
        do
        {
            n = Exec(target, settings, $"DELETE TOP ({settings.BulkBatchSize}) FROM {table.QName};");
            total += n;
        } while (n > 0);
        return (false, total);
    }

    private static long BulkCopy(SqlConnection source, SqlConnection target, CatalogTable table, DbSyncSettings settings)
    {
        var columns = table.CopyColumns;
        var select = $"SELECT {string.Join(", ", columns.Select(c => TableCatalog.Quote(c.Name)))} FROM {table.QName};";
        using var cmd = new SqlCommand(select, source) { CommandTimeout = settings.CommandTimeoutSeconds };
        using var rd = cmd.ExecuteReader(CommandBehavior.SequentialAccess);

        // CheckConstraints keeps CHECK constraints trusted; foreign keys the guard has disabled are not checked.
        const SqlBulkCopyOptions options = SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls |
                                           SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.CheckConstraints;
        using var bulk = new SqlBulkCopy(target, options, null)
        {
            DestinationTableName = table.QName,
            BatchSize = settings.BulkBatchSize,
            BulkCopyTimeout = settings.CommandTimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (var c in columns) bulk.ColumnMappings.Add(c.Name, c.Name);
        bulk.WriteToServer(rd);
        return bulk.RowsCopied64;
    }

    /// <summary>Reseeds the target's identity to the source's last value, read after the copy; null when there is none.</summary>
    private static string? Reseed(SqlConnection source, SqlConnection target, CatalogTable table, DbSyncSettings settings)
    {
        if (table.IdentityColumn is null) return null;

        using var cmd = new SqlCommand("SELECT CONVERT(VARCHAR(40), ic.last_value) FROM sys.identity_columns ic WHERE ic.object_id = OBJECT_ID(@n)", source) { CommandTimeout = settings.CommandTimeoutSeconds };
        cmd.Parameters.Add("@n", SqlDbType.NVarChar, 600).Value = table.QName;
        if (cmd.ExecuteScalar() is not string last || !decimal.TryParse(last, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            return null;

        Exec(target, settings, $"DBCC CHECKIDENT ('{table.QName.Replace("'", "''")}', RESEED, {last}) WITH NO_INFOMSGS;");
        return last;
    }

    private static int Exec(SqlConnection c, DbSyncSettings settings, string sql)
    {
        using var cmd = new SqlCommand(sql, c) { CommandTimeout = settings.CommandTimeoutSeconds };
        return cmd.ExecuteNonQuery();
    }
}
