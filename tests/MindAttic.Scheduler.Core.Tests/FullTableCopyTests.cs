using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>DbSync full table copy and target constraint guard against scratch LocalDB databases (dropped afterwards).</summary>
public sealed class FullTableCopyTests
{
    private const string Ddl = """
        CREATE TABLE dbo.Parent (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Parent PRIMARY KEY, Name NVARCHAR(50) NULL);
        CREATE TABLE dbo.Child (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Child PRIMARY KEY,
            ParentId INT NOT NULL CONSTRAINT FK_Child_Parent REFERENCES dbo.Parent (Id), Note NVARCHAR(50) NULL);
        CREATE TABLE dbo.Audit (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, What NVARCHAR(50) NOT NULL);
        """;

    private const string Trigger = "CREATE TRIGGER dbo.TR_Child_Audit ON dbo.Child AFTER INSERT AS INSERT dbo.Audit (What) SELECT N'child' FROM inserted;";

    private readonly List<string> databases = [];
    private readonly DbSyncSettings settings = new() { BulkBatchSize = 2 };

    private static bool LocalDbInstalled()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
        return Directory.Exists(root) &&
            Directory.EnumerateFiles(root, "SqlLocalDB.exe", SearchOption.AllDirectories).Any();
    }

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

    private static object? Scalar(SqlConnection c, string sql)
    {
        using var cmd = new SqlCommand(sql, c);
        return cmd.ExecuteScalar();
    }

    private string CreateDatabase()
    {
        var name = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
        databases.Add(name);
        using (var m = Open("master")) Execute(m, $"CREATE DATABASE [{name}]");
        using var c = Open(name);
        Execute(c, Ddl);
        Execute(c, Trigger);
        return name;
    }

    /// <summary>A source with gaps in both identities (rows 2 and 5 deleted) and a target holding stale rows.</summary>
    private (string Source, string Target) CreatePair()
    {
        var source = CreateDatabase();
        using (var s = Open(source))
        {
            Execute(s, "INSERT dbo.Parent (Name) VALUES (N'a'), (N'b'), (N'c'), (N'd'), (N'e'); DELETE dbo.Parent WHERE Id IN (2, 5);");
            Execute(s, "INSERT dbo.Child (ParentId, Note) VALUES (1, N'x'), (3, N'y'), (4, N'z'), (4, N'w'); DELETE dbo.Child WHERE Id = 1;");
        }
        var target = CreateDatabase();
        using (var t = Open(target))
        {
            Execute(t, "INSERT dbo.Parent (Name) VALUES (N'stale');");
            Execute(t, "INSERT dbo.Child (ParentId, Note) VALUES (1, N'stale');");
            Execute(t, "DELETE dbo.Audit;");
        }
        return (source, target);
    }

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
                using var c = Open("master");
                Execute(c, $"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
            }
            catch (SqlException) { /* best effort */ }
        }
        databases.Clear();
    }

    [Test]
    public void CopiesAParentChildPairWithIdentityPreservedAndRestoresConstraints()
    {
        var (sourceDb, targetDb) = CreatePair();
        using var source = Open(sourceDb);
        using var target = Open(targetDb);
        var catalog = TableCatalog.Read(source);

        FullCopyResult child, parent;
        using (TargetConstraintGuard.Engage(target, ["dbo.Parent", "dbo.Child"], settings))
        {
            Assert.That(Scalar(target, "SELECT is_disabled FROM sys.foreign_keys WHERE name = N'FK_Child_Parent'"), Is.True);
            Assert.That(Scalar(target, "SELECT is_disabled FROM sys.triggers WHERE name = N'TR_Child_Audit'"), Is.True);

            // Child first: with the guard on, the order cannot trip the foreign key.
            child = FullTableCopy.Copy(source, target, catalog.Tables["dbo.Child"], settings);
            parent = FullTableCopy.Copy(source, target, catalog.Tables["dbo.Parent"], settings);
        }

        Assert.That(child.Truncated, Is.True, "nothing references Child");
        Assert.That(child.RowsCopied, Is.EqualTo(3));
        Assert.That(parent.Truncated, Is.False, "Child references Parent, so it is deleted, not truncated");
        Assert.That(parent.RowsDeleted, Is.EqualTo(1));
        Assert.That(parent.RowsCopied, Is.EqualTo(3));
        Assert.That(parent.IdentityReseededTo, Is.EqualTo("5"));
        Assert.That(child.IdentityReseededTo, Is.EqualTo("4"));

        using (var cmd = new SqlCommand("SELECT STRING_AGG(CONCAT(Id, ':', Name), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.Parent", target))
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("1:a,3:c,4:d"));
        using (var cmd = new SqlCommand("SELECT STRING_AGG(CONCAT(Id, ':', ParentId, ':', Note), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.Child", target))
            Assert.That(cmd.ExecuteScalar(), Is.EqualTo("2:3:y,3:4:z,4:4:w"));
        Assert.That(Scalar(target, "SELECT IDENT_CURRENT('dbo.Parent')"), Is.EqualTo(5m));
        Assert.That(Scalar(target, "SELECT IDENT_CURRENT('dbo.Child')"), Is.EqualTo(4m));

        Assert.That(Scalar(target, "SELECT CONCAT(is_disabled, is_not_trusted) FROM sys.foreign_keys WHERE name = N'FK_Child_Parent'"), Is.EqualTo("00"), "enabled and trusted");
        Assert.That(Scalar(target, "SELECT is_disabled FROM sys.triggers WHERE name = N'TR_Child_Audit'"), Is.False);
        Assert.That(Scalar(target, "SELECT COUNT(*) FROM dbo.Audit"), Is.EqualTo(0), "the trigger did not fire on copied rows");
        Assert.That(Scalar(target, $"SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'{TargetConstraintGuard.Marker}'"), Is.EqualTo(0));
    }

    [Test]
    public void AGuardLeftBehindByACrashIsRepairedByTheNextGuard()
    {
        var (_, targetDb) = CreatePair();
        using (var target = Open(targetDb))
        {
            // A crash: the guard is never released and its connection is gone.
            var crashed = TargetConstraintGuard.Engage(target, ["dbo.Child"], settings);
            Assert.That(crashed.MarkedTables, Is.EquivalentTo(new[] { "dbo.Child" }));
        }

        using (var target = Open(targetDb))
        {
            Assert.That(Scalar(target, "SELECT is_disabled FROM sys.foreign_keys WHERE name = N'FK_Child_Parent'"), Is.True);
            Assert.That(Scalar(target, $"SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'{TargetConstraintGuard.Marker}'"), Is.EqualTo(1));

            using (TargetConstraintGuard.Engage(target, ["dbo.Parent"], settings))
            {
                // The repair restored the trigger before this guard (which does not cover Child) started.
                Assert.That(Scalar(target, "SELECT is_disabled FROM sys.triggers WHERE name = N'TR_Child_Audit'"), Is.False);
            }

            Assert.That(Scalar(target, "SELECT CONCAT(is_disabled, is_not_trusted) FROM sys.foreign_keys WHERE name = N'FK_Child_Parent'"), Is.EqualTo("00"));
            Assert.That(Scalar(target, "SELECT is_disabled FROM sys.triggers WHERE name = N'TR_Child_Audit'"), Is.False);
            Assert.That(Scalar(target, $"SELECT COUNT(*) FROM sys.extended_properties WHERE name = N'{TargetConstraintGuard.Marker}'"), Is.EqualTo(0));
        }
    }
}
