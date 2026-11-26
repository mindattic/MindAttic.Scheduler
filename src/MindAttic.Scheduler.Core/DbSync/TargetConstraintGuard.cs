using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>
/// Suspends the target's foreign keys and triggers around a sync (M.SCHD-T66, ported from Sync-ToAzure.ps1's Sync-All,
/// Set-TableMarker, Clear-TableMarker and the FK and trigger part of Repair-State), so upserts and full copies in any
/// table order cannot trip a foreign key and no trigger fires on rows the source already processed. Every enabled
/// foreign key declared on or referencing a guarded table gets NOCHECK and every enabled trigger on a guarded table is
/// disabled; releasing enables the triggers again and re-checks the foreign keys WITH CHECK (one that was untrusted
/// before goes back to untrusted). While suspended, each affected table carries a <see cref="Marker"/> extended
/// property naming what was turned off, so a guard left behind by a crash is repaired by <see cref="Repair"/>, which
/// <see cref="Engage"/> runs first.
/// </summary>
public sealed class TargetConstraintGuard : IDisposable
{
    /// <summary>The extended property on each table the guard changed.</summary>
    public const string Marker = "SyncToRemote.ConstraintsSuspended";

    private readonly SqlConnection target;
    private readonly DbSyncSettings settings;
    private readonly Dictionary<string, Suspended> suspended;
    private bool released;

    private TargetConstraintGuard(SqlConnection target, DbSyncSettings settings, Dictionary<string, Suspended> suspended)
    {
        this.target = target;
        this.settings = settings;
        this.suspended = suspended;
    }

    /// <summary>Tables that carry a marker: <c>schema.name</c>.</summary>
    public IReadOnlyCollection<string> MarkedTables => suspended.Keys;

    /// <summary>
    /// Repairs any interrupted guard, then suspends the foreign keys touching <paramref name="tableKeys"/> and their
    /// triggers on the database <paramref name="target"/> is open on.
    /// </summary>
    public static TargetConstraintGuard Engage(SqlConnection target, IEnumerable<string> tableKeys, DbSyncSettings settings)
    {
        Repair(target, settings);

        var catalog = TableCatalog.Read(target);
        var keys = tableKeys.ToHashSet(StringComparer.Ordinal);
        var plan = new Dictionary<string, Suspended>(StringComparer.Ordinal);
        Suspended For(string key) => plan.TryGetValue(key, out var s) ? s : plan[key] = new Suspended();

        foreach (var fk in catalog.Constraints.Where(c => c.Kind == "FK" && !c.Disabled &&
                     (keys.Contains(c.TableKey) || (c.ReferencedTableKey is { } r && keys.Contains(r)))))
            For(fk.TableKey).ForeignKeys.Add(new SuspendedKey(fk.Name, !fk.NotTrusted));

        foreach (var key in keys.Order(StringComparer.Ordinal))
            if (catalog.Tables.TryGetValue(key, out var t))
                foreach (var tr in t.Triggers.Where(tr => !tr.Disabled))
                    For(key).Triggers.Add(tr.Name);

        var guard = new TargetConstraintGuard(target, settings, plan);
        foreach (var (key, s) in plan)
        {
            var qname = catalog.Tables[key].QName;
            SetMarker(target, settings, key, JsonSerializer.Serialize(s));
            foreach (var fk in s.ForeignKeys) Exec(target, settings, $"ALTER TABLE {qname} NOCHECK CONSTRAINT {TableCatalog.Quote(fk.Name)};");
            foreach (var tr in s.Triggers) Exec(target, settings, $"ALTER TABLE {qname} DISABLE TRIGGER {TableCatalog.Quote(tr)};");
        }
        return guard;
    }

    /// <summary>
    /// Restores what an interrupted guard left behind on the database <paramref name="target"/> is open on and clears
    /// its markers. Returns the tables repaired. Throws when a foreign key fails its re-check (orphaned rows); that
    /// table keeps its marker.
    /// </summary>
    public static IReadOnlyList<string> Repair(SqlConnection target, DbSyncSettings settings)
    {
        var marked = new List<(string Key, string QName, Suspended State)>();
        using (var cmd = new SqlCommand(
                   "SELECT SCHEMA_NAME(t.schema_id), t.name, CONVERT(NVARCHAR(4000), ep.value) FROM sys.extended_properties ep " +
                   "JOIN sys.tables t ON t.object_id = ep.major_id WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = @m;", target)
               { CommandTimeout = settings.CommandTimeoutSeconds })
        {
            cmd.Parameters.Add("@m", SqlDbType.NVarChar, 128).Value = Marker;
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var schema = rd.GetString(0);
                var name = rd.GetString(1);
                var state = rd.IsDBNull(2) ? null : JsonSerializer.Deserialize<Suspended>(rd.GetString(2));
                marked.Add(($"{schema}.{name}", $"{TableCatalog.Quote(schema)}.{TableCatalog.Quote(name)}", state ?? new Suspended()));
            }
        }

        var failures = new List<string>();
        foreach (var (key, qname, state) in marked)
        {
            try { Restore(target, settings, key, qname, state); }
            catch (SqlException ex) { failures.Add($"{key}: {ex.Message}"); }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException($"Could not restore the target's foreign keys or triggers (markers kept for the next run): {string.Join("; ", failures)}");
        return marked.Select(m => m.Key).ToList();
    }

    /// <summary>Enables the triggers, re-checks the foreign keys and clears the markers. Throws when a table could not be restored; it keeps its marker.</summary>
    public void Release()
    {
        if (released) return;
        released = true;

        var failures = new List<string>();
        foreach (var (key, s) in suspended)
        {
            var dot = key.IndexOf('.');
            var qname = $"{TableCatalog.Quote(key[..dot])}.{TableCatalog.Quote(key[(dot + 1)..])}";
            try { Restore(target, settings, key, qname, s); }
            catch (SqlException ex) { failures.Add($"{key}: {ex.Message}"); }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException($"Could not restore the target's foreign keys or triggers (markers kept for the next run): {string.Join("; ", failures)}");
    }

    /// <summary>Releases without throwing: a table that could not be restored keeps its marker, and the next guard repairs it.</summary>
    public void Dispose()
    {
        try { Release(); }
        catch (InvalidOperationException) { }
        catch (SqlException) { }
    }

    private static void Restore(SqlConnection target, DbSyncSettings settings, string key, string qname, Suspended s)
    {
        foreach (var tr in s.Triggers)
            Exec(target, settings, $"ALTER TABLE {qname} ENABLE TRIGGER {TableCatalog.Quote(tr)};");
        foreach (var fk in s.ForeignKeys)
            Exec(target, settings, $"ALTER TABLE {qname} {(fk.Trusted ? "WITH CHECK" : "WITH NOCHECK")} CHECK CONSTRAINT {TableCatalog.Quote(fk.Name)};");
        ClearMarker(target, settings, key);
    }

    private static void SetMarker(SqlConnection target, DbSyncSettings settings, string key, string value)
    {
        RunMarker(target, settings, key, value, """
            IF NOT EXISTS (SELECT 1 FROM sys.extended_properties ep WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = @m AND ep.major_id = OBJECT_ID(QUOTENAME(@s) + N'.' + QUOTENAME(@t)))
                EXEC sys.sp_addextendedproperty @name = @m, @value = @v, @level0type = N'SCHEMA', @level0name = @s, @level1type = N'TABLE', @level1name = @t;
            """);
    }

    private static void ClearMarker(SqlConnection target, DbSyncSettings settings, string key)
    {
        RunMarker(target, settings, key, null, """
            IF EXISTS (SELECT 1 FROM sys.extended_properties ep WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = @m AND ep.major_id = OBJECT_ID(QUOTENAME(@s) + N'.' + QUOTENAME(@t)))
                EXEC sys.sp_dropextendedproperty @name = @m, @level0type = N'SCHEMA', @level0name = @s, @level1type = N'TABLE', @level1name = @t;
            """);
    }

    private static void RunMarker(SqlConnection target, DbSyncSettings settings, string key, string? value, string sql)
    {
        var dot = key.IndexOf('.');
        using var cmd = new SqlCommand(sql, target) { CommandTimeout = settings.CommandTimeoutSeconds };
        cmd.Parameters.Add("@m", SqlDbType.NVarChar, 128).Value = Marker;
        cmd.Parameters.Add("@s", SqlDbType.NVarChar, 128).Value = key[..dot];
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 128).Value = key[(dot + 1)..];
        if (value is not null) cmd.Parameters.Add("@v", SqlDbType.NVarChar, 3750).Value = value;
        cmd.ExecuteNonQuery();
    }

    private static int Exec(SqlConnection c, DbSyncSettings settings, string sql)
    {
        using var cmd = new SqlCommand(sql, c) { CommandTimeout = settings.CommandTimeoutSeconds };
        return cmd.ExecuteNonQuery();
    }

    private sealed record SuspendedKey(string Name, bool Trusted);

    private sealed class Suspended
    {
        public List<SuspendedKey> ForeignKeys { get; init; } = [];
        public List<string> Triggers { get; init; } = [];
    }
}
