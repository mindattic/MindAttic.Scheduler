using System.Diagnostics;
using MindAttic.Scheduler.Core;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>The run log against a scratch LocalDB database per test (dropped afterwards).</summary>
public sealed class RunLogTests
{
    private string dbName = "";
    private string spool = "";

    private string ConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={dbName};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private SchedulerDb Db() => new(ConnectionString, "test");

    // IgnoreInaccessible: a full SQL Server instance installed alongside LocalDB has folders a normal
    // user can't list (MSSQL16.MSSQLSERVER\MSSQL\Backup), and SearchOption.AllDirectories throws on them.
    private static bool LocalDbInstalled()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
        return Directory.Exists(root) &&
            Directory.EnumerateFiles(root, "SqlLocalDB.exe",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Any();
    }

    [SetUp]
    public void SetUp()
    {
        if (!LocalDbInstalled()) Assert.Ignore("LocalDB (SqlLocalDB.exe) is not installed on this machine.");
        dbName = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
        spool = Path.Combine(Path.GetTempPath(), $"scheduler-spool-{Guid.NewGuid():N}.jsonl");
    }

    [TearDown]
    public void TearDown()
    {
        if (spool.Length > 0) File.Delete(spool);
        if (dbName.Length == 0) return;
        try
        {
            SqlConnection.ClearAllPools();
            using var c = new SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true");
            c.Open();
            using var cmd = new SqlCommand(
                $"IF DB_ID(N'{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END", c);
            cmd.ExecuteNonQuery();
        }
        catch (SqlException) { /* best effort */ }
    }

    [Test]
    public void MigrateIsIdempotent()
    {
        var db = Db();
        db.Migrate();
        db.Migrate();

        Assert.That(db.ReadSchemaVersion(), Is.EqualTo(SchedulerDb.SchemaVersion));
    }

    [Test]
    public void RecordsAStartAndAnEnd()
    {
        var db = Db();
        db.Migrate();
        var log = new RunLog(db, spool);
        var queued = DateTimeOffset.Now.AddSeconds(-30);

        var key = log.Start("Job.A", exclusive: true, queued);
        log.MarkStarted(key, queued.AddSeconds(10));
        log.End(key, queued.AddSeconds(25), exitCode: 3);

        var runs = log.Runs("Job.A");
        Assert.That(runs, Has.Count.EqualTo(1));
        var run = runs[0];
        Assert.That(run.Outcome, Is.EqualTo("failed"));
        Assert.That(run.ExitCode, Is.EqualTo(3));
        Assert.That(run.Exclusive, Is.True);
        Assert.That(run.Wait, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(run.Duration, Is.EqualTo(TimeSpan.FromSeconds(15)));
        Assert.That(File.Exists(spool), Is.False);
    }

    [Test]
    public void SummariesAverageTheSuccessfulRuns()
    {
        var db = Db();
        db.Migrate();
        var log = new RunLog(db, spool);
        var t = DateTimeOffset.Now.AddHours(-1);
        foreach (var (minutes, exit) in new[] { (10, 0), (20, 0), (90, 1) })
        {
            var key = log.Start("Job.B", false, t);
            log.End(key, t.AddMinutes(minutes), exit);
            t = t.AddMinutes(1);
        }

        var s = log.Summaries()["Job.B"];

        Assert.That(s.MeasuredRuns, Is.EqualTo(2));
        Assert.That(s.UsualDuration, Is.EqualTo(TimeSpan.FromMinutes(15)));
        Assert.That(s.Last!.Outcome, Is.EqualTo("failed"));
    }

    [Test]
    public void MarksARunWhoseProcessIsGoneAbandoned()
    {
        var db = Db();
        db.Migrate();
        var log = new RunLog(db, spool);
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();

        log.Start("Job.C", false, DateTimeOffset.Now, gone.Id);
        log.Start("Job.D", false, DateTimeOffset.Now); // this process: alive

        Assert.That(log.SweepAbandoned(), Is.EqualTo(1));
        Assert.That(log.Runs("Job.C").Single().Outcome, Is.EqualTo("abandoned"));
        Assert.That(log.Runs("Job.D").Single().Outcome, Is.EqualTo("running"));
    }

    [Test]
    public void SpoolsWhenTheDatabaseIsUnreachableThenFlushes()
    {
        var real = Db();
        real.Migrate();
        var down = new SchedulerDb(@"Server=(localdb)\NoSuchInstance_MindAtticTests;Database=x;Integrated Security=true;Connect Timeout=2", "test");
        var offline = new RunLog(down, spool);

        var key = offline.Start("Job.E", false, DateTimeOffset.Now.AddMinutes(-2));
        offline.End(key, DateTimeOffset.Now, 0);

        Assert.That(offline.LastError, Is.Not.Null);
        Assert.That(File.ReadAllLines(spool).Count(l => l.Length > 0), Is.EqualTo(2));
        Assert.That(new RunLog(real, spool + ".unused").Runs("Job.E"), Is.Empty);

        var online = new RunLog(real, spool);
        Assert.That(online.FlushSpool(), Is.EqualTo(2));

        Assert.That(File.Exists(spool), Is.False);
        var runs = online.Runs("Job.E");
        Assert.That(runs, Has.Count.EqualTo(1));
        Assert.That(runs[0].Outcome, Is.EqualTo("succeeded"));
    }
}
