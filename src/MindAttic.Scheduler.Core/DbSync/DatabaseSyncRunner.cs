using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>How one database's run ended.</summary>
public enum DatabaseSyncOutcome
{
    /// <summary>Every table synced and the watermark moved to the run's start.</summary>
    Succeeded,

    /// <summary>Nothing changed since the watermark, so nothing was written to the target.</summary>
    Unchanged,

    /// <summary>The run stopped; the watermark stays where it was, so the next run repeats this one's work.</summary>
    Failed,
}

/// <summary>What one table's sync did: <see cref="Method"/> is <c>full</c>, <c>changetracking</c> or a hashdiff method.</summary>
public sealed record DatabaseSyncTable(string Table, string Method, long Written, long Deleted);

/// <summary>
/// One database's run. <see cref="StartVersion"/> and <see cref="StartUtc"/> are what the run captured before reading
/// any rows (null when it failed before that); on success they are the new watermark.
/// </summary>
public sealed record DatabaseSyncResult(
    string Database,
    string Target,
    DatabaseSyncOutcome Outcome,
    long? StartVersion,
    DateTime? StartUtc,
    IReadOnlyList<DatabaseSyncTable> Tables,
    string? Message);

/// <summary>
/// Syncs one database to its target, once (M.SCHD-T69). Change Tracking is ensured on the source and the two schemas
/// compared (a mismatch fails the database with nothing written). The source's change-tracking version and the UTC time
/// are captured BEFORE any row is read, so a write made during the run is picked up by the next one. A database whose
/// version still equals the watermark, and whose fallback tables are all cheaply unchanged, is <c>unchanged</c>.
/// Otherwise, inside a <see cref="TargetConstraintGuard"/>, each table gets a <see cref="FullTableCopy"/> (first run,
/// newly tracked, or watermark older than its minimum valid version), a <see cref="ChangeTrackedTableSync"/> from the
/// watermark, a <see cref="TemporalPairSync"/> (a system-versioned table with its history) or a
/// <see cref="HashDiffTableSync"/> (no primary key). The watermark moves to the captured start only when every table
/// succeeded; any failure is recorded with <see cref="WatermarkStore.Fail"/> and the watermark stays.
/// </summary>
public sealed class DatabaseSyncRunner(DbSyncSettings settings, WatermarkStore watermarks)
{
    /// <summary>Runs right after the start version and time are captured, before any row is read (a test hook).</summary>
    public Action? AfterStartCaptured { get; init; }

    /// <summary>Syncs <paramref name="database"/>, which must have a target. Never throws for the database's own failures.</summary>
    public DatabaseSyncResult Run(DbSyncDatabase database)
    {
        if (!database.IsConfigured)
            throw new ArgumentException($"{database.Name} has no target to sync to.", nameof(database));

        var name = database.Name;
        var targetKey = database.DescribeTarget();
        long? startVersion = null;
        DateTime? startUtc = null;
        var tables = new List<DatabaseSyncTable>();
        try
        {
            using var source = new SqlConnection(database.SourceConnectionString(settings.ConnectTimeoutSeconds));
            using var target = new SqlConnection(database.TargetConnectionString);
            source.Open();
            target.Open();

            var report = ChangeTrackingSetup.Ensure(source, settings);
            var sourceCatalog = TableCatalog.Read(source);
            var targetCatalog = TableCatalog.Read(target);
            if (TableCatalog.Compare(sourceCatalog, targetCatalog, $"{name} (source)", targetKey) is { } mismatch)
                return Failed(mismatch.ToString());
            if (sourceCatalog.TemporalPairs.Count > 0 && TemporalPairSync.Repair(target, sourceCatalog, settings).Count > 0)
                targetCatalog = TableCatalog.Read(target);

            using (var cmd = new SqlCommand("SELECT CHANGE_TRACKING_CURRENT_VERSION()", source) { CommandTimeout = settings.CommandTimeoutSeconds })
                startVersion = cmd.ExecuteScalar() is long v ? v : null;
            startUtc = DateTime.UtcNow;
            AfterStartCaptured?.Invoke();

            var watermark = watermarks.Get(name, targetKey);
            var lastVersion = watermark?.LastVersion;
            var states = watermarks.TableStates(name, targetKey).ToDictionary(s => s.TableName, StringComparer.Ordinal);
            var tracking = report.Tables.ToDictionary(t => t.Key, StringComparer.Ordinal);

            bool NeedsFullCopy(string key) =>
                lastVersion is not { } last || !tracking.TryGetValue(key, out var t) || t.NewlyEnabled || t.MinValidVersion is not { } min || min > last;

            if (lastVersion is not null && lastVersion == startVersion &&
                !report.Tracked.Any(t => NeedsFullCopy(t.Key)) &&
                report.Fallback.All(t => HashDiffTableSync.Unchanged(source, sourceCatalog, targetCatalog, t.Key, states.GetValueOrDefault(t.Key), settings) is not null))
            {
                watermarks.Commit(name, targetKey, startVersion, startUtc.Value, message: "unchanged");
                return new DatabaseSyncResult(name, targetKey, DatabaseSyncOutcome.Unchanged, startVersion, startUtc, tables, "unchanged since the last run");
            }

            var pairs = sourceCatalog.TemporalPairs.ToDictionary(p => p.CurrentKey, StringComparer.Ordinal);
            var histories = sourceCatalog.TemporalPairs.Select(p => p.HistoryKey).ToHashSet(StringComparer.Ordinal);
            var newStates = new List<DbSyncTableState>();

            using (var guard = TargetConstraintGuard.Engage(target, sourceCatalog.Tables.Keys, settings))
            {
                foreach (var table in sourceCatalog.Tables.Values.OrderBy(t => t.Key, StringComparer.Ordinal))
                {
                    var key = table.Key;
                    if (histories.Contains(key)) continue;
                    var tracked = tracking.TryGetValue(key, out var ct) && ct.Status == ChangeTrackingStatus.Tracked;

                    if (pairs.TryGetValue(key, out var pair))
                    {
                        var r = TemporalPairSync.Apply(source, target, sourceCatalog, targetCatalog, key,
                            tracked && !NeedsFullCopy(key) ? lastVersion : null, states.GetValueOrDefault(pair.HistoryKey), settings);
                        tables.Add(new DatabaseSyncTable(pair.HistoryKey, r.HistoryResult.Method, r.HistoryResult.Inserted, r.HistoryResult.Deleted));
                        tables.Add(new DatabaseSyncTable(key, r.CurrentMethod, r.CurrentWritten, r.CurrentDeleted));
                        newStates.Add(r.HistoryResult.State);
                        newStates.Add(new DbSyncTableState(key, "temporal", startVersion, null));
                    }
                    else if (!tracked)
                    {
                        var r = HashDiffTableSync.Apply(source, target, sourceCatalog, targetCatalog, key, states.GetValueOrDefault(key), settings);
                        tables.Add(new DatabaseSyncTable(key, r.Method, r.Inserted, r.Deleted));
                        newStates.Add(r.State);
                    }
                    else if (NeedsFullCopy(key))
                    {
                        var r = FullTableCopy.Copy(source, target, table, settings);
                        tables.Add(new DatabaseSyncTable(key, "full", r.RowsCopied, r.RowsDeleted));
                        newStates.Add(new DbSyncTableState(key, "tracked", startVersion, null));
                    }
                    else
                    {
                        var r = ChangeTrackedTableSync.Apply(source, target, table, lastVersion!.Value, settings);
                        tables.Add(new DatabaseSyncTable(key, "changetracking", r.Upserted, r.Deleted));
                        newStates.Add(new DbSyncTableState(key, "tracked", startVersion, null));
                    }
                }
                guard.Release();
            }

            var message = $"{tables.Count} table(s) synced";
            watermarks.Commit(name, targetKey, startVersion, startUtc.Value, newStates, message);
            return new DatabaseSyncResult(name, targetKey, DatabaseSyncOutcome.Succeeded, startVersion, startUtc, tables, message);
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }

        DatabaseSyncResult Failed(string message)
        {
            watermarks.Fail(name, targetKey, message);
            return new DatabaseSyncResult(name, targetKey, DatabaseSyncOutcome.Failed, startVersion, startUtc, tables, message);
        }
    }
}
