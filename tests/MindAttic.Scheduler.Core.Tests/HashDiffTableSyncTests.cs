using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>The hashdiff fallback sync of a table with no primary key, between two scratch LocalDB databases (dropped afterwards).</summary>
public sealed class HashDiffTableSyncTests
{
    private const string Master = @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    // No primary key; a non-unique clustered index on At gives the key ranges and the append watermark.
    private const string LogDdl = """
        CREATE TABLE dbo.Log (At INT NOT NULL, Msg NVARCHAR(50) NULL, Amount DECIMAL(9,2) NULL);
        CREATE CLUSTERED INDEX CX_Log ON dbo.Log (At);
        """;

    // A heap: no index at all, so one range and no append watermark.
    private const string HeapDdl = "CREATE TABLE dbo.Heap (Msg NVARCHAR(50) NULL, N INT NULL);";

    private const string Seed = """
        INSERT dbo.Log (At, Msg, Amount) VALUES (1, N'a', 1.00), (1, N'a', 1.00), (2, NULL, NULL), (3, N'c', 3.00), (3, N'c', 3.00), (4, N'd', NULL);
        """;

    private const string HeapSeed = "INSERT dbo.Heap (Msg, N) VALUES (N'x', 1), (N'x', 1), (NULL, NULL), (N'y', 2);";

    private const string LogRows = "SELECT At, Msg, Amount FROM dbo.Log ORDER BY At, Msg, Amount";
    private const string HeapRows = "SELECT Msg, N FROM dbo.Heap ORDER BY Msg, N";

    private readonly List<string> databases = [];
    private DbSyncSettings settings = new() { BulkBatchSize = 2, CommandTimeoutSeconds = 60 };

    private static string ConnectionString(string db) =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={db};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private string CreateDatabase(params string[] batches)
    {
        var name = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
        databases.Add(name);
        using (var m = new SqlConnection(Master))
        {
            m.Open();
            using var create = new SqlCommand($"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET AUTO_CLOSE OFF;", m);
            create.ExecuteNonQuery();
        }
        foreach (var batch in batches) Run(name, batch);
        return name;
    }

    private static void Run(string db, string sql)
    {
        using var c = new SqlConnection(ConnectionString(db));
        c.Open();
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every row as text, sorted on every column: equal lists are equal multisets.</summary>
    private static List<string> Rows(string db, string sql)
    {
        using var c = new SqlConnection(ConnectionString(db));
        c.Open();
        using var cmd = new SqlCommand(sql, c);
        using var rd = cmd.ExecuteReader();
        var rows = new List<string>();
        while (rd.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? "NULL" : Convert.ToString(rd.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return rows;
    }

    private HashDiffResult Sync(string source, string target, string table, DbSyncTableState? previous = null)
    {
        using var s = new SqlConnection(ConnectionString(source));
        using var t = new SqlConnection(ConnectionString(target));
        s.Open();
        t.Open();
        return HashDiffTableSync.Apply(s, t, TableCatalog.Read(s), TableCatalog.Read(t), table, previous, settings);
    }

    private (string Source, string Target) SeededPair() => (CreateDatabase(LogDdl, Seed), CreateDatabase(LogDdl, Seed));

    [SetUp]
    public void SetUp()
    {
        if (!LocalDb.Installed()) Assert.Ignore("LocalDB (SqlLocalDB.exe) is not installed on this machine.");
    }

    [TearDown]
    public void TearDown()
    {
        SqlConnection.ClearAllPools();
        foreach (var db in databases)
        {
            try
            {
                using var c = new SqlConnection(Master);
                c.Open();
                using var cmd = new SqlCommand(
                    $"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", c);
                cmd.ExecuteNonQuery();
            }
            catch (SqlException) { /* best effort */ }
        }
        databases.Clear();
    }

    [Test]
    public void AnAppendOnlyTailCopiesOnlyTheTail()
    {
        var (source, target) = SeededPair();
        Run(source, "INSERT dbo.Log (At, Msg, Amount) VALUES (5, N'e', 5.00), (5, N'e', 5.00), (6, NULL, 6.00);");

        var result = Sync(source, target, "dbo.Log");

        Assert.That(result.Method, Is.EqualTo("append"));
        Assert.That(result.Inserted, Is.EqualTo(3));
        Assert.That(result.Deleted, Is.EqualTo(0));
        Assert.That(Rows(target, LogRows), Is.EqualTo(Rows(source, LogRows)));
    }

    [Test]
    public void AnUpdateAndADeleteAreFixedByHashDiff()
    {
        var (source, target) = SeededPair();
        Run(source, "UPDATE TOP (1) dbo.Log SET Msg = N'A' WHERE At = 1; DELETE TOP (1) FROM dbo.Log WHERE At = 3;");

        var result = Sync(source, target, "dbo.Log");

        Assert.That(result.Method, Is.EqualTo("hashdiff"));
        // Each hash whose count differs is replaced whole: both target (1,a) and both (3,c); one (1,a), one (1,A), one (3,c) back.
        Assert.That(result.Deleted, Is.EqualTo(4));
        Assert.That(result.Inserted, Is.EqualTo(3));
        Assert.That(Rows(target, LogRows), Is.EqualTo(Rows(source, LogRows)));
    }

    [Test]
    public void AChangeInTheMiddleIsFoundAcrossSeveralKeyRanges()
    {
        settings = settings with { HashDiffRangeRows = 2 };
        var (source, target) = SeededPair();
        Run(source, "UPDATE dbo.Log SET Amount = 2.50 WHERE At = 2; INSERT dbo.Log (At, Msg) VALUES (2, N'b');");

        var result = Sync(source, target, "dbo.Log");

        Assert.That(result.Method, Is.EqualTo("hashdiff"));
        Assert.That(Rows(target, LogRows), Is.EqualTo(Rows(source, LogRows)));
    }

    [Test]
    public void AHeapWithDuplicatesEndsEqualToTheSource()
    {
        var source = CreateDatabase(HeapDdl, HeapSeed);
        var target = CreateDatabase(HeapDdl, HeapSeed);
        Run(source, "DELETE TOP (1) FROM dbo.Heap WHERE Msg = N'x'; UPDATE dbo.Heap SET N = 0 WHERE Msg IS NULL; INSERT dbo.Heap (Msg, N) VALUES (N'y', 2);");

        var result = Sync(source, target, "dbo.Heap");

        Assert.That(result.Method, Is.EqualTo("hashdiff"));
        Assert.That(Rows(target, HeapRows), Is.EqualTo(Rows(source, HeapRows)));
    }

    [Test]
    public void AnUnchangedTableIsReportedUnchanged()
    {
        var (source, target) = SeededPair();

        var result = Sync(source, target, "dbo.Log");

        Assert.That(result.Method, Is.EqualTo("unchanged"));
        Assert.That(result.Reason, Does.Contain("fingerprints agree"));
        Assert.That(result.State.Method, Is.EqualTo(HashDiffTableSync.Method));
        Assert.That(Rows(target, LogRows), Is.EqualTo(Rows(source, LogRows)));
    }

    [Test]
    public void WithTheLastRunsStateAnUnwrittenTableIsUnchangedWithoutAScan()
    {
        var (source, target) = SeededPair();
        Thread.Sleep(50);
        var first = Sync(source, target, "dbo.Log");

        var second = Sync(source, target, "dbo.Log", first.State);

        Assert.That(second.Method, Is.EqualTo("unchanged"));
        Assert.That(second.Reason, Does.Contain("write statistics"));
    }

    [Test]
    public void AWriteSinceTheLastRunIsNotMissed()
    {
        var (source, target) = SeededPair();
        Thread.Sleep(50);
        var first = Sync(source, target, "dbo.Log");
        Thread.Sleep(50);
        Run(source, "UPDATE TOP (1) dbo.Log SET Msg = N'D' WHERE At = 4;");

        var second = Sync(source, target, "dbo.Log", first.State);

        Assert.That(second.Method, Is.EqualTo("hashdiff"));
        Assert.That(Rows(target, LogRows), Is.EqualTo(Rows(source, LogRows)));
    }

    [Test]
    public void AnEmptyTargetIsFilledAndAnEmptySourceEmptiesTheTarget()
    {
        var source = CreateDatabase(LogDdl, Seed);
        var target = CreateDatabase(LogDdl);

        var filled = Sync(source, target, "dbo.Log");
        Assert.That(filled.Method, Is.EqualTo("full"));
        Assert.That(filled.Inserted, Is.EqualTo(6));
        Assert.That(Rows(target, LogRows), Is.EqualTo(Rows(source, LogRows)));

        Run(source, "DELETE FROM dbo.Log;");
        var emptied = Sync(source, target, "dbo.Log");
        Assert.That(emptied.Deleted, Is.EqualTo(6));
        Assert.That(Rows(target, LogRows), Is.Empty);
    }
}
