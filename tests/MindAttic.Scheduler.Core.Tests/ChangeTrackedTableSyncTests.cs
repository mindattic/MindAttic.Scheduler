using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>The incremental change-tracked table sync between two scratch LocalDB databases (dropped afterwards).</summary>
public sealed class ChangeTrackedTableSyncTests
{
    private const string Master = @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private const string ItemDdl = """
        CREATE TABLE dbo.Item (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Item PRIMARY KEY, Name NVARCHAR(50) NOT NULL,
            Price DECIMAL(18,2) NULL, Stamp ROWVERSION, Doubled AS (Id * 2));
        """;

    private const string Seed = """
        SET IDENTITY_INSERT dbo.Item ON;
        INSERT dbo.Item (Id, Name, Price) VALUES (1, N'one', 1.10), (2, N'two', NULL), (3, N'three', 3.30), (4, N'four', 4.40);
        SET IDENTITY_INSERT dbo.Item OFF;
        """;

    private const string TrackOn = """
        ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
        """;

    private readonly List<string> databases = [];
    private readonly DbSyncSettings settings = new() { BulkBatchSize = 2, CommandTimeoutSeconds = 60 };

    private static bool LocalDbInstalled()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
        return Directory.Exists(root) &&
            Directory.EnumerateFiles(root, "SqlLocalDB.exe", SearchOption.AllDirectories).Any();
    }

    private static string ConnectionString(string db) =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={db};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private string CreateDatabase(params string[] batches)
    {
        var name = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
        databases.Add(name);
        using (var m = new SqlConnection(Master))
        {
            m.Open();
            using var create = new SqlCommand($"CREATE DATABASE [{name}]", m);
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

    private static long Scalar(string db, string sql)
    {
        using var c = new SqlConnection(ConnectionString(db));
        c.Open();
        using var cmd = new SqlCommand(sql, c);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

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

    private TableSyncResult Sync(string source, string target, string table, long lastVersion)
    {
        using var s = new SqlConnection(ConnectionString(source));
        using var t = new SqlConnection(ConnectionString(target));
        s.Open();
        t.Open();
        return ChangeTrackedTableSync.Apply(s, t, TableCatalog.Read(s).Tables[table], lastVersion, settings);
    }

    /// <summary>A tracked source and an untracked target holding the same seeded rows, with the source's version before any change.</summary>
    private (string Source, string Target, long Version) SeededPair()
    {
        var source = CreateDatabase(TrackOn, ItemDdl, "ALTER TABLE dbo.Item ENABLE CHANGE_TRACKING;", Seed);
        var target = CreateDatabase(ItemDdl, Seed);
        return (source, target, Scalar(source, "SELECT CHANGE_TRACKING_CURRENT_VERSION()"));
    }

    private const string ItemRows = "SELECT Id, Name, Price FROM dbo.Item ORDER BY Id";

    [SetUp]
    public void SetUp()
    {
        if (!LocalDbInstalled()) Assert.Ignore("LocalDB (SqlLocalDB.exe) is not installed on this machine.");
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
    public void InsertsUpdatesAndDeletesLeaveTheTargetEqualToTheSource()
    {
        var (source, target, version) = SeededPair();
        Run(source, """
            DELETE dbo.Item WHERE Id = 3;
            INSERT dbo.Item (Name, Price) VALUES (N'five', 5.50), (N'six', NULL), (N'seven', 7.70);
            UPDATE dbo.Item SET Name = N'TWO', Price = 2.20 WHERE Id = 2;
            UPDATE dbo.Item SET Price = NULL WHERE Id = 1;
            """);

        var result = Sync(source, target, "dbo.Item", version);

        Assert.That(Rows(target, ItemRows), Is.EqualTo(Rows(source, ItemRows)));
        Assert.That(result.Deleted, Is.EqualTo(1));
        Assert.That(result.Upserted, Is.EqualTo(5));
    }

    [Test]
    public void IdentityValuesArePreserved()
    {
        var (source, target, version) = SeededPair();
        Run(source, "DELETE dbo.Item WHERE Id IN (3, 4); INSERT dbo.Item (Name) VALUES (N'five');");

        Sync(source, target, "dbo.Item", version);

        Assert.That(Rows(target, "SELECT Id FROM dbo.Item ORDER BY Id"), Is.EqualTo(new[] { "1", "2", "5" }));
    }

    [Test]
    public void ReapplyingTheSameChangeSetIsANoOp()
    {
        var (source, target, version) = SeededPair();
        Run(source, "DELETE dbo.Item WHERE Id = 4; INSERT dbo.Item (Name, Price) VALUES (N'five', 5.50); UPDATE dbo.Item SET Name = N'ONE' WHERE Id = 1;");

        Sync(source, target, "dbo.Item", version);
        var once = Rows(target, ItemRows);
        var again = Sync(source, target, "dbo.Item", version);

        Assert.That(Rows(target, ItemRows), Is.EqualTo(once));
        Assert.That(Rows(target, ItemRows), Is.EqualTo(Rows(source, ItemRows)));
        Assert.That(again.Deleted, Is.EqualTo(0));
    }

    [Test]
    public void NoChangesSinceTheVersionWritesNothing()
    {
        var (source, target, version) = SeededPair();

        var result = Sync(source, target, "dbo.Item", version);

        Assert.That(result, Is.EqualTo(new TableSyncResult("dbo.Item", 0, 0)));
        Assert.That(Rows(target, ItemRows), Is.EqualTo(Rows(source, ItemRows)));
    }

    [Test]
    public void AnUntrackedTableIsRefused()
    {
        var source = CreateDatabase(TrackOn, ItemDdl, Seed);
        var target = CreateDatabase(ItemDdl);

        Assert.That(() => Sync(source, target, "dbo.Item", 0),
            Throws.InvalidOperationException.With.Message.Contains("not enabled on dbo.Item"));
    }

    [Test]
    public void VectorsAreCopiedExactly()
    {
        const string ddl = "CREATE TABLE dbo.Embedding (Id INT NOT NULL PRIMARY KEY, V VECTOR(3) NULL);";
        string source, target;
        try
        {
            source = CreateDatabase(TrackOn, ddl, "ALTER TABLE dbo.Embedding ENABLE CHANGE_TRACKING;");
            target = CreateDatabase(ddl);
        }
        catch (SqlException ex) { Assert.Ignore($"This LocalDB does not support VECTOR: {ex.Message}"); return; }

        var version = Scalar(source, "SELECT CHANGE_TRACKING_CURRENT_VERSION()");
        // 0.1 and 1/3 need 9 significant digits as float32; the server's own text form keeps 8.
        Run(source, "INSERT dbo.Embedding (Id, V) VALUES (1, CAST('[0.1, 0.33333334, -1.00000012]' AS VECTOR(3))), (2, NULL);");

        Sync(source, target, "dbo.Embedding", version);

        // Each element exactly: the dot product with a unit vector, printed with 17 digits.
        var exact = "SELECT Id, " + string.Join(", ", new[] { "[1,0,0]", "[0,1,0]", "[0,0,1]" }
            .Select(u => $"CONVERT(VARCHAR(30), -VECTOR_DISTANCE('dot', V, CAST('{u}' AS VECTOR(3))), 3)")) + " FROM dbo.Embedding ORDER BY Id";
        Assert.That(Rows(target, exact), Is.EqualTo(Rows(source, exact)));
        Assert.That(Rows(target, exact), Has.Count.EqualTo(2));
    }
}
