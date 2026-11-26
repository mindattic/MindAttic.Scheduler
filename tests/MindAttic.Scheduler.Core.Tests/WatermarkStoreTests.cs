using MindAttic.Scheduler.Core;
using MindAttic.Scheduler.Core.DbSync;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

/// <summary>The DbSync watermark store against a scratch LocalDB database per test (dropped afterwards).</summary>
public sealed class WatermarkStoreTests
{
    private string dbName = "";

    private string ConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={dbName};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private SchedulerDb Db() => new(ConnectionString, "test");

    [SetUp]
    public void SetUp()
    {
        if (!LocalDb.Installed()) Assert.Ignore("LocalDB (SqlLocalDB.exe) is not installed on this machine.");
        dbName = $"MindAttic.Scheduler.Tests.{Guid.NewGuid():N}";
    }

    [TearDown]
    public void TearDown()
    {
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

    private static DateTime Utc(int minutesAgo)
    {
        var t = DateTime.UtcNow.AddMinutes(-minutesAgo);
        return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    [Test]
    public void FirstGetIsNull()
    {
        var db = Db();
        db.Migrate();

        Assert.That(new WatermarkStore(db).Get("Prose", "azure"), Is.Null);
    }

    [Test]
    public void AFailedFirstRunLeavesNoWatermark()
    {
        var db = Db();
        db.Migrate();
        var store = new WatermarkStore(db);

        store.Fail("Prose", "azure", "target unreachable");

        Assert.That(store.Get("Prose", "azure"), Is.Null);
    }

    [Test]
    public void ASuccessfulRunStoresTheStartVersionAndTime()
    {
        var db = Db();
        db.Migrate();
        var store = new WatermarkStore(db);
        var start = Utc(10);

        store.Commit("Prose", "azure", 42, start,
            [new DbSyncTableState("dbo.Book", "tracked", 42, null), new DbSyncTableState("dbo.Note", "hashdiff", null, "{\"rows\":3}")]);

        var w = store.Get("Prose", "azure")!;
        Assert.That(w.LastVersion, Is.EqualTo(42));
        Assert.That(w.LastStartUtc, Is.EqualTo(start));
        Assert.That(w.LastStartUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(w.LastEndUtc, Is.GreaterThan(start));
        Assert.That(w.LastOutcome, Is.EqualTo("succeeded"));
        var tables = store.TableStates("Prose", "azure");
        Assert.That(tables.Select(t => t.TableName), Is.EqualTo(new[] { "dbo.Book", "dbo.Note" }));
        Assert.That(tables[1].StateJson, Is.EqualTo("{\"rows\":3}"));
    }

    [Test]
    public void AFailedRunLeavesTheOldWatermark()
    {
        var db = Db();
        db.Migrate();
        var store = new WatermarkStore(db);
        var start = Utc(30);
        store.Commit("Prose", "azure", 7, start, [new DbSyncTableState("dbo.Book", "tracked", 7, null)]);

        store.Fail("Prose", "azure", "timeout copying dbo.Book");

        var w = store.Get("Prose", "azure")!;
        Assert.That(w.LastVersion, Is.EqualTo(7));
        Assert.That(w.LastStartUtc, Is.EqualTo(start));
        Assert.That(w.LastOutcome, Is.EqualTo("failed"));
        Assert.That(w.LastMessage, Is.EqualTo("timeout copying dbo.Book"));
        Assert.That(store.TableStates("Prose", "azure").Single().LastVersion, Is.EqualTo(7));
    }

    [Test]
    public void ALaterSuccessMovesTheWatermarkAndUpdatesTableStates()
    {
        var db = Db();
        db.Migrate();
        var store = new WatermarkStore(db);
        store.Commit("Prose", "azure", 7, Utc(30), [new DbSyncTableState("dbo.Book", "tracked", 7, null)]);
        var start = Utc(5);

        store.Commit("Prose", "azure", 9, start, [new DbSyncTableState("dbo.Book", "tracked", 9, null)]);

        var w = store.Get("Prose", "azure")!;
        Assert.That(w.LastVersion, Is.EqualTo(9));
        Assert.That(w.LastStartUtc, Is.EqualTo(start));
        Assert.That(store.TableStates("Prose", "azure").Single().LastVersion, Is.EqualTo(9));
        Assert.That(store.Get("Prose", "other"), Is.Null);
    }

    [Test]
    public void MigrateIsIdempotentAndUpgradesAVersion1Database()
    {
        var db = Db();
        db.Migrate();
        using (var c = db.Open())
        using (var cmd = new SqlCommand(
            "DROP TABLE dbo.DbSyncTableState; DROP TABLE dbo.DbSyncWatermark; UPDATE Meta.SchemaVersion SET Version = 1 WHERE Id = 1", c))
            cmd.ExecuteNonQuery();
        Assert.That(db.ReadSchemaVersion(), Is.EqualTo(1));

        db.Migrate();
        db.Migrate();

        Assert.That(db.ReadSchemaVersion(), Is.EqualTo(SchedulerDb.SchemaVersion));
        Assert.That(new WatermarkStore(db).Get("Prose", "azure"), Is.Null);
    }
}
