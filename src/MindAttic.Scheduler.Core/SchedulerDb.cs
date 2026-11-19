using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core;

/// <summary>
/// The MindAttic.Scheduler database (M.SCHD-T42): where every job run is recorded. LocalDB in
/// development, Azure SQL in production. <see cref="Migrate"/> is the only code that runs DDL.
/// </summary>
public sealed class SchedulerDb
{
    /// <summary>A full connection string; wins over everything else.</summary>
    public const string EnvVar = "MINDATTIC_SCHEDULER_SQL";

    /// <summary>The database's name, the same locally and on Azure.</summary>
    public const string DatabaseName = "MindAttic.Scheduler";

    /// <summary>The schema version <see cref="Migrate"/> brings the database to.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The fallback: LocalDB, Windows authentication. LocalDB starts on demand.</summary>
    public const string LocalDbConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=MindAttic.Scheduler;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=20";

    /// <summary>The connection string this instance uses.</summary>
    public string ConnectionString { get; }

    /// <summary>Where <see cref="ConnectionString"/> came from: env, database.json or localdb.</summary>
    public string Source { get; }

    /// <summary>Uses <paramref name="connectionString"/> as given.</summary>
    public SchedulerDb(string connectionString, string source = "explicit")
    {
        ConnectionString = connectionString;
        Source = source;
        EntraTokenCache.Use(connectionString);
    }

    /// <summary>
    /// The connection the machine is configured for: <see cref="EnvVar"/>, else
    /// <c>%APPDATA%\MindAttic\MindAttic.Scheduler\database.json</c> (<c>{"SqlServer": "..."}</c>), else LocalDB.
    /// </summary>
    public static SchedulerDb FromEnvironment(string? databaseConfigPath = null)
    {
        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env) return new SchedulerDb(env, "env");
        var path = databaseConfigPath ?? SchedulerPaths.DatabaseConfig;
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("SqlServer", out var s) && s.ValueKind == JsonValueKind.String &&
                    s.GetString() is { Length: > 0 } cs)
                    return new SchedulerDb(cs, "database.json");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new SchedulerDb(LocalDbConnectionString, "localdb");
    }

    /// <summary>Server and database, without credentials, for messages.</summary>
    public string Describe()
    {
        try
        {
            var b = new SqlConnectionStringBuilder(ConnectionString);
            return $"{b.DataSource}/{b.InitialCatalog} ({Source})";
        }
        catch (ArgumentException) { return $"(unreadable connection string, {Source})"; }
    }

    /// <summary>True when the connection points at LocalDB.</summary>
    public bool IsLocalDb =>
        ConnectionString.Contains("(localdb)", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a connection.</summary>
    public SqlConnection Open()
    {
        var c = new SqlConnection(ConnectionString);
        c.Open();
        return c;
    }

    /// <summary>
    /// Creates the schema if it isn't there (idempotent). On LocalDB the database itself is created
    /// first; on Azure it is made by deploy-azure-db.ps1.
    /// </summary>
    public void Migrate()
    {
        if (IsLocalDb) EnsureLocalDatabase();
        using var c = Open();
        foreach (var batch in SchemaBatches)
        {
            using var cmd = new SqlCommand(batch, c) { CommandTimeout = 60 };
            cmd.ExecuteNonQuery();
        }
        using var v = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM Meta.SchemaVersion WHERE Id = 1)
                UPDATE Meta.SchemaVersion SET Version = @v, AppliedAt = SYSDATETIMEOFFSET() WHERE Id = 1 AND Version < @v;
            ELSE
                INSERT Meta.SchemaVersion (Id, Version, AppliedAt) VALUES (1, @v, SYSDATETIMEOFFSET());
            """, c);
        v.Parameters.AddWithValue("@v", SchemaVersion);
        v.ExecuteNonQuery();
    }

    /// <summary>The schema version recorded in the database, or null when it has none.</summary>
    public int? ReadSchemaVersion()
    {
        using var c = Open();
        using var cmd = new SqlCommand(
            "IF OBJECT_ID(N'Meta.SchemaVersion') IS NULL SELECT CAST(NULL AS int) ELSE SELECT Version FROM Meta.SchemaVersion WHERE Id = 1", c);
        return cmd.ExecuteScalar() is int i ? i : null;
    }

    private void EnsureLocalDatabase()
    {
        var b = new SqlConnectionStringBuilder(ConnectionString);
        var name = string.IsNullOrEmpty(b.InitialCatalog) ? DatabaseName : b.InitialCatalog;
        b.InitialCatalog = "master";
        using var c = new SqlConnection(b.ConnectionString);
        c.Open();
        using var cmd = new SqlCommand(
            "IF DB_ID(@n) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@n); EXEC (@sql); END",
            c) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@n", name);
        cmd.ExecuteNonQuery();
    }

    private static readonly string[] SchemaBatches =
    [
        "IF SCHEMA_ID(N'Meta') IS NULL EXEC(N'CREATE SCHEMA Meta')",
        """
        IF OBJECT_ID(N'Meta.SchemaVersion') IS NULL
        CREATE TABLE Meta.SchemaVersion (
            Id int NOT NULL CONSTRAINT PK_SchemaVersion PRIMARY KEY,
            Version int NOT NULL,
            AppliedAt datetimeoffset NOT NULL)
        """,
        """
        IF OBJECT_ID(N'dbo.JobRun') IS NULL
        CREATE TABLE dbo.JobRun (
            Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_JobRun PRIMARY KEY,
            RunKey uniqueidentifier NOT NULL CONSTRAINT UQ_JobRun_RunKey UNIQUE,
            JobName nvarchar(200) NOT NULL,
            Host nvarchar(100) NOT NULL,
            Pid int NULL,
            Exclusive bit NOT NULL,
            QueuedAt datetimeoffset NOT NULL,
            StartedAt datetimeoffset NOT NULL,
            EndedAt datetimeoffset NULL,
            ExitCode int NULL,
            Outcome varchar(16) NOT NULL CONSTRAINT CK_JobRun_Outcome
                CHECK (Outcome IN ('running', 'succeeded', 'failed', 'abandoned')))
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JobRun_JobName_StartedAt' AND object_id = OBJECT_ID(N'dbo.JobRun'))
        CREATE INDEX IX_JobRun_JobName_StartedAt ON dbo.JobRun (JobName, StartedAt)
        """,
    ];
}
