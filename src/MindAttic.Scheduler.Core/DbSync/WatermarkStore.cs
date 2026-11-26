using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>
/// Where a database's incremental sync to one target resumes from (M.SCHD-T62): the change-tracking version and UTC
/// time captured at the START of the last successful run, plus how the last run ended.
/// </summary>
public sealed record DbSyncWatermark(
    string DatabaseName,
    string TargetKey,
    long? LastVersion,
    DateTime LastStartUtc,
    DateTime? LastEndUtc,
    string LastOutcome,
    string? LastMessage);

/// <summary>One table's bookkeeping for a database and target; <see cref="Method"/> is tracked, hashdiff or temporal.</summary>
public sealed record DbSyncTableState(string TableName, string Method, long? LastVersion, string? StateJson);

/// <summary>
/// Reads and writes <c>dbo.DbSyncWatermark</c> and <c>dbo.DbSyncTableState</c> in the Scheduler database. The watermark
/// only moves in <see cref="Commit"/>, which a run calls once it has succeeded; <see cref="Fail"/> records the outcome
/// and leaves the watermark where it was, so the next run repeats the failed one's work.
/// </summary>
public sealed class WatermarkStore(SchedulerDb db)
{
    /// <summary>The watermark to resume from; null when no run has succeeded yet (a first run copies everything).</summary>
    public DbSyncWatermark? Get(string database, string target)
    {
        using var c = db.Open();
        using var cmd = new SqlCommand(
            """
            SELECT LastVersion, LastStartUtc, LastEndUtc, LastOutcome, LastMessage FROM dbo.DbSyncWatermark
            WHERE DatabaseName = @db AND TargetKey = @t AND LastStartUtc IS NOT NULL
            """, c);
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", target);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new DbSyncWatermark(
            database,
            target,
            r.IsDBNull(0) ? null : r.GetInt64(0),
            DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc),
            r.IsDBNull(2) ? null : DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc),
            r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4));
    }

    /// <summary>The per-table bookkeeping stored for <paramref name="database"/> and <paramref name="target"/>.</summary>
    public IReadOnlyList<DbSyncTableState> TableStates(string database, string target)
    {
        using var c = db.Open();
        using var cmd = new SqlCommand(
            """
            SELECT TableName, Method, LastVersion, StateJson FROM dbo.DbSyncTableState
            WHERE DatabaseName = @db AND TargetKey = @t ORDER BY TableName
            """, c);
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", target);
        using var r = cmd.ExecuteReader();
        var list = new List<DbSyncTableState>();
        while (r.Read())
            list.Add(new DbSyncTableState(
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    /// <summary>
    /// Records a successful run in one transaction: the watermark becomes <paramref name="startVersion"/> and
    /// <paramref name="startUtc"/>, both captured when the run STARTED, and each of <paramref name="tableStates"/> is
    /// written over that table's earlier row. Tables not named keep their rows.
    /// </summary>
    public void Commit(string database, string target, long? startVersion, DateTime startUtc,
        IEnumerable<DbSyncTableState>? tableStates = null, string? message = null)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = new SqlCommand(
            """
            UPDATE dbo.DbSyncWatermark
            SET LastVersion = @v, LastStartUtc = @start, LastEndUtc = SYSUTCDATETIME(), LastOutcome = 'succeeded', LastMessage = @m
            WHERE DatabaseName = @db AND TargetKey = @t;
            IF @@ROWCOUNT = 0
                INSERT dbo.DbSyncWatermark (DatabaseName, TargetKey, LastVersion, LastStartUtc, LastEndUtc, LastOutcome, LastMessage)
                VALUES (@db, @t, @v, @start, SYSUTCDATETIME(), 'succeeded', @m);
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@t", target);
            cmd.Parameters.AddWithValue("@v", (object?)startVersion ?? DBNull.Value);
            cmd.Parameters.Add("@start", System.Data.SqlDbType.DateTime2).Value = ToUtc(startUtc);
            cmd.Parameters.AddWithValue("@m", (object?)message ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        foreach (var s in tableStates ?? [])
        {
            using var cmd = new SqlCommand(
                """
                UPDATE dbo.DbSyncTableState SET Method = @method, LastVersion = @v, StateJson = @json
                WHERE DatabaseName = @db AND TargetKey = @t AND TableName = @table;
                IF @@ROWCOUNT = 0
                    INSERT dbo.DbSyncTableState (DatabaseName, TargetKey, TableName, Method, LastVersion, StateJson)
                    VALUES (@db, @t, @table, @method, @v, @json);
                """, c, tx);
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@t", target);
            cmd.Parameters.AddWithValue("@table", s.TableName);
            cmd.Parameters.AddWithValue("@method", s.Method);
            cmd.Parameters.AddWithValue("@v", (object?)s.LastVersion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@json", (object?)s.StateJson ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Records a failed run: its end time and message. The watermark and table states stay as they were.</summary>
    public void Fail(string database, string target, string message)
    {
        using var c = db.Open();
        using var cmd = new SqlCommand(
            """
            UPDATE dbo.DbSyncWatermark SET LastEndUtc = SYSUTCDATETIME(), LastOutcome = 'failed', LastMessage = @m
            WHERE DatabaseName = @db AND TargetKey = @t;
            IF @@ROWCOUNT = 0
                INSERT dbo.DbSyncWatermark (DatabaseName, TargetKey, LastVersion, LastStartUtc, LastEndUtc, LastOutcome, LastMessage)
                VALUES (@db, @t, NULL, NULL, SYSUTCDATETIME(), 'failed', @m);
            """, c);
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", target);
        cmd.Parameters.AddWithValue("@m", message);
        cmd.ExecuteNonQuery();
    }

    private static DateTime ToUtc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;
}
