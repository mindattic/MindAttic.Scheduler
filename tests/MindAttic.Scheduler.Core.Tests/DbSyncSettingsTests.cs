using Microsoft.Data.SqlClient;
using MindAttic.Scheduler.Core.DbSync;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

public sealed class DbSyncSettingsTests
{
    private string dir = "";

    private string SettingsPath => Path.Combine(dir, "dbsync.json");
    private string ProvidersPath => Path.Combine(dir, "providers.json");

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), $"dbsync-settings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(dir, recursive: true);

    private DbSyncSettings Load(string json)
    {
        File.WriteAllText(SettingsPath, json);
        return DbSyncSettings.Load(SettingsPath, ProvidersPath);
    }

    [Test]
    public void MissingFileGivesDefaultsWithNoTargets()
    {
        var s = DbSyncSettings.Load(SettingsPath, ProvidersPath);

        Assert.That(s.ChangeRetentionDays, Is.EqualTo(14));
        Assert.That(s.Databases.Select(d => d.Name), Is.EqualTo(DbSyncSettings.DefaultDatabases.Select(d => d.Name)));
        Assert.That(s.Databases, Has.All.Matches<DbSyncDatabase>(d => !d.IsConfigured && d.Status == "not configured"));
        Assert.That(s.Databases.Select(d => d.Name), Does.Not.Contain("ClaudeActivity"));
        Assert.That(s.Databases.Single(d => d.Name == "IdiotProof_History").SourceInstance, Is.EqualTo("localhost"));
        Assert.That(s.Databases.Single(d => d.Name == "MindAttic.Tickets").SourceInstance, Is.EqualTo("localhost"));
        Assert.That(s.Databases.Single(d => d.Name == "MindAttic.Orchestrator").SourceInstance, Is.EqualTo("localhost"));
        Assert.That(s.Databases.Single(d => d.Name == "Prose").SourceInstance, Is.EqualTo(DbSyncSettings.LocalDb));
    }

    [Test]
    public void ParsesTunablesAndMergesDatabasesByName()
    {
        var s = Load("""
            {
              // comments are allowed
              "changeRetentionDays": 7,
              "commandTimeoutSeconds": 120,
              "bulkBatchSize": 1000,
              "maxParallelDatabases": 3,
              "hashDiffRangeRows": 5000,
              "strongHashMaxMB": 64,
              "databases": [
                { "name": "prose", "targetConnection": "Server=remote;Database=Prose;User Id=u;Password=p" },
                { "name": "Gadfly", "enabled": false },
                { "name": "NewOne", "sourceInstance": "localhost", "targetConnection": "Server=remote;Database=NewOne;Integrated Security=true" },
              ]
            }
            """);

        Assert.That(s.ChangeRetentionDays, Is.EqualTo(7));
        Assert.That(s.CommandTimeoutSeconds, Is.EqualTo(120));
        Assert.That(s.BulkBatchSize, Is.EqualTo(1000));
        Assert.That(s.MaxParallelDatabases, Is.EqualTo(3));
        Assert.That(s.HashDiffRangeRows, Is.EqualTo(5000));
        Assert.That(s.StrongHashMaxMB, Is.EqualTo(64));

        var prose = s.Databases.Single(d => d.Name == "Prose");
        Assert.That(prose.IsConfigured, Is.True);
        Assert.That(prose.SourceInstance, Is.EqualTo(DbSyncSettings.LocalDb));
        Assert.That(prose.DescribeTarget(), Is.EqualTo("remote/Prose"));
        Assert.That(prose.ToString(), Does.Not.Contain("Password"));

        Assert.That(s.Databases.Single(d => d.Name == "Gadfly").Status, Is.EqualTo("disabled"));
        Assert.That(s.Databases.Last().Name, Is.EqualTo("NewOne"));
        Assert.That(s.Databases.Last().SourceInstance, Is.EqualTo("localhost"));
        Assert.That(s.Databases, Has.Count.EqualTo(DbSyncSettings.DefaultDatabases.Count + 1));
    }

    [Test]
    public void SourceConnectionUsesWindowsAuthAndTrustsTheCertificate()
    {
        var db = DbSyncSettings.DefaultDatabases.Single(d => d.Name == "IdiotProof_History");
        var b = new SqlConnectionStringBuilder(db.SourceConnectionString(45));

        Assert.That(b.DataSource, Is.EqualTo("localhost"));
        Assert.That(b.InitialCatalog, Is.EqualTo("IdiotProof_History"));
        Assert.That(b.IntegratedSecurity, Is.True);
        Assert.That(b.TrustServerCertificate, Is.True);
        Assert.That(b.ConnectTimeout, Is.EqualTo(45));
    }

    [Test]
    public void ProviderRefResolvesThroughProvidersJson()
    {
        File.WriteAllText(ProvidersPath, """
            { "Remote": { "Server": "remote.example.net", "TenantId": "t", "ClientId": "app-id", "ClientSecret": "s3cret" } }
            """);

        var s = Load("""{ "databases": [ { "name": "ThinkTank", "targetConnection": "{Remote}" } ] }""");

        var db = s.Databases.Single(d => d.Name == "ThinkTank");
        var b = new SqlConnectionStringBuilder(db.TargetConnectionString);
        Assert.That(b.DataSource, Is.EqualTo("remote.example.net"));
        Assert.That(b.InitialCatalog, Is.EqualTo("ThinkTank"));
        Assert.That(b.Authentication, Is.EqualTo(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal));
        Assert.That(b.UserID, Is.EqualTo("app-id"));
        Assert.That(b.Password, Is.EqualTo("s3cret"));
        Assert.That(db.TargetConnection, Is.EqualTo("{Remote}"));
        Assert.That(db.ToString(), Does.Not.Contain("s3cret"));
    }

    [Test]
    public void UnknownProviderRefIsRejected()
    {
        File.WriteAllText(ProvidersPath, """{ "Other": { "Server": "x", "ClientId": "a", "ClientSecret": "b" } }""");

        var ex = Assert.Throws<InvalidDataException>(() =>
            Load("""{ "databases": [ { "name": "Prose", "targetConnection": "{Remote}" } ] }"""));
        Assert.That(ex!.Message, Does.Contain("{Remote}").And.Contain("Prose"));
    }

    [Test]
    public void ProviderMissingASecretIsRejected()
    {
        File.WriteAllText(ProvidersPath, """{ "Remote": { "Server": "x", "ClientId": "a" } }""");

        var ex = Assert.Throws<InvalidDataException>(() =>
            Load("""{ "databases": [ { "name": "Prose", "targetConnection": "{Remote}" } ] }"""));
        Assert.That(ex!.Message, Does.Contain("ClientSecret"));
    }

    [TestCase("""{ "changeRetentionDays": 0 }""", "changeRetentionDays")]
    [TestCase("""{ "bulkBatchSize": -5 }""", "bulkBatchSize")]
    [TestCase("""{ "maxParallelDatabases": "many" }""", "not valid JSON")]
    [TestCase("""{ "databases": [ { "targetConnection": "Server=x" } ] }""", "no name")]
    [TestCase("""{ "databases": [ { "name": "A" }, { "name": "a" } ] }""", "listed twice")]
    [TestCase("""{ "databases": [ { "name": "A", "sourceInstance": " " } ] }""", "empty sourceInstance")]
    [TestCase("""{ "databases": [ { "name": "A", "targetConnection": "Nonsense=1" } ] }""", "unreadable targetConnection")]
    public void InvalidValuesAreRejectedWithAClearMessage(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Load(json));
        Assert.That(ex!.Message, Does.Contain(expected).And.Contain(SettingsPath));
    }
}
