using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>Whether a table's changes come from Change Tracking or from the full-compare fallback.</summary>
public enum ChangeTrackingStatus
{
    Tracked,
    Fallback,
}

/// <summary>
/// One user table's Change Tracking state. <see cref="FallbackReason"/> is <c>no-pk</c>,
/// <c>temporal-history</c> or <c>unsupported (...)</c> on a fallback table. <see cref="NewlyEnabled"/>
/// tables (would) get tracking from this call, so they need a full copy: CT has no history before it.
/// </summary>
public sealed record ChangeTrackingTable(
    string Key,
    ChangeTrackingStatus Status,
    string? FallbackReason,
    bool NewlyEnabled,
    long? MinValidVersion);

/// <summary>What <see cref="ChangeTrackingSetup.Ensure"/> found and changed (or, on a dry run, would change).</summary>
public sealed record ChangeTrackingReport(
    string Database,
    bool DryRun,
    bool DatabaseWasEnabled,
    int? RetentionMinutesBefore,
    int RetentionMinutesAfter,
    IReadOnlyList<string> Statements,
    IReadOnlyList<ChangeTrackingTable> Tables)
{
    /// <summary>True when the call ran (or would run) no DDL.</summary>
    public bool Unchanged => Statements.Count == 0;

    public IReadOnlyList<ChangeTrackingTable> Tracked => Tables.Where(t => t.Status == ChangeTrackingStatus.Tracked).ToList();

    public IReadOnlyList<ChangeTrackingTable> Fallback => Tables.Where(t => t.Status == ChangeTrackingStatus.Fallback).ToList();

    /// <summary>Tables that (would) get tracking from this call and so need a full copy.</summary>
    public IReadOnlyList<ChangeTrackingTable> NewlyEnabled => Tables.Where(t => t.NewlyEnabled).ToList();
}

/// <summary>
/// Turns Change Tracking on for one SOURCE database (M.SCHD-T63), idempotently: on at database level
/// with <see cref="DbSyncSettings.ChangeRetentionDays"/> retention (an existing shorter retention is
/// raised, a longer one is never lowered) and on every user table that has a primary key and is not a
/// history table or a memory-optimized, external, graph or ledger table.
/// </summary>
public static class ChangeTrackingSetup
{
    private const string DatabaseStateSql =
        "SELECT ctd.retention_period, ctd.retention_period_units FROM sys.change_tracking_databases ctd WHERE ctd.database_id = DB_ID();";

    private const string TrackedTablesSql =
        "SELECT ctt.object_id, CHANGE_TRACKING_MIN_VALID_VERSION(ctt.object_id) FROM sys.change_tracking_tables ctt;";

    /// <summary>
    /// Ensures Change Tracking on the database <paramref name="connection"/> is open on. With
    /// <paramref name="dryRun"/> it runs no DDL and reports the statements it would run.
    /// </summary>
    public static ChangeTrackingReport Ensure(SqlConnection connection, DbSyncSettings settings, bool dryRun = false)
    {
        var statements = new List<string>();
        var wantedMinutes = checked(settings.ChangeRetentionDays * 24 * 60);

        var before = RetentionMinutes(connection, settings);
        var after = before is { } b && b >= wantedMinutes ? b : wantedMinutes;
        if (before is null)
            statements.Add($"ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = {settings.ChangeRetentionDays} DAYS, AUTO_CLEANUP = ON);");
        else if (before < wantedMinutes)
            statements.Add($"ALTER DATABASE CURRENT SET CHANGE_TRACKING (CHANGE_RETENTION = {settings.ChangeRetentionDays} DAYS);");

        var catalog = TableCatalog.Read(connection);
        var trackedIds = TrackedTables(connection, settings);

        var newly = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in catalog.Tables.Values.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (FallbackReason(t) is null && !trackedIds.ContainsKey(t.ObjectId))
            {
                statements.Add($"ALTER TABLE {t.QName} ENABLE CHANGE_TRACKING;");
                newly.Add(t.Key);
            }
        }

        if (!dryRun && statements.Count > 0)
        {
            foreach (var sql in statements)
            {
                using var cmd = new SqlCommand(sql, connection) { CommandTimeout = settings.CommandTimeoutSeconds };
                cmd.ExecuteNonQuery();
            }
            trackedIds = TrackedTables(connection, settings);
        }

        var tables = catalog.Tables.Values
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => FallbackReason(t) is { } reason
                ? new ChangeTrackingTable(t.Key, ChangeTrackingStatus.Fallback, reason, false, null)
                : new ChangeTrackingTable(t.Key, ChangeTrackingStatus.Tracked, null, newly.Contains(t.Key), trackedIds.GetValueOrDefault(t.ObjectId)))
            .ToList();

        return new ChangeTrackingReport(connection.Database, dryRun, before is not null, before, after, statements, tables);
    }

    /// <summary>Why a table cannot be tracked, or null when it can.</summary>
    public static string? FallbackReason(CatalogTable table)
    {
        if (table.Unsupported.Count > 0) return $"unsupported ({string.Join(", ", table.Unsupported)})";
        if (table.TemporalType == 1) return "temporal-history";
        if (table.PrimaryKey.Count == 0) return "no-pk";
        return null;
    }

    /// <summary>The database's Change Tracking retention in minutes, or null when tracking is off.</summary>
    private static int? RetentionMinutes(SqlConnection connection, DbSyncSettings settings)
    {
        using var cmd = new SqlCommand(DatabaseStateSql, connection) { CommandTimeout = settings.CommandTimeoutSeconds };
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        var period = Convert.ToInt32(rd[0]);
        return Convert.ToInt32(rd[1]) switch
        {
            1 => period,
            2 => period * 60,
            _ => period * 24 * 60,
        };
    }

    /// <summary>Tracked tables' object ids with their minimum valid version.</summary>
    private static Dictionary<int, long?> TrackedTables(SqlConnection connection, DbSyncSettings settings)
    {
        var ids = new Dictionary<int, long?>();
        using var cmd = new SqlCommand(TrackedTablesSql, connection) { CommandTimeout = settings.CommandTimeoutSeconds };
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) ids[rd.GetInt32(0)] = rd.IsDBNull(1) ? null : rd.GetInt64(1);
        return ids;
    }
}
