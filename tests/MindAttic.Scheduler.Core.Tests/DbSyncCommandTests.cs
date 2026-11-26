using MindAttic.Scheduler.Cli;
using MindAttic.Scheduler.Core.DbSync;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary><c>dbsync</c>'s flags, database selection and exit codes, with the runner faked (no database is touched).</summary>
public class DbSyncCommandTests
{
    private const string Target = "Server=remote.example;Database=X;User ID=u;Password=p";

    private static DbSyncDatabase Db(string name, bool configured = true, bool enabled = true) =>
        new DbSyncDatabase(name, DbSyncSettings.LocalDb, configured ? Target : null, enabled) { TargetConnectionString = configured ? Target : null };

    private static DatabaseSyncResult Result(DbSyncDatabase db, DatabaseSyncOutcome outcome, int tables = 0, string? message = null, bool mismatch = false) =>
        new(db.Name, db.DescribeTarget(), outcome, 7, DateTime.UtcNow,
            Enumerable.Range(0, tables).Select(i => new DatabaseSyncTable($"dbo.T{i}", "full", 1, 0)).ToList(), message)
        { SchemaMismatch = mismatch };

    [Test]
    public void ParsesRepeatedDbAndSwitches()
    {
        var o = DbSyncCommand.Parse(["--db", "Prose", "--full", "--DB", "Gadfly", "--dry-run", "--json"]);

        Assert.That(o.Databases, Is.EqualTo(new[] { "Prose", "Gadfly" }));
        Assert.That(o.Full && o.DryRun && o.Json, Is.True);
    }

    [Test]
    public void NoFlagsMeansEveryDatabaseAndARealRun()
    {
        var o = DbSyncCommand.Parse([]);

        Assert.That(o.Databases, Is.Empty);
        Assert.That(o.Full || o.DryRun || o.Json, Is.False);
    }

    [TestCase("--db")]
    [TestCase("--nope")]
    [TestCase("Prose")]
    public void BadArgumentsAreUsageErrors(string arg) =>
        Assert.Throws<UsageException>(() => DbSyncCommand.Parse([arg]));

    [Test]
    public void DbFiltersByNameIgnoringCaseInSettingsOrder()
    {
        var all = new[] { Db("A"), Db("B"), Db("C") };

        var picked = DbSyncCommand.Select(all, ["c", "A"]);

        Assert.That(picked.Select(d => d.Name), Is.EqualTo(new[] { "A", "C" }));
        Assert.That(DbSyncCommand.Select(all, []), Is.EqualTo(all));
    }

    [Test]
    public void AnUnknownDbIsAUsageError() =>
        Assert.Throws<UsageException>(() => DbSyncCommand.Select([Db("A")], ["Nope"]));

    [Test]
    public void OnlyEnabledConfiguredDatabasesRunAndTheRestAreReported()
    {
        var ran = new List<string>();
        var dbs = new[] { Db("Ready"), Db("NoTarget", configured: false), Db("Off", enabled: false) };

        var lines = DbSyncCommand.RunAll(dbs, 2, db => { lock (ran) ran.Add(db.Name); return Result(db, DatabaseSyncOutcome.Succeeded, tables: 3); });

        Assert.That(ran, Is.EqualTo(new[] { "Ready" }));
        Assert.That(lines.Select(l => l.Text), Is.EqualTo(new[] { "Ready: synced 3 tables", "NoTarget: not configured", "Off: disabled" }));
        Assert.That(DbSyncCommand.ExitCode(lines), Is.EqualTo(0));
    }

    [Test]
    public void NothingConfiguredRunsNothingAndExitsZero()
    {
        var lines = DbSyncCommand.RunAll(DbSyncSettings.DefaultDatabases, 2, _ => throw new AssertionException("nothing should run"));

        Assert.That(lines, Has.All.Matches<DbSyncLine>(l => l.Status == "not configured" && l.Result is null));
        Assert.That(DbSyncCommand.ExitCode(lines), Is.EqualTo(0));
    }

    [Test]
    public void ARunnerThatThrowsFailsOnlyItsOwnDatabase()
    {
        var dbs = new[] { Db("Bad"), Db("Good") };

        var lines = DbSyncCommand.RunAll(dbs, 1, db => db.Name == "Bad" ? throw new InvalidOperationException("boom") : Result(db, DatabaseSyncOutcome.Unchanged));

        Assert.That(lines.Select(l => l.Text), Is.EqualTo(new[] { "Bad: failed: boom", "Good: unchanged" }));
        Assert.That(DbSyncCommand.ExitCode(lines), Is.EqualTo(DbSyncCommand.Failed));
    }

    [Test]
    public void RunsAtMostMaxParallelDatabasesAtOnce()
    {
        var running = 0;
        var peak = 0;
        var dbs = Enumerable.Range(0, 6).Select(i => Db($"D{i}")).ToList();

        DbSyncCommand.RunAll(dbs, 2, db =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref running);
            return Result(db, DatabaseSyncOutcome.Succeeded);
        });

        Assert.That(peak, Is.LessThanOrEqualTo(2));
    }

    [Test]
    public void ExitCodesFollowSyncToAzure()
    {
        DbSyncLine Line(DatabaseSyncOutcome outcome, bool mismatch = false)
        {
            var r = Result(Db("X"), outcome, mismatch: mismatch, message: "why");
            return new DbSyncLine("X", DbSyncCommand.Describe(r), r);
        }

        Assert.That(DbSyncCommand.ExitCode([Line(DatabaseSyncOutcome.Succeeded), Line(DatabaseSyncOutcome.Unchanged), new DbSyncLine("N", "not configured", null)]), Is.EqualTo(0));
        Assert.That(DbSyncCommand.ExitCode([Line(DatabaseSyncOutcome.Succeeded), Line(DatabaseSyncOutcome.Failed)]), Is.EqualTo(1));
        Assert.That(DbSyncCommand.ExitCode([Line(DatabaseSyncOutcome.Failed), Line(DatabaseSyncOutcome.Failed, mismatch: true)]), Is.EqualTo(3));
        Assert.That(Line(DatabaseSyncOutcome.Failed, mismatch: true).Status, Is.EqualTo("schema mismatch: why"));
        Assert.That(Line(DatabaseSyncOutcome.Failed).Status, Is.EqualTo("failed: why"));
    }

    [Test]
    public void LogAppendsOneLinePerDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dbsync-tests-{Guid.NewGuid():N}", "dbsync.log");
        try
        {
            var lines = new[] { new DbSyncLine("A", "unchanged", null), new DbSyncLine("B", "not configured", null) };
            var options = new DbSyncOptions([], Full: false, DryRun: true, Json: false);

            DbSyncCommand.Log(lines, options, path);
            DbSyncCommand.Log(lines, options, path);

            var logged = File.ReadAllLines(path);
            Assert.That(logged, Has.Length.EqualTo(4));
            Assert.That(logged[1], Does.EndWith("[dry-run] B: not configured"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
