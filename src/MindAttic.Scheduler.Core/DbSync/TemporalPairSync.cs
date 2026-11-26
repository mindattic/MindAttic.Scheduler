using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>
/// What one temporal pair sync did: the history table's hashdiff result, then the current table's method
/// (<c>changetracking</c>, <c>full</c>, or a hashdiff method) with the rows it wrote and deleted.
/// </summary>
public sealed record TemporalPairResult(string Table, string History, HashDiffResult HistoryResult, string CurrentMethod, long CurrentWritten, long CurrentDeleted);

/// <summary>
/// The sync of a system-versioned table and its history table (M.SCHD-T68, ported from Sync-ToAzure.ps1's
/// Get-TargetTemporalState, Suspend-Versioning, Restore-Versioning and the temporal part of Repair-State). On the target,
/// a <see cref="Marker"/> extended property is set, SYSTEM_VERSIONING turned off and the PERIOD FOR SYSTEM_TIME dropped,
/// so both tables, period columns included, can be written with the source's exact values. The history table is synced
/// first (hashdiff; usually an append), then the current table (Change Tracking from the watermark, a full copy, or
/// hashdiff without a primary key). The period is added back, versioning re-enabled on the same history table WITH
/// (DATA_CONSISTENCY_CHECK = ON) and the marker cleared. A target left unversioned by a killed run is restored by
/// <see cref="Repair"/>, or by the next sync of the pair.
/// </summary>
public static class TemporalPairSync
{
    /// <summary>The extended property on a current table whose versioning the sync has suspended.</summary>
    public const string Marker = "SyncToRemote.VersioningSuspended";

    /// <summary>
    /// Makes the target's copy of the temporal pair whose current table is <paramref name="currentKey"/> equal to the
    /// source's. <paramref name="lastVersion"/> is the current table's Change Tracking watermark, or null for a full
    /// copy; <paramref name="previousHistory"/> is the history table's state from the last successful run, or null.
    /// Writes only to <paramref name="target"/>.
    /// </summary>
    public static TemporalPairResult Apply(SqlConnection source, SqlConnection target, TableCatalog sourceCatalog, TableCatalog targetCatalog,
        string currentKey, long? lastVersion, DbSyncTableState? previousHistory, DbSyncSettings settings)
    {
        var pair = sourceCatalog.TemporalPairs.FirstOrDefault(p => p.CurrentKey == currentKey)
            ?? throw new InvalidOperationException($"{currentKey} is not a system-versioned table in the source.");
        var current = sourceCatalog.Tables[pair.CurrentKey];
        var history = sourceCatalog.Tables[pair.HistoryKey].QName;
        if (!targetCatalog.Tables.ContainsKey(pair.CurrentKey) || !targetCatalog.Tables.ContainsKey(pair.HistoryKey))
            throw new InvalidOperationException($"{pair.CurrentKey} or its history table {pair.HistoryKey} is not a table in the target.");

        Suspend(target, pair, current, settings);
        TemporalPairResult result;
        try
        {
            var historyResult = HashDiffTableSync.Apply(source, target, sourceCatalog, targetCatalog, pair.HistoryKey, previousHistory, settings);
            string method;
            long written, deleted;
            if (current.PrimaryKey.Count == 0)
            {
                var r = HashDiffTableSync.Apply(source, target, sourceCatalog, targetCatalog, pair.CurrentKey, null, settings);
                (method, written, deleted) = (r.Method, r.Inserted, r.Deleted);
            }
            else if (lastVersion is { } version)
            {
                var r = ChangeTrackedTableSync.Apply(source, target, current, version, settings, writePeriod: true);
                (method, written, deleted) = ("changetracking", r.Upserted, r.Deleted);
            }
            else
            {
                var r = FullTableCopy.Copy(source, target, current, settings);
                (method, written, deleted) = ("full", r.RowsCopied, r.RowsDeleted);
            }
            result = new TemporalPairResult(pair.CurrentKey, pair.HistoryKey, historyResult, method, written, deleted);
        }
        catch
        {
            // Best effort: a target that cannot be re-versioned now keeps its marker for Repair or the next run.
            try { Restore(target, pair, current, history, settings); }
            catch (SqlException) { }
            throw;
        }
        Restore(target, pair, current, history, settings);
        return result;
    }

    /// <summary>
    /// Re-versions every table of the database <paramref name="target"/> is open on that is system-versioned in the
    /// source but not in the target, or that still carries a <see cref="Marker"/>, on its own history table, and clears
    /// the markers. Returns the tables repaired. Throws when one cannot be restored (it keeps its marker).
    /// </summary>
    public static IReadOnlyList<string> Repair(SqlConnection target, TableCatalog sourceCatalog, DbSyncSettings settings)
    {
        var marked = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = new SqlCommand(
                   "SELECT SCHEMA_NAME(t.schema_id) + N'.' + t.name FROM sys.extended_properties ep " +
                   "JOIN sys.tables t ON t.object_id = ep.major_id WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = @m;", target)
               { CommandTimeout = settings.CommandTimeoutSeconds })
        {
            cmd.Parameters.Add("@m", SqlDbType.NVarChar, 128).Value = Marker;
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) marked.Add(rd.GetString(0));
        }

        var targetCatalog = TableCatalog.Read(target);
        var repaired = new List<string>();
        var failures = new List<string>();
        foreach (var pair in sourceCatalog.TemporalPairs)
        {
            if (!targetCatalog.Tables.TryGetValue(pair.CurrentKey, out var t)) continue;
            if (t.TemporalType == 2 && !marked.Contains(pair.CurrentKey)) continue;
            try
            {
                Restore(target, pair, sourceCatalog.Tables[pair.CurrentKey], sourceCatalog.Tables[pair.HistoryKey].QName, settings);
                repaired.Add(pair.CurrentKey);
            }
            catch (SqlException ex) { failures.Add($"{pair.CurrentKey}: {ex.Message}"); }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException($"Could not restore system versioning on the target (markers kept for the next run): {string.Join("; ", failures)}");
        return repaired;
    }

    private sealed record TargetState(bool Versioned, bool HasPeriod, bool StartHidden, bool EndHidden);

    /// <summary>The target's versioning, period and period-column visibility for <paramref name="pair"/> (Get-TargetTemporalState).</summary>
    private static TargetState State(SqlConnection target, TemporalPair pair, CatalogTable current, DbSyncSettings settings)
    {
        using var cmd = new SqlCommand(
            """
            SELECT t.temporal_type, (SELECT COUNT(*) FROM sys.periods p WHERE p.object_id = t.object_id),
                   ISNULL((SELECT MAX(CAST(c.is_hidden AS INT)) FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = @s), 0),
                   ISNULL((SELECT MAX(CAST(c.is_hidden AS INT)) FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = @e), 0)
            FROM sys.tables t WHERE t.object_id = OBJECT_ID(@n)
            """, target) { CommandTimeout = settings.CommandTimeoutSeconds };
        cmd.Parameters.Add("@n", SqlDbType.NVarChar, 600).Value = current.QName;
        cmd.Parameters.Add("@s", SqlDbType.NVarChar, 128).Value = pair.PeriodStart;
        cmd.Parameters.Add("@e", SqlDbType.NVarChar, 128).Value = pair.PeriodEnd;
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) throw new InvalidOperationException($"{pair.CurrentKey} is not a table in the target.");
        return new TargetState(Convert.ToInt32(rd[0], CultureInfo.InvariantCulture) == 2, Convert.ToInt32(rd[1], CultureInfo.InvariantCulture) > 0,
            Convert.ToInt32(rd[2], CultureInfo.InvariantCulture) == 1, Convert.ToInt32(rd[3], CultureInfo.InvariantCulture) == 1);
    }

    /// <summary>Marks the table, then turns versioning off and drops the period (Suspend-Versioning).</summary>
    private static void Suspend(SqlConnection target, TemporalPair pair, CatalogTable current, DbSyncSettings settings)
    {
        RunMarker(target, settings, current, $"system versioning suspended by the DB sync at {DateTime.Now:s}; history table {pair.HistoryKey}, " +
            $"period ({pair.PeriodStart}, {pair.PeriodEnd}); the next sync restores it", """
            IF NOT EXISTS (SELECT 1 FROM sys.extended_properties ep WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = @m AND ep.major_id = OBJECT_ID(QUOTENAME(@s) + N'.' + QUOTENAME(@t)))
                EXEC sys.sp_addextendedproperty @name = @m, @value = @v, @level0type = N'SCHEMA', @level0name = @s, @level1type = N'TABLE', @level1name = @t;
            """);
        var st = State(target, pair, current, settings);
        if (st.Versioned) Exec(target, settings, $"ALTER TABLE {current.QName} SET (SYSTEM_VERSIONING = OFF);");
        if (st.HasPeriod) Exec(target, settings, $"ALTER TABLE {current.QName} DROP PERIOD FOR SYSTEM_TIME;");
    }

    /// <summary>
    /// Adds the period back, hides the period columns the source hides, re-enables versioning on the same history table
    /// with the consistency check and the source's retention, and clears the marker (Restore-Versioning).
    /// </summary>
    private static void Restore(SqlConnection target, TemporalPair pair, CatalogTable current, string history, DbSyncSettings settings)
    {
        var st = State(target, pair, current, settings);
        if (!st.HasPeriod)
        {
            Exec(target, settings, $"ALTER TABLE {current.QName} ADD PERIOD FOR SYSTEM_TIME ({Q(pair.PeriodStart)}, {Q(pair.PeriodEnd)});");
            st = State(target, pair, current, settings);
        }
        if (current.Column(pair.PeriodStart)?.Hidden == true && !st.StartHidden)
            Exec(target, settings, $"ALTER TABLE {current.QName} ALTER COLUMN {Q(pair.PeriodStart)} ADD HIDDEN;");
        if (current.Column(pair.PeriodEnd)?.Hidden == true && !st.EndHidden)
            Exec(target, settings, $"ALTER TABLE {current.QName} ALTER COLUMN {Q(pair.PeriodEnd)} ADD HIDDEN;");
        if (!st.Versioned)
        {
            var retention = current.HistoryRetentionPeriod is > 0 && current.HistoryRetentionUnit.Length > 0
                ? $", HISTORY_RETENTION_PERIOD = {current.HistoryRetentionPeriod} {current.HistoryRetentionUnit}"
                : "";
            Exec(target, settings, $"ALTER TABLE {current.QName} SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = {history}, DATA_CONSISTENCY_CHECK = ON{retention}));");
        }
        RunMarker(target, settings, current, null, """
            IF EXISTS (SELECT 1 FROM sys.extended_properties ep WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = @m AND ep.major_id = OBJECT_ID(QUOTENAME(@s) + N'.' + QUOTENAME(@t)))
                EXEC sys.sp_dropextendedproperty @name = @m, @level0type = N'SCHEMA', @level0name = @s, @level1type = N'TABLE', @level1name = @t;
            """);
    }

    private static void RunMarker(SqlConnection target, DbSyncSettings settings, CatalogTable table, string? value, string sql)
    {
        using var cmd = new SqlCommand(sql, target) { CommandTimeout = settings.CommandTimeoutSeconds };
        cmd.Parameters.Add("@m", SqlDbType.NVarChar, 128).Value = Marker;
        cmd.Parameters.Add("@s", SqlDbType.NVarChar, 128).Value = table.Schema;
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 128).Value = table.Name;
        if (value is not null) cmd.Parameters.Add("@v", SqlDbType.NVarChar, 3750).Value = value;
        cmd.ExecuteNonQuery();
    }

    private static int Exec(SqlConnection c, DbSyncSettings settings, string sql)
    {
        using var cmd = new SqlCommand(sql, c) { CommandTimeout = settings.CommandTimeoutSeconds };
        return cmd.ExecuteNonQuery();
    }

    private static string Q(string name) => TableCatalog.Quote(name);
}
