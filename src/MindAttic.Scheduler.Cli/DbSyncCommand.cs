using MindAttic.Scheduler.Core;
using MindAttic.Scheduler.Core.DbSync;

namespace MindAttic.Scheduler.Cli;

/// <summary>What <c>dbsync</c> was asked to do.</summary>
internal sealed record DbSyncOptions(IReadOnlyList<string> Databases, bool Full, bool DryRun, bool Json);

/// <summary>One database's line in <c>dbsync</c>'s report: <see cref="Result"/> is null when it was not run.</summary>
internal sealed record DbSyncLine(string Database, string Status, DatabaseSyncResult? Result)
{
    public string Text => $"{Database}: {Status}";
}

/// <summary>
/// <c>dbsync [--db &lt;name&gt;]... [--full] [--dry-run] [--json]</c> (M.SCHD-T70): runs <see cref="DatabaseSyncRunner"/>
/// for every enabled, configured database, <see cref="DbSyncSettings.MaxParallelDatabases"/> at a time, one line each to
/// stdout and <see cref="SchedulerPaths.DbSyncLog"/>. Exit codes follow Sync-ToAzure.ps1: 0 when every database synced,
/// was unchanged, disabled or not configured; 1 when any failed; 3 on a schema mismatch; 2 usage.
/// </summary>
internal static class DbSyncCommand
{
    public const int Failed = 1;
    public const int SchemaMismatch = 3;

    public static int Execute(string[] args)
    {
        var options = Parse(args);
        var settings = DbSyncSettings.Load();
        var selected = Select(settings.Databases, options.Databases);

        // Only a database that is actually run needs the Scheduler database (its watermarks).
        var runner = new Lazy<DatabaseSyncRunner>(() => new DatabaseSyncRunner(settings, new WatermarkStore(SchedulerDb.FromEnvironment())));
        var lines = RunAll(selected, settings.MaxParallelDatabases, db => runner.Value.Run(db, options.Full, options.DryRun));

        Log(lines, options, SchedulerPaths.DbSyncLog);
        if (options.Json) Json.Write(View(lines, options));
        else foreach (var l in lines) Console.WriteLine(l.Text);
        return ExitCode(lines);
    }

    /// <summary>Parses the flags after <c>dbsync</c>; <c>--db</c> may be given more than once.</summary>
    internal static DbSyncOptions Parse(IReadOnlyList<string> args)
    {
        var databases = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], "--db", StringComparison.OrdinalIgnoreCase)) { rest.Add(args[i]); continue; }
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1])) throw new UsageException("--db needs a value.");
            databases.Add(args[++i].Trim());
        }
        var flags = new Flags(rest);
        flags.AllowOnly("full", "dry-run", "json");
        return new DbSyncOptions(databases, flags.Has("full"), flags.Has("dry-run"), flags.Has("json"));
    }

    /// <summary>The databases to report on, in settings order: all of them, or the named ones (an unknown name is a usage error).</summary>
    internal static IReadOnlyList<DbSyncDatabase> Select(IReadOnlyList<DbSyncDatabase> all, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return all;
        foreach (var n in names)
            if (!all.Any(d => string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase)))
                throw new UsageException($"No database named '{n}' (dbsync.json and the defaults list {string.Join(", ", all.Select(d => d.Name))}).");
        return all.Where(d => names.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Runs each enabled, configured database through <paramref name="run"/>, <paramref name="maxParallel"/> at a time,
    /// each on its own (one throwing fails only that one); the rest are reported as disabled or not configured.
    /// </summary>
    internal static IReadOnlyList<DbSyncLine> RunAll(IReadOnlyList<DbSyncDatabase> databases, int maxParallel, Func<DbSyncDatabase, DatabaseSyncResult> run)
    {
        var lines = new DbSyncLine[databases.Count];
        var runnable = new List<int>();
        for (var i = 0; i < databases.Count; i++)
        {
            var db = databases[i];
            if (db.Enabled && db.IsConfigured) runnable.Add(i);
            else lines[i] = new DbSyncLine(db.Name, db.Status, null);
        }

        Parallel.ForEach(runnable, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallel) }, i =>
        {
            var db = databases[i];
            DatabaseSyncResult result;
            try { result = run(db); }
            catch (Exception ex)
            {
                result = new DatabaseSyncResult(db.Name, db.DescribeTarget(), DatabaseSyncOutcome.Failed, null, null, [], ex.Message);
            }
            lines[i] = new DbSyncLine(db.Name, Describe(result), result);
        });
        return lines;
    }

    /// <summary>"synced N tables", "unchanged", "schema mismatch: ..." or "failed: ...".</summary>
    internal static string Describe(DatabaseSyncResult r)
    {
        var reason = r.Message?.Split('\n')[0].Trim();
        return r.Outcome switch
        {
            DatabaseSyncOutcome.Failed when r.SchemaMismatch => $"schema mismatch: {reason}",
            DatabaseSyncOutcome.Failed => $"failed: {reason}",
            DatabaseSyncOutcome.Unchanged => r.DryRun ? "unchanged (dry run)" : "unchanged",
            _ when r.DryRun => reason ?? $"dry run: {r.Tables.Count} table(s) would sync",
            _ when r.Bootstrapped => $"bootstrapped schema, synced {r.Tables.Count} tables",
            _ => $"synced {r.Tables.Count} tables",
        };
    }

    /// <summary>3 when any database's schema mismatched, else 1 when any failed, else 0.</summary>
    internal static int ExitCode(IReadOnlyList<DbSyncLine> lines)
    {
        var failed = lines.Where(l => l.Result?.Outcome == DatabaseSyncOutcome.Failed).ToList();
        return failed.Any(l => l.Result!.SchemaMismatch) ? SchemaMismatch : failed.Count > 0 ? Failed : 0;
    }

    /// <summary>Appends one timestamped line per database to <paramref name="path"/>.</summary>
    internal static void Log(IReadOnlyList<DbSyncLine> lines, DbSyncOptions options, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
        var mode = (options.DryRun ? " [dry-run]" : "") + (options.Full ? " [full]" : "");
        File.AppendAllLines(path, lines.Select(l => $"{stamp}{mode} {l.Text}"));
    }

    /// <summary>The JSON shape of <c>dbsync --json</c>.</summary>
    internal static object View(IReadOnlyList<DbSyncLine> lines, DbSyncOptions options) => new
    {
        dryRun = options.DryRun,
        full = options.Full,
        exitCode = ExitCode(lines),
        databases = lines.Select(l => new
        {
            name = l.Database,
            status = l.Status,
            outcome = l.Result is { } r ? (r.Outcome == DatabaseSyncOutcome.Failed && r.SchemaMismatch ? "schemaMismatch" : r.Outcome.ToString().ToLowerInvariant()) : l.Status,
            target = l.Result?.Target,
            bootstrapped = l.Result?.Bootstrapped,
            startVersion = l.Result?.StartVersion,
            startUtc = l.Result?.StartUtc,
            message = l.Result?.Message,
            tables = l.Result?.Tables.Select(t => new { table = t.Table, method = t.Method, written = t.Written, deleted = t.Deleted }),
        }),
    };
}
