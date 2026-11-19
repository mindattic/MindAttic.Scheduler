using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Cli;

/// <summary><c>db migrate | status | flush</c>.</summary>
internal static class DbCommand
{
    public static int Execute(string[] args)
    {
        if (args.Length != 1) throw new UsageException("db needs one of: migrate, status, flush.");
        var db = SchedulerDb.FromEnvironment();
        switch (args[0].ToLowerInvariant())
        {
            case "migrate":
                db.Migrate();
                Console.WriteLine($"Schema at version {SchedulerDb.SchemaVersion} on {db.Describe()}.");
                Flush(db);
                return 0;
            case "status":
                Console.WriteLine($"Database: {db.Describe()}");
                try { Console.WriteLine($"Schema version: {db.ReadSchemaVersion()?.ToString() ?? "none (run 'db migrate')"}"); }
                catch (Exception ex) { Console.WriteLine($"Unreachable: {RunCommand.FirstLine(ex.Message)}"); return 1; }
                Console.WriteLine(File.Exists(SchedulerPaths.Spool)
                    ? $"Spool: {File.ReadAllLines(SchedulerPaths.Spool).Count(l => l.Length > 0)} row(s) waiting in {SchedulerPaths.Spool}"
                    : "Spool: empty");
                return 0;
            case "flush":
                return Flush(db) ? 0 : 1;
            default:
                throw new UsageException($"Unknown db verb '{args[0]}'.");
        }
    }

    private static bool Flush(SchedulerDb db)
    {
        var log = new RunLog(db);
        var sent = log.FlushSpool();
        if (log.LastError is { } e)
        {
            Console.Error.WriteLine($"Spool not flushed: {RunCommand.FirstLine(e.Message)}");
            return false;
        }
        if (sent > 0) Console.WriteLine($"Flushed {sent} spooled row(s).");
        return true;
    }
}
