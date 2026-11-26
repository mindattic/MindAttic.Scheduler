using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>The DbSync table catalog against scratch LocalDB databases (dropped afterwards).</summary>
public sealed class TableCatalogTests
{
    private const string Master = @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private readonly List<string> databases = [];

    private static bool LocalDbInstalled()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
        return Directory.Exists(root) &&
            Directory.EnumerateFiles(root, "SqlLocalDB.exe", SearchOption.AllDirectories).Any();
    }

    private static string ConnectionString(string db) =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={db};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private string CreateDatabase(string ddl)
    {
        var name = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
        databases.Add(name);
        using (var m = new SqlConnection(Master))
        {
            m.Open();
            using var create = new SqlCommand($"CREATE DATABASE [{name}]", m);
            create.ExecuteNonQuery();
        }
        using var c = new SqlConnection(ConnectionString(name));
        c.Open();
        using var cmd = new SqlCommand(ddl, c);
        cmd.ExecuteNonQuery();
        return name;
    }

    private static TableCatalog ReadCatalog(string db)
    {
        using var c = new SqlConnection(ConnectionString(db));
        c.Open();
        return TableCatalog.Read(c);
    }

    private const string BaseDdl = """
        CREATE TABLE dbo.Parent (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Parent PRIMARY KEY, Name NVARCHAR(50) NOT NULL,
            Price DECIMAL(18,2) NULL, Stamp ROWVERSION, Doubled AS (Id * 2));
        CREATE TABLE dbo.Child (ParentId INT NOT NULL CONSTRAINT FK_Child_Parent REFERENCES dbo.Parent (Id), Seq INT NOT NULL,
            CONSTRAINT PK_Child PRIMARY KEY (ParentId, Seq));
        CREATE TABLE dbo.Versioned (Id INT NOT NULL CONSTRAINT PK_Versioned PRIMARY KEY, Value NVARCHAR(20) NULL,
            ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL, ValidTo DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
            WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.VersionedHistory));
        """;

    private const string TriggerDdl = "CREATE TRIGGER dbo.Child_Touch ON dbo.Child AFTER INSERT AS SET NOCOUNT ON;";

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
    public void ReadsIdentityTemporalKeysConstraintsAndTriggers()
    {
        var db = CreateDatabase(BaseDdl);
        using (var c = new SqlConnection(ConnectionString(db)))
        {
            c.Open();
            using var cmd = new SqlCommand(TriggerDdl, c);
            cmd.ExecuteNonQuery();
        }

        var catalog = ReadCatalog(db);

        Assert.That(catalog.Tables.Keys, Is.EquivalentTo(new[] { "dbo.Parent", "dbo.Child", "dbo.Versioned", "dbo.VersionedHistory" }));

        var parent = catalog.Tables["dbo.Parent"];
        Assert.That(parent.IdentityColumn?.Name, Is.EqualTo("Id"));
        Assert.That(parent.PrimaryKey, Is.EqualTo(new[] { "Id" }));
        Assert.That(parent.CopyColumns.Select(c => c.Name), Is.EqualTo(new[] { "Id", "Name", "Price" }));
        Assert.That(parent.Column("Name")!.TypeDecl, Is.EqualTo("nvarchar(50)"));
        Assert.That(parent.Column("Price")!.TypeDecl, Is.EqualTo("decimal(18,2)"));
        Assert.That(parent.ReferencedByForeignKey, Is.True);

        var child = catalog.Tables["dbo.Child"];
        Assert.That(child.PrimaryKey, Is.EqualTo(new[] { "ParentId", "Seq" }));
        Assert.That(child.IdentityColumn, Is.Null);
        Assert.That(child.Triggers.Select(t => t.Name), Is.EqualTo(new[] { "Child_Touch" }));

        var fk = catalog.ForeignKeysTouching("dbo.Parent").Single();
        Assert.That(fk.Name, Is.EqualTo("FK_Child_Parent"));
        Assert.That(fk.TableKey, Is.EqualTo("dbo.Child"));
        Assert.That(fk.ReferencedTableKey, Is.EqualTo("dbo.Parent"));
        Assert.That(fk.Definition, Is.EqualTo("ParentId>Id"));

        var pair = catalog.TemporalPairs.Single();
        Assert.That(pair, Is.EqualTo(new TemporalPair("dbo.Versioned", "dbo.VersionedHistory", "ValidFrom", "ValidTo")));
        Assert.That(catalog.Tables["dbo.VersionedHistory"].TemporalType, Is.EqualTo(1));
        Assert.That(catalog.Tables["dbo.Versioned"].Column("ValidTo")!.TypeDecl, Is.EqualTo("datetime2(7)"));
    }

    [Test]
    public void IdenticalSchemasMatch()
    {
        Assert.That(TableCatalog.Compare(ReadCatalog(CreateDatabase(BaseDdl)), ReadCatalog(CreateDatabase(BaseDdl))), Is.Null);
    }

    [Test]
    public void ReadsAVectorColumn()
    {
        string db;
        try { db = CreateDatabase("CREATE TABLE dbo.Embedding (Id INT NOT NULL PRIMARY KEY, V VECTOR(3) NULL);"); }
        catch (SqlException ex) { Assert.Ignore($"This LocalDB does not support VECTOR: {ex.Message}"); return; }

        var table = ReadCatalog(db).Tables["dbo.Embedding"];

        var v = table.VectorColumns.Single();
        Assert.That(v.Name, Is.EqualTo("V"));
        Assert.That(v.VectorDimensions, Is.EqualTo(3));
        Assert.That(v.TypeDecl, Is.EqualTo("vector(3)"));
    }

    [Test]
    public void AnExtraColumnOnTheTargetIsAMismatch()
    {
        var source = ReadCatalog(CreateDatabase(BaseDdl));
        var target = ReadCatalog(CreateDatabase(BaseDdl + "ALTER TABLE dbo.Child ADD Extra INT NULL;"));

        var mismatch = TableCatalog.Compare(source, target, "local", "Azure");

        Assert.That(mismatch, Is.Not.Null);
        Assert.That(mismatch!.Differences, Is.EqualTo(new[] { "column dbo.Child.Extra exists in Azure but not in local" }));
    }

    [Test]
    public void ATypeChangeAndAMissingTableAreMismatches()
    {
        var source = ReadCatalog(CreateDatabase(BaseDdl));
        var target = ReadCatalog(CreateDatabase(BaseDdl + "DROP TABLE dbo.Child; ALTER TABLE dbo.Parent ALTER COLUMN Name NVARCHAR(80) NOT NULL;"));

        var mismatch = TableCatalog.Compare(source, target);

        Assert.That(mismatch!.Differences, Is.EquivalentTo(new[]
        {
            "table dbo.Child exists in source but not in target",
            "column dbo.Parent.Name: MaxLength is '100' in source but '160' in target",
        }));
    }
}
