using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>DbSync Change Tracking setup against scratch LocalDB databases (dropped afterwards).</summary>
public sealed class ChangeTrackingSetupTests
{
    private const string Master = @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private const string Ddl = """
        CREATE TABLE dbo.Keyed (Id INT NOT NULL CONSTRAINT PK_Keyed PRIMARY KEY, Name NVARCHAR(50) NULL);
        CREATE TABLE dbo.Heap (Id INT NOT NULL, Name NVARCHAR(50) NULL);
        CREATE TABLE dbo.Versioned (Id INT NOT NULL CONSTRAINT PK_Versioned PRIMARY KEY, Value NVARCHAR(20) NULL,
            ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL, ValidTo DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
            WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.VersionedHistory));
        """;

    private readonly List<string> databases = [];

    private static SqlConnection Open(string db)
    {
        var c = new SqlConnection($@"Server=(localdb)\MSSQLLocalDB;Database={db};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30");
        c.Open();
        return c;
    }

    private static void Execute(SqlConnection c, string sql)
    {
        using var cmd = new SqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private string CreateDatabase()
    {
        var name = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
        databases.Add(name);
        using (var m = Open("master")) Execute(m, $"CREATE DATABASE [{name}]");
        using var c = Open(name);
        Execute(c, Ddl);
        return name;
    }

    private static ChangeTrackingReport Ensure(string db, int retentionDays = 14, bool dryRun = false)
    {
        using var c = Open(db);
        return ChangeTrackingSetup.Ensure(c, new DbSyncSettings { ChangeRetentionDays = retentionDays }, dryRun);
    }

    private static (int Period, int Units)? DatabaseRetention(string db)
    {
        using var c = Open(db);
        using var cmd = new SqlCommand("SELECT retention_period, retention_period_units FROM sys.change_tracking_databases WHERE database_id = DB_ID()", c);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (Convert.ToInt32(rd[0]), Convert.ToInt32(rd[1])) : null;
    }

    private static string[] TrackedTables(string db)
    {
        using var c = Open(db);
        using var cmd = new SqlCommand("SELECT SCHEMA_NAME(t.schema_id) + '.' + t.name FROM sys.change_tracking_tables ctt JOIN sys.tables t ON t.object_id = ctt.object_id", c);
        using var rd = cmd.ExecuteReader();
        var names = new List<string>();
        while (rd.Read()) names.Add(rd.GetString(0));
        return [.. names];
    }

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
                using var c = Open("master");
                Execute(c, $"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
            }
            catch (SqlException) { /* best effort */ }
        }
        databases.Clear();
    }

    [Test]
    public void EnablesTheDatabaseAndKeyedTablesAndClassifiesTheRest()
    {
        var db = CreateDatabase();

        var report = Ensure(db, retentionDays: 5);

        Assert.That(report.DatabaseWasEnabled, Is.False);
        Assert.That(report.RetentionMinutesAfter, Is.EqualTo(5 * 24 * 60));
        Assert.That(DatabaseRetention(db), Is.EqualTo(((int, int)?)(5, 3)));
        Assert.That(TrackedTables(db), Is.EquivalentTo(new[] { "dbo.Keyed", "dbo.Versioned" }));

        Assert.That(report.Tracked.Select(t => t.Key), Is.EquivalentTo(new[] { "dbo.Keyed", "dbo.Versioned" }));
        Assert.That(report.Tracked.All(t => t.MinValidVersion is not null), Is.True);
        Assert.That(report.NewlyEnabled.Select(t => t.Key), Is.EquivalentTo(new[] { "dbo.Keyed", "dbo.Versioned" }));

        var fallback = report.Fallback.ToDictionary(t => t.Key, t => t.FallbackReason);
        Assert.That(fallback, Is.EquivalentTo(new Dictionary<string, string?>
        {
            ["dbo.Heap"] = "no-pk",
            ["dbo.VersionedHistory"] = "temporal-history",
        }));
    }

    [Test]
    public void ASecondCallChangesNothing()
    {
        var db = CreateDatabase();
        Ensure(db);

        var again = Ensure(db);

        Assert.That(again.Unchanged, Is.True);
        Assert.That(again.DatabaseWasEnabled, Is.True);
        Assert.That(again.NewlyEnabled, Is.Empty);
        Assert.That(again.Tracked.Select(t => t.Key), Is.EquivalentTo(new[] { "dbo.Keyed", "dbo.Versioned" }));
    }

    [Test]
    public void RetentionIsRaisedButNeverLowered()
    {
        var db = CreateDatabase();
        Ensure(db, retentionDays: 3);

        var raised = Ensure(db, retentionDays: 10);
        Assert.That(raised.Statements, Has.Count.EqualTo(1));
        Assert.That(DatabaseRetention(db), Is.EqualTo(((int, int)?)(10, 3)));

        var lower = Ensure(db, retentionDays: 2);
        Assert.That(lower.Unchanged, Is.True);
        Assert.That(lower.RetentionMinutesAfter, Is.EqualTo(10 * 24 * 60));
        Assert.That(DatabaseRetention(db), Is.EqualTo(((int, int)?)(10, 3)));
    }

    [Test]
    public void ADryRunReportsButRunsNoDdl()
    {
        var db = CreateDatabase();

        var report = Ensure(db, dryRun: true);

        Assert.That(report.DryRun, Is.True);
        Assert.That(report.Statements, Has.Count.EqualTo(3));
        Assert.That(report.NewlyEnabled.Select(t => t.Key), Is.EquivalentTo(new[] { "dbo.Keyed", "dbo.Versioned" }));
        Assert.That(DatabaseRetention(db), Is.Null);
        Assert.That(TrackedTables(db), Is.Empty);
    }
}
