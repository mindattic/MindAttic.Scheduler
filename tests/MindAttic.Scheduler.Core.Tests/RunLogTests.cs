using System.Diagnostics;
using MindAttic.Scheduler.Core;
using Microsoft.Data.SqlClient;
using Xunit;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>A fact that runs only where LocalDB is installed.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
        var found = Directory.Exists(exe) &&
            Directory.EnumerateFiles(exe, "SqlLocalDB.exe", SearchOption.AllDirectories).Any();
        if (!found) Skip = "LocalDB (SqlLocalDB.exe) is not installed on this machine.";
    }
}

/// <summary>The run log against a scratch LocalDB database (dropped afterwards).</summary>
public sealed class RunLogTests : IDisposable
{
    private readonly string dbName = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
    private readonly string spool = Path.Combine(Path.GetTempPath(), $"scheduler-spool-{Guid.NewGuid():N}.jsonl");

    private string ConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={dbName};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private SchedulerDb Db() => new(ConnectionString, "test");

    public void Dispose()
    {
        File.Delete(spool);
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

    [LocalDbFact]
    public void MigrateIsIdempotent()
    {
        var db = Db();
        db.Migrate();
        db.Migrate();

        Assert.Equal(SchedulerDb.SchemaVersion, db.ReadSchemaVersion());
    }

    [LocalDbFact]
    public void RecordsAStartAndAnEnd()
    {
        var db = Db();
        db.Migrate();
        var log = new RunLog(db, spool);
        var queued = DateTimeOffset.Now.AddSeconds(-30);

        var key = log.Start("Job.A", exclusive: true, queued);
        log.MarkStarted(key, queued.AddSeconds(10));
        log.End(key, queued.AddSeconds(25), exitCode: 3);

        var run = Assert.Single(log.Runs("Job.A"));
        Assert.Equal("failed", run.Outcome);
        Assert.Equal(3, run.ExitCode);
        Assert.True(run.Exclusive);
        Assert.Equal(TimeSpan.FromSeconds(10), run.Wait);
        Assert.Equal(TimeSpan.FromSeconds(15), run.Duration);
        Assert.False(File.Exists(spool));
    }

    [LocalDbFact]
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

        Assert.Equal(2, s.MeasuredRuns);
        Assert.Equal(TimeSpan.FromMinutes(15), s.UsualDuration);
        Assert.Equal("failed", s.Last!.Outcome);
    }

    [LocalDbFact]
    public void MarksARunWhoseProcessIsGoneAbandoned()
    {
        var db = Db();
        db.Migrate();
        var log = new RunLog(db, spool);
        using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        gone.WaitForExit();

        log.Start("Job.C", false, DateTimeOffset.Now, gone.Id);
        log.Start("Job.D", false, DateTimeOffset.Now); // this process: alive

        Assert.Equal(1, log.SweepAbandoned());
        Assert.Equal("abandoned", log.Runs("Job.C").Single().Outcome);
        Assert.Equal("running", log.Runs("Job.D").Single().Outcome);
    }

    [LocalDbFact]
    public void SpoolsWhenTheDatabaseIsUnreachableThenFlushes()
    {
        var real = Db();
        real.Migrate();
        var down = new SchedulerDb(@"Server=(localdb)\NoSuchInstance_MindAtticTests;Database=x;Integrated Security=true;Connect Timeout=2", "test");
        var offline = new RunLog(down, spool);

        var key = offline.Start("Job.E", false, DateTimeOffset.Now.AddMinutes(-2));
        offline.End(key, DateTimeOffset.Now, 0);

        Assert.NotNull(offline.LastError);
        Assert.Equal(2, File.ReadAllLines(spool).Count(l => l.Length > 0));
        Assert.Empty(new RunLog(real, spool + ".unused").Runs("Job.E"));

        var online = new RunLog(real, spool);
        Assert.Equal(2, online.FlushSpool());

        Assert.False(File.Exists(spool));
        var run = Assert.Single(online.Runs("Job.E"));
        Assert.Equal("succeeded", run.Outcome);
    }
}
