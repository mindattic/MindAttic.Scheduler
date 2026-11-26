using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>A missing or table-less target gets its schema from the source (M.SCHD-T72), between scratch LocalDB databases (all dropped afterwards).</summary>
public sealed class SchemaBootstrapTests
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
    private readonly DbSyncSettings settings = new() { BulkBatchSize = 2, CommandTimeoutSeconds = 60, ChangeRetentionDays = 2, SchemaBootstrapTimeoutSeconds = 300 };
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

    private static bool Exists(string db)
    {
        using var c = new SqlConnection(Master);
        c.Open();
        using var cmd = new SqlCommand("SELECT DB_ID(@n)", c);
        cmd.Parameters.AddWithValue("@n", db);
        return cmd.ExecuteScalar() is not DBNull;
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

    private DatabaseSyncResult Sync(bool dryRun = false) => new DatabaseSyncRunner(settings, store).Run(Database, dryRun: dryRun);

    [SetUp]
    public void SetUp()
    {
        if (!LocalDb.Installed()) Assert.Ignore("LocalDB (SqlLocalDB.exe) is not installed on this machine.");
        if (!File.Exists(settings.SqlPackagePath)) Assert.Ignore($"SqlPackage is not installed at {settings.SqlPackagePath}.");
        var schedulerDb = new SchedulerDb(ConnectionString(NewName()), "test");
        schedulerDb.Migrate();
        store = new WatermarkStore(schedulerDb);
        source = CreateDatabase(Schema, Seed);
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
    public void ProbeTellsMissingEmptyAndPopulatedTargetsApart()
    {
        Assert.That(SchemaBootstrap.Probe(ConnectionString(NewName()), settings), Is.EqualTo(TargetSchemaState.Missing));
        Assert.That(SchemaBootstrap.Probe(ConnectionString(CreateDatabase()), settings), Is.EqualTo(TargetSchemaState.Empty));
        Assert.That(SchemaBootstrap.Probe(ConnectionString(source), settings), Is.EqualTo(TargetSchemaState.HasTables));
    }

    [Test]
    public void AMissingTargetIsCreatedFromTheSourceSchemaThenCopiedInFull()
    {
        target = NewName();

        var result = Sync();

        Assert.That(result.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), result.Message);
        Assert.That(result.Bootstrapped, Is.True);
        Assert.That(Rows(target), Is.EqualTo(Rows(source)));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Parent").Method, Is.EqualTo("full"));
        Assert.That(result.Tables.Single(t => t.Table == "dbo.Account").Method, Is.EqualTo("full"));
    }

    [Test]
    public void AnEmptyTargetGetsTheSourceSchemaThenTheNextRunIsIncremental()
    {
        target = CreateDatabase();

        var first = Sync();
        Exec(source, "UPDATE dbo.Child SET Note = N'changed' WHERE Id = 2;");
        var second = Sync();

        Assert.That(first.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), first.Message);
        Assert.That(first.Bootstrapped, Is.True);
        Assert.That(second.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), second.Message);
        Assert.That(second.Bootstrapped, Is.False);
        Assert.That(second.Tables.Single(t => t.Table == "dbo.Child"), Is.EqualTo(new DatabaseSyncTable("dbo.Child", "changetracking", 1, 0)));
        Assert.That(Rows(target), Is.EqualTo(Rows(source)));
    }

    [Test]
    public void ADryRunOnlyReportsTheBootstrap()
    {
        target = NewName();

        var result = Sync(dryRun: true);

        Assert.That(result.Outcome, Is.EqualTo(DatabaseSyncOutcome.Succeeded), result.Message);
        Assert.That(result.DryRun, Is.True);
        Assert.That(result.Bootstrapped, Is.True);
        Assert.That(result.Message, Does.Contain("would bootstrap"));
        Assert.That(Exists(target), Is.False);
        Assert.That(store.Get(source, Database.DescribeTarget()), Is.Null);
    }

    [Test]
    public void ATargetWithOtherTablesIsNeverAltered()
    {
        target = CreateDatabase("CREATE TABLE dbo.Other (Id INT NOT NULL PRIMARY KEY);");

        var result = Sync();

        Assert.That(result.Outcome, Is.EqualTo(DatabaseSyncOutcome.Failed));
        Assert.That(result.SchemaMismatch, Is.True);
        Assert.That(result.Bootstrapped, Is.False);
        using var c = new SqlConnection(ConnectionString(target));
        c.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0", c);
        Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1));
    }

    [Test]
    public void TheBootstrapSettingsLoadFromTheFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dbsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "dbsync.json");
            File.WriteAllText(path, """
                { "sqlPackagePath": "%TEMP%\\sqlpackage.exe", "schemaBootstrapTimeoutSeconds": 60,
                  "schemaPublishProperties": ["/p:DropObjectsNotInSource=False"] }
                """);

            var loaded = DbSyncSettings.Load(path, Path.Combine(dir, "none.json"));

            Assert.That(loaded.SqlPackagePath, Is.EqualTo(Environment.ExpandEnvironmentVariables(@"%TEMP%\sqlpackage.exe")));
            Assert.That(loaded.SchemaBootstrapTimeoutSeconds, Is.EqualTo(60));
            Assert.That(loaded.SchemaPublishProperties, Is.EqualTo(new[] { "/p:DropObjectsNotInSource=False" }));
            Assert.That(loaded.SchemaExtractProperties, Is.EqualTo(DbSyncSettings.DefaultSchemaExtractProperties));

            File.WriteAllText(path, """{ "schemaExtractProperties": ["ExtractAllTableData=True"] }""");
            Assert.That(() => DbSyncSettings.Load(path, Path.Combine(dir, "none.json")), Throws.TypeOf<InvalidDataException>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
