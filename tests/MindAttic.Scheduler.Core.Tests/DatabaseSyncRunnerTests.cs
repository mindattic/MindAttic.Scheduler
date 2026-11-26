using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>One database's sync run between scratch LocalDB source and target databases, with a scratch Scheduler database (all dropped afterwards).</summary>
public sealed class DatabaseSyncRunnerTests
{
    private const string Master = @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private const string Schema = """
        CREATE TABLE dbo.Parent (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Parent PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
        CREATE TABLE dbo.Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY,
            ParentId INT NOT NULL CONSTRAINT FK_Child_Parent REFERENCES dbo.Parent (Id), Note NVARCHAR(50) NULL);
        CREATE TABLE dbo.Log (Msg NVARCHAR(50) NOT NULL, At INT NOT NULL);
        CREATE TABLE dbo.Account (Id INT NOT NULL CONSTRAINT PK_Account PRIMARY KEY, Balance DECIMAL(18,2) NOT NULL,
            ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL, ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
            WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.AccountHistory));
        """;

    private const string Seed = """
        INSERT dbo.Parent (Name) VALUES (N'one'), (N'two'), (N'three');
        INSERT dbo.Child (Id, ParentId, Note) VALUES (1, 1, N'a'), (2, 1, NULL), (3, 2, N'c');
        INSERT dbo.Log (Msg, At) VALUES (N'boot', 1), (N'boot', 1), (N'ready', 2);
        INSERT dbo.Account (Id, Balance) VALUES (1, 10.00), (2, 20.00);
        UPDATE dbo.Account SET Balance = 15.00 WHERE Id = 1;
        """;

    private static readonly string[] Snapshot =
    [
        "SELECT Id, Name FROM dbo.Parent ORDER BY Id",
        "SELECT Id, ParentId, Note FROM dbo.Child ORDER BY Id",
        "SELECT Msg, At FROM dbo.Log ORDER BY Msg, At",
        "SELECT Id, Balance, ValidFrom, ValidTo FROM dbo.Account ORDER BY Id",
        "SELECT Id, Balance, ValidFrom, ValidTo FROM dbo.AccountHistory ORDER BY Id, ValidFrom",
    ];

    private readonly List<string> databases = [];
    private readonly DbSyncSettings settings = new() { BulkBatchSize = 2, CommandTimeoutSeconds = 60, ChangeRetentionDays = 2 };
    private string source = "";
    private string target = "";
    private WatermarkStore store = null!;

    private static string ConnectionString(string db) =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={db};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private string NewName()
    {
        var name = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
        databases.Add(name);
        return name;
    }

    private string CreateDatabase(params string[] batches)
    {
        var name = NewName();
        using (var m = new SqlConnection(Master))
        {
            m.Open();
            // LocalDB creates databases with AUTO_CLOSE on, which the hashdiff cheap verdict rightly distrusts.
            using var create = new SqlCommand($"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET AUTO_CLOSE OFF;", m);
            create.ExecuteNonQuery();
        }
        foreach (var batch in batches) Exec(name, batch);
        return name;
    }

    private static void Exec(string db, string sql)
    {
        using var c = new SqlConnection(ConnectionString(db));
        c.Open();
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string db, string sql)
    {
        using var c = new SqlConnection(ConnectionString(db));
        c.Open();
        using var cmd = new SqlCommand(sql, c);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string> Rows(string db)
    {
        using var c = new SqlConnection(ConnectionString(db));
        c.Open();
        var rows = new List<string>();
        foreach (var sql in Snapshot)
        {
            using var cmd = new SqlCommand(sql, c);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? "NULL" : Convert.ToString(rd.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            rows.Add("--");
        }
        return rows;
    }

    private DbSyncDatabase Database => new(source, DbSyncSettings.LocalDb, "test target", true) { TargetConnectionString = ConnectionString(target) };

    private DbSyncWatermark? Watermark => store.Get(source, Database.DescribeTarget());

    private DatabaseSyncResult Sync(Action? afterStartCaptured = null) =>
        new DatabaseSyncRunner(settings, store) { AfterStartCaptured = afterStartCaptured }.Run(Database);

    [SetUp]
    public void SetUp()
    {
        if (!LocalDb.Installed()) Assert.Ignore("LocalDB (SqlLocalDB.exe) is not installed on this machine.");
        var schedulerDb = new SchedulerDb(ConnectionString(NewName()), "test");
        schedulerDb.Migrate();
        store = new WatermarkStore(schedulerDb);
        source = CreateDatabase(Schema, Seed);
        target = CreateDatabase(Schema);
        // The write statistics and the hashdiff state's clock must not share a tick.
        Thread.Sleep(50);
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
    public void TheFirstRunCopiesEverythingAndCommitsTheStartWatermark()
    {
        var before = DateTime.UtcNow;

        var result = Sync();

        Assert.That(result.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), result.Message);
        Assert.That(Rows(target), Is.EqualTo(Rows(source)));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Parent").Method, Is.EqualTo("full"));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Child").Method, Is.EqualTo("full"));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Account").Method, Is.EqualTo("full"));
        var w = Watermark!;
        Assert.That(w.LastOutcome, Is.EqualTo("succeeded"));
        Assert.That(w.LastVersion, Is.EqualTo(result.StartVersion));
        Assert.That(w.LastStartUtc, Is.EqualTo(result.StartUtc!.Value).Within(TimeSpan.FromMilliseconds(1)));
        Assert.That(w.LastStartUtc, Is.GreaterThanOrEqualTo(before.AddSeconds(-1)));
    }

    [Test]
    public void TheSecondRunCopiesOnlyRowsChangedSinceTheFirstRunStarted()
    {
        Sync();
        Exec(source, "UPDATE dbo.Child SET Note = N'changed' WHERE Id = 2; INSERT dbo.Parent (Name) VALUES (N'four');");

        var result = Sync();

        Assert.That(result.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), result.Message);
        Assert.That(Rows(target), Is.EqualTo(Rows(source)));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Child"), Is.EqualTo(new DatabaseSyncTable("dbo.Child", "changetracking", 1, 0)));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Parent"), Is.EqualTo(new DatabaseSyncTable("dbo.Parent", "changetracking", 1, 0)));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Account").Method, Is.EqualTo("changetracking"));
    }

    [Test]
    public void ARowWrittenAfterTheStartVersionWasCapturedArrivesWithTheNextRun()
    {
        Sync();
        long captured = 0;

        var second = Sync(() =>
        {
            captured = Scalar(source, "SELECT CHANGE_TRACKING_CURRENT_VERSION()");
            Exec(source, "INSERT dbo.Child (Id, ParentId, Note) VALUES (4, 3, N'late');");
        });

        Assert.That(second.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), second.Message);
        Assert.That(second.StartVersion, Is.EqualTo(captured));
        Assert.That(Watermark!.LastVersion, Is.EqualTo(captured));

        var third = Sync();

        Assert.That(third.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), third.Message);
        Assert.That(Rows(target), Is.EqualTo(Rows(source)));
        Assert.That(Rows(target), Does.Contain("4|3|late"));
    }

    [Test]
    public void AnUntouchedDatabaseIsUnchangedAndWritesNothing()
    {
        var first = Sync();
        Assert.That(first.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), first.Message);
        // A target-only edit survives only if the run wrote nothing.
        Exec(target, "UPDATE dbo.Parent SET Name = N'target only' WHERE Id = 1;");

        var second = Sync();

        Assert.That(second.Outcome, Is.EqualTo(DatabaseSyncOutcome.Unchanged), second.Message);
        Assert.That(second.Tables, Is.Empty);
        Assert.That(Scalar(target, "SELECT COUNT(*) FROM dbo.Parent WHERE Name = N'target only'"), Is.EqualTo(1));
        Assert.That(Watermark!.LastVersion, Is.EqualTo(first.StartVersion));
    }

    [Test]
    public void AWatermarkBelowTheMinimumValidVersionForcesAFullCopy()
    {
        var first = Sync();
        Exec(source, "ALTER TABLE dbo.Parent DISABLE CHANGE_TRACKING;");
        Exec(source, "UPDATE dbo.Parent SET Name = N'renamed' WHERE Id = 3; UPDATE dbo.Child SET Note = N'x' WHERE Id = 1;");
        Exec(source, "ALTER TABLE dbo.Parent ENABLE CHANGE_TRACKING;");
        Assume.That(Scalar(source, "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(N'dbo.Parent'))"), Is.GreaterThan(first.StartVersion!.Value));
        Exec(target, "UPDATE dbo.Parent SET Name = N'target only' WHERE Id = 1;");

        var result = Sync();

        Assert.That(result.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), result.Message);
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Parent").Method, Is.EqualTo("full"));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Child").Method, Is.EqualTo("changetracking"));
        Assert.That(Rows(target), Is.EqualTo(Rows(source)));
    }

    [Test]
    public void AFailedRunDoesNotAdvanceTheWatermark()
    {
        var first = Sync();
        Exec(source, "UPDATE dbo.Child SET Note = N'after' WHERE Id = 3;");

        var failed = Sync(() => throw new InvalidOperationException("target went away"));

        Assert.That(failed.Outcome, Is.EqualTo(DatabaseSyncOutcome.Failed));
        Assert.That(failed.Message, Does.Contain("target went away"));
        var w = Watermark!;
        Assert.That(w.LastVersion, Is.EqualTo(first.StartVersion));
        Assert.That(w.LastStartUtc, Is.EqualTo(first.StartUtc!.Value).Within(TimeSpan.FromMilliseconds(1)));
        Assert.That(w.LastOutcome, Is.EqualTo("failed"));

        var retry = Sync();

        Assert.That(retry.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), retry.Message);
        Assert.That(Rows(target), Is.EqualTo(Rows(source)));
    }

    [Test]
    public void ASchemaMismatchFailsTheDatabaseWithNothingWritten()
    {
        Exec(target, "ALTER TABLE dbo.Log ADD Extra INT NULL;");

        var result = Sync();

        Assert.That(result.Outcome, Is.EqualTo(DatabaseSyncOutcome.Failed));
        Assert.That(result.Message, Does.Contain("dbo.Log.Extra"));
        Assert.That(Scalar(target, "SELECT (SELECT COUNT(*) FROM dbo.Parent) + (SELECT COUNT(*) FROM dbo.Child) + (SELECT COUNT(*) FROM dbo.Log)"), Is.EqualTo(0));
        Assert.That(Watermark, Is.Null);
    }
}
