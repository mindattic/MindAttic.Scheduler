using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>The temporal pair sync between two scratch LocalDB databases (dropped afterwards).</summary>
public sealed class TemporalPairSyncTests
{
    private const string Master = @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private const string DocDdl = """
        CREATE TABLE dbo.Doc (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Doc PRIMARY KEY, Title NVARCHAR(50) NOT NULL, Body NVARCHAR(MAX) NULL,
            ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL, ValidTo DATETIME2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.DocHistory));
        """;

    private const string EmbDdl = """
        CREATE TABLE dbo.Emb (Id INT NOT NULL CONSTRAINT PK_Emb PRIMARY KEY, V VECTOR(3) NULL,
            ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL, ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.EmbHistory));
        """;

    private const string TrackOn = "ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);";

    private const string DocRows = "SELECT Id, Title, Body, CONVERT(VARCHAR(30), ValidFrom, 121), CONVERT(VARCHAR(30), ValidTo, 121) FROM dbo.Doc ORDER BY Id";
    private const string DocHistoryRows = "SELECT Id, Title, Body, CONVERT(VARCHAR(30), ValidFrom, 121), CONVERT(VARCHAR(30), ValidTo, 121) FROM dbo.DocHistory ORDER BY Id, ValidFrom, ValidTo";

    private readonly List<string> databases = [];
    private readonly DbSyncSettings settings = new() { BulkBatchSize = 2, CommandTimeoutSeconds = 60 };

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

    private TemporalPairResult Sync(string source, string target, string table, long? lastVersion)
    {
        using var s = new SqlConnection(ConnectionString(source));
        using var t = new SqlConnection(ConnectionString(target));
        s.Open();
        t.Open();
        return TemporalPairSync.Apply(s, t, TableCatalog.Read(s), TableCatalog.Read(t), table, lastVersion, null, settings);
    }

    private static long Version(string db) => Scalar(db, "SELECT CHANGE_TRACKING_CURRENT_VERSION()");

    /// <summary>The target is system-versioned on <paramref name="history"/> and carries no suspension marker.</summary>
    private static void AssertVersioned(string target, string table, string history)
    {
        Assert.That(Scalar(target, $"SELECT temporal_type FROM sys.tables WHERE object_id = OBJECT_ID('{table}')"), Is.EqualTo(2));
        Assert.That(Scalar(target, $"SELECT CASE WHEN history_table_id = OBJECT_ID('{history}') THEN 1 ELSE 0 END FROM sys.tables WHERE object_id = OBJECT_ID('{table}')"), Is.EqualTo(1));
        Assert.That(Scalar(target, $"SELECT COUNT(*) FROM sys.extended_properties WHERE class = 1 AND name = N'{TemporalPairSync.Marker}'"), Is.EqualTo(0));
    }

    /// <summary>A tracked source with two versions of history and an empty target, fully synced once; returns the version after that sync.</summary>
    private (string Source, string Target, long Version) SyncedDocPair()
    {
        var source = CreateDatabase(TrackOn, DocDdl, "ALTER TABLE dbo.Doc ENABLE CHANGE_TRACKING;",
            "INSERT dbo.Doc (Title, Body) VALUES (N'one', N'a'), (N'two', NULL), (N'three', N'c'), (N'four', N'd');",
            "UPDATE dbo.Doc SET Body = N'b' WHERE Id = 2;");
        var target = CreateDatabase(DocDdl);
        var version = Version(source);
        Sync(source, target, "dbo.Doc", null);
        return (source, target, version);
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
    public void AFullSyncCopiesCurrentAndHistoryWithExactPeriods()
    {
        var (source, target, _) = SyncedDocPair();

        Assert.That(Rows(target, DocRows), Is.EqualTo(Rows(source, DocRows)));
        Assert.That(Rows(target, DocHistoryRows), Is.EqualTo(Rows(source, DocHistoryRows)));
        Assert.That(Rows(target, DocHistoryRows), Has.Count.EqualTo(1));
        AssertVersioned(target, "dbo.Doc", "dbo.DocHistory");
        Assert.That(Scalar(target, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Doc') AND is_hidden = 1"), Is.EqualTo(2));
    }

    [Test]
    public void UpdatesAndDeletesLeaveCurrentAndHistoryEqualAndTheTargetVersionedOnTheSameHistoryTable()
    {
        var (source, target, version) = SyncedDocPair();
        Run(source, """
            UPDATE dbo.Doc SET Title = N'ONE', Body = NULL WHERE Id = 1;
            DELETE dbo.Doc WHERE Id = 3;
            INSERT dbo.Doc (Title) VALUES (N'five');
            """);
        Run(source, "UPDATE dbo.Doc SET Body = N'bb' WHERE Id = 2;");

        var result = Sync(source, target, "dbo.Doc", version);

        Assert.That(Rows(target, DocRows), Is.EqualTo(Rows(source, DocRows)));
        Assert.That(Rows(target, DocHistoryRows), Is.EqualTo(Rows(source, DocHistoryRows)));
        Assert.That(Rows(target, DocHistoryRows), Has.Count.EqualTo(4));
        Assert.That(result.CurrentMethod, Is.EqualTo("changetracking"));
        Assert.That(result.CurrentDeleted, Is.EqualTo(1));
        AssertVersioned(target, "dbo.Doc", "dbo.DocHistory");

        // Versioning works again on the target: a change there lands in its own history table.
        Run(target, "UPDATE dbo.Doc SET Title = N'changed' WHERE Id = 4;");
        Assert.That(Scalar(target, "SELECT COUNT(*) FROM dbo.DocHistory WHERE Id = 4"), Is.EqualTo(1));
    }

    [Test]
    public void ACrashMidSuspendIsRepaired()
    {
        var (source, target, _) = SyncedDocPair();
        Run(target, $"""
            EXEC sys.sp_addextendedproperty @name = N'{TemporalPairSync.Marker}', @value = N'interrupted',
                @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'Doc';
            ALTER TABLE dbo.Doc SET (SYSTEM_VERSIONING = OFF);
            ALTER TABLE dbo.Doc DROP PERIOD FOR SYSTEM_TIME;
            """);
        Assert.That(Scalar(target, "SELECT temporal_type FROM sys.tables WHERE object_id = OBJECT_ID('dbo.Doc')"), Is.EqualTo(0));

        IReadOnlyList<string> repaired;
        using (var s = new SqlConnection(ConnectionString(source)))
        using (var t = new SqlConnection(ConnectionString(target)))
        {
            s.Open();
            t.Open();
            repaired = TemporalPairSync.Repair(t, TableCatalog.Read(s), settings);
        }

        Assert.That(repaired, Is.EqualTo(new[] { "dbo.Doc" }));
        AssertVersioned(target, "dbo.Doc", "dbo.DocHistory");
        Assert.That(Scalar(target, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Doc') AND is_hidden = 1"), Is.EqualTo(2));
        Assert.That(Rows(target, DocRows), Is.EqualTo(Rows(source, DocRows)));
    }

    [Test]
    public void TheNextSyncRepairsATargetLeftUnversioned()
    {
        var (source, target, version) = SyncedDocPair();
        Run(target, "ALTER TABLE dbo.Doc SET (SYSTEM_VERSIONING = OFF); ALTER TABLE dbo.Doc DROP PERIOD FOR SYSTEM_TIME;");
        Run(source, "UPDATE dbo.Doc SET Title = N'FOUR' WHERE Id = 4;");

        Sync(source, target, "dbo.Doc", version);

        Assert.That(Rows(target, DocRows), Is.EqualTo(Rows(source, DocRows)));
        Assert.That(Rows(target, DocHistoryRows), Is.EqualTo(Rows(source, DocHistoryRows)));
        AssertVersioned(target, "dbo.Doc", "dbo.DocHistory");
    }

    [Test]
    public void VectorsInATemporalPairAreCopiedExactly()
    {
        string source, target;
        try
        {
            source = CreateDatabase(TrackOn, EmbDdl, "ALTER TABLE dbo.Emb ENABLE CHANGE_TRACKING;");
            target = CreateDatabase(EmbDdl);
        }
        catch (SqlException ex) { Assert.Ignore($"This LocalDB does not support VECTOR in a temporal table: {ex.Message}"); return; }

        // 0.1 and 1/3 need 9 significant digits as float32; the server's own text form keeps 8.
        Run(source, "INSERT dbo.Emb (Id, V) VALUES (1, CAST('[0.1, 0.33333334, -1.00000012]' AS VECTOR(3))), (2, NULL), (3, CAST('[1e-7, 2, 3]' AS VECTOR(3)));");
        Run(source, "UPDATE dbo.Emb SET V = CAST('[0.2, 0.66666669, 1.00000012]' AS VECTOR(3)) WHERE Id = 1;");
        var version = Version(source);
        Sync(source, target, "dbo.Emb", null);

        Run(source, "UPDATE dbo.Emb SET V = CAST('[0.3, 0.1, 0.7]' AS VECTOR(3)) WHERE Id = 2; DELETE dbo.Emb WHERE Id = 3;");
        // A target history row that differs from the source only past the 8th significant digit is found and fixed.
        Run(target, """
            ALTER TABLE dbo.Emb SET (SYSTEM_VERSIONING = OFF);
            UPDATE dbo.EmbHistory SET V = CAST('[0.1, 0.33333331, -1.00000012]' AS VECTOR(3)) WHERE Id = 1;
            ALTER TABLE dbo.Emb SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.EmbHistory));
            """);

        Sync(source, target, "dbo.Emb", version);

        // Each element exactly: the dot product with a unit vector, printed with 17 digits.
        string Exact(string table) => "SELECT Id, " + string.Join(", ", new[] { "[1,0,0]", "[0,1,0]", "[0,0,1]" }
                .Select(u => $"CONVERT(VARCHAR(30), -VECTOR_DISTANCE('dot', V, CAST('{u}' AS VECTOR(3))), 3)")) +
            $", CONVERT(VARCHAR(30), ValidFrom, 121), CONVERT(VARCHAR(30), ValidTo, 121) FROM {table} ORDER BY Id, ValidFrom";
        Assert.That(Rows(target, Exact("dbo.Emb")), Is.EqualTo(Rows(source, Exact("dbo.Emb"))));
        Assert.That(Rows(target, Exact("dbo.EmbHistory")), Is.EqualTo(Rows(source, Exact("dbo.EmbHistory"))));
        Assert.That(Rows(target, Exact("dbo.EmbHistory")), Has.Count.EqualTo(3));
        AssertVersioned(target, "dbo.Emb", "dbo.EmbHistory");
    }
}
