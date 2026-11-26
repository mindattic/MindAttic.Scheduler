using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>
/// One database the sync job copies (M.SCHD-T61): read from <see cref="SourceInstance"/> with Windows authentication and
/// written to <see cref="TargetConnectionString"/>, any SQL Server. With no target it is "not configured" and skipped.
/// </summary>
public sealed record DbSyncDatabase(string Name, string SourceInstance, string? TargetConnection, bool Enabled)
{
    /// <summary><see cref="TargetConnection"/> with a <c>{providerRef}</c> resolved; null when not configured.</summary>
    public string? TargetConnectionString { get; init; }

    /// <summary>True when the database has a target to sync to.</summary>
    public bool IsConfigured => TargetConnectionString is { Length: > 0 };

    /// <summary>"disabled", "not configured" or "ready", for the job's report.</summary>
    public string Status => !Enabled ? "disabled" : IsConfigured ? "ready" : "not configured";

    /// <summary>The source: <see cref="SourceInstance"/>, Windows authentication, the server certificate trusted.</summary>
    public string SourceConnectionString(int connectTimeoutSeconds) =>
        new SqlConnectionStringBuilder
        {
            DataSource = SourceInstance,
            InitialCatalog = Name,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = connectTimeoutSeconds,
            ApplicationName = "MindAttic.Scheduler DbSync",
        }.ConnectionString;

    /// <summary>The target's server and database, without credentials, for messages.</summary>
    public string DescribeTarget()
    {
        if (!IsConfigured) return "not configured";
        try
        {
            var b = new SqlConnectionStringBuilder(TargetConnectionString);
            return $"{b.DataSource}/{b.InitialCatalog}";
        }
        catch (ArgumentException) { return "(unreadable connection string)"; }
    }

    // Never print the resolved connection: it can carry a service principal's secret.
    public override string ToString() => $"{Name} ({SourceInstance} -> {DescribeTarget()}, {Status})";
}

/// <summary>
/// The DB sync job's settings (M.SCHD-T61), from <see cref="SchedulerPaths.DbSyncConfig"/>. Every tunable is here, none
/// is a constant in the sync code. The file's <c>databases</c> merge into <see cref="DefaultDatabases"/> by name: an
/// entry only has to give what it changes (usually just <c>targetConnection</c>); a new name is added.
/// </summary>
public sealed record DbSyncSettings
{
    /// <summary>LocalDB, the home of most local copies.</summary>
    public const string LocalDb = @"(localdb)\MSSQLLocalDB";

    /// <summary>Change Tracking retention set on each source database, in days.</summary>
    public int ChangeRetentionDays { get; init; } = 14;

    /// <summary>Timeout of each SQL command against either side.</summary>
    public int CommandTimeoutSeconds { get; init; } = 600;

    /// <summary>Connect timeout of each connection.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 30;

    /// <summary>Rows per SqlBulkCopy batch.</summary>
    public int BulkBatchSize { get; init; } = 5000;

    /// <summary>How many databases sync at once.</summary>
    public int MaxParallelDatabases { get; init; } = 2;

    /// <summary>Rows per key range the hashdiff fallback compares at once.</summary>
    public int HashDiffRangeRows { get; init; } = 100000;

    /// <summary>Tables up to this size get the row-by-row strong hash; larger ones the cheaper range checksum.</summary>
    public int StrongHashMaxMB { get; init; } = 1024;

    /// <summary>SqlPackage, which creates a missing or table-less target's schema from its source (M.SCHD-T72).</summary>
    public string SqlPackagePath { get; init; } = DefaultSqlPackagePath;

    /// <summary>How long each SqlPackage step of a schema bootstrap may take.</summary>
    public int SchemaBootstrapTimeoutSeconds { get; init; } = 1800;

    /// <summary>The <c>/p:</c> properties of the bootstrap's Extract: schema only, no logins or permissions.</summary>
    public IReadOnlyList<string> SchemaExtractProperties { get; init; } = DefaultSchemaExtractProperties;

    /// <summary>The <c>/p:</c> properties of the bootstrap's Publish: never drops anything, no users, logins or permissions.</summary>
    public IReadOnlyList<string> SchemaPublishProperties { get; init; } = DefaultSchemaPublishProperties;

    /// <summary><c>%USERPROFILE%\.dotnet\tools\sqlpackage.exe</c>, where <c>dotnet tool install -g microsoft.sqlpackage</c> puts it.</summary>
    public static string DefaultSqlPackagePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "sqlpackage.exe");

    public static IReadOnlyList<string> DefaultSchemaExtractProperties { get; } =
    [
        "/p:ExtractAllTableData=False",
        "/p:ExtractReferencedServerScopedElements=False",
        "/p:IgnoreUserLoginMappings=True",
        "/p:IgnorePermissions=True",
    ];

    public static IReadOnlyList<string> DefaultSchemaPublishProperties { get; } =
    [
        "/p:CreateNewDatabase=False",
        "/p:DropObjectsNotInSource=False",
        "/p:BlockOnPossibleDataLoss=True",
        "/p:AllowIncompatiblePlatform=True",
        "/p:IgnorePermissions=True",
        "/p:IgnoreRoleMembership=True",
        "/p:ExcludeObjectTypes=Users;Logins;RoleMembership;Permissions;ServerRoleMembership;ServerRoles",
    ];

    /// <summary>The databases, in order.</summary>
    public IReadOnlyList<DbSyncDatabase> Databases { get; init; } = DefaultDatabases;

    /// <summary>
    /// Every database with a local copy (D:\Backups\AzureSql\2026-11-25\copy-azure-to-local.ps1, ClaudeActivity left out
    /// as export-only) plus the localhost apps' own; none has a target until dbsync.json gives one.
    /// </summary>
    public static IReadOnlyList<DbSyncDatabase> DefaultDatabases { get; } =
    [
        new("Prose", LocalDb, null, true),
        new("IdiotProof_History", "localhost", null, true),
        new("IdiotProof_Research_GAP", LocalDb, null, true),
        new("IdiotProof_Research_T375", LocalDb, null, true),
        new("IdeaCmsDev", LocalDb, null, true),
        new("ThinkTank", LocalDb, null, true),
        new("Formicarium", LocalDb, null, true),
        new("OpenCredentials", LocalDb, null, true),
        new("TutorAuth", LocalDb, null, true),
        new("TaxRateCollector", LocalDb, null, true),
        new("Gadfly", LocalDb, null, true),
        new("IdiotProof", LocalDb, null, true),
        new("IdiotProof_Research_T405", LocalDb, null, true),
        new("IdiotProof_Research_T407", LocalDb, null, true),
        new("Prose_ProseSql", LocalDb, null, true),
        new("MindAtticIdeas", LocalDb, null, true),
        new("MindAtticIdeasDemo", LocalDb, null, true),
        new("MindAtticIdeasDemoTemplate", LocalDb, null, true),
        new("StreetSamurai", LocalDb, null, true),
        new("MindAttic.Tickets", "localhost", null, true),
        new("MindAttic.Orchestrator", "localhost", null, true),
    ];

    private static readonly Regex ProviderRef = new(@"^\{\s*([^{}\s]+)\s*\}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads <paramref name="path"/> (default <see cref="SchedulerPaths.DbSyncConfig"/>); a missing file gives the
    /// defaults with no targets. A <c>{providerRef}</c> target resolves through <paramref name="providersPath"/>
    /// (default <see cref="SchedulerPaths.DatabaseProviders"/>). Throws <see cref="InvalidDataException"/> naming the
    /// file and the bad value.
    /// </summary>
    public static DbSyncSettings Load(string? path = null, string? providersPath = null)
    {
        path ??= SchedulerPaths.DbSyncConfig;
        if (!File.Exists(path)) return new DbSyncSettings();

        FileDto dto;
        try { dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Json) ?? new FileDto(); }
        catch (JsonException ex) { throw new InvalidDataException($"{path}: not valid JSON: {ex.Message}", ex); }

        var defaults = new DbSyncSettings();
        var settings = new DbSyncSettings
        {
            ChangeRetentionDays = Positive(path, "changeRetentionDays", dto.ChangeRetentionDays, defaults.ChangeRetentionDays),
            CommandTimeoutSeconds = Positive(path, "commandTimeoutSeconds", dto.CommandTimeoutSeconds, defaults.CommandTimeoutSeconds),
            ConnectTimeoutSeconds = Positive(path, "connectTimeoutSeconds", dto.ConnectTimeoutSeconds, defaults.ConnectTimeoutSeconds),
            BulkBatchSize = Positive(path, "bulkBatchSize", dto.BulkBatchSize, defaults.BulkBatchSize),
            MaxParallelDatabases = Positive(path, "maxParallelDatabases", dto.MaxParallelDatabases, defaults.MaxParallelDatabases),
            HashDiffRangeRows = Positive(path, "hashDiffRangeRows", dto.HashDiffRangeRows, defaults.HashDiffRangeRows),
            StrongHashMaxMB = Positive(path, "strongHashMaxMB", dto.StrongHashMaxMB, defaults.StrongHashMaxMB),
            SqlPackagePath = dto.SqlPackagePath is null ? defaults.SqlPackagePath
                : !string.IsNullOrWhiteSpace(dto.SqlPackagePath) ? Environment.ExpandEnvironmentVariables(dto.SqlPackagePath.Trim())
                : throw new InvalidDataException($"{path}: sqlPackagePath is empty."),
            SchemaBootstrapTimeoutSeconds = Positive(path, "schemaBootstrapTimeoutSeconds", dto.SchemaBootstrapTimeoutSeconds, defaults.SchemaBootstrapTimeoutSeconds),
            SchemaExtractProperties = Properties(path, "schemaExtractProperties", dto.SchemaExtractProperties, defaults.SchemaExtractProperties),
            SchemaPublishProperties = Properties(path, "schemaPublishProperties", dto.SchemaPublishProperties, defaults.SchemaPublishProperties),
        };

        var merged = DefaultDatabases.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < (dto.Databases?.Count ?? 0); i++)
        {
            var d = dto.Databases![i];
            if (d is null || string.IsNullOrWhiteSpace(d.Name))
                throw new InvalidDataException($"{path}: databases[{i}] has no name.");
            var name = d.Name.Trim();
            if (!seen.Add(name))
                throw new InvalidDataException($"{path}: database '{name}' is listed twice.");
            if (d.SourceInstance is not null && string.IsNullOrWhiteSpace(d.SourceInstance))
                throw new InvalidDataException($"{path}: database '{name}' has an empty sourceInstance.");

            var at = merged.FindIndex(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            var baseline = at >= 0 ? merged[at] : new DbSyncDatabase(name, LocalDb, null, true);
            var entry = new DbSyncDatabase(
                baseline.Name,
                d.SourceInstance?.Trim() ?? baseline.SourceInstance,
                d.TargetConnection is null ? baseline.TargetConnection : NullIfBlank(d.TargetConnection),
                d.Enabled ?? baseline.Enabled);
            if (at >= 0) merged[at] = entry; else merged.Add(entry);
        }

        Dictionary<string, JsonElement>? providers = null;
        var resolved = merged
            .Select(db => db with { TargetConnectionString = ResolveTarget(path, db, providersPath, ref providers) })
            .ToList();
        return settings with { Databases = resolved };
    }

    private static string? ResolveTarget(string path, DbSyncDatabase db, string? providersPath, ref Dictionary<string, JsonElement>? providers)
    {
        if (db.TargetConnection is not { Length: > 0 } target) return null;

        var m = ProviderRef.Match(target);
        if (!m.Success)
        {
            try { _ = new SqlConnectionStringBuilder(target); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
            {
                throw new InvalidDataException($"{path}: database '{db.Name}' has an unreadable targetConnection: {ex.Message}", ex);
            }
            return target;
        }

        providersPath ??= SchedulerPaths.DatabaseProviders;
        providers ??= ReadProviders(providersPath);
        var refName = m.Groups[1].Value;
        var entry = providers.FirstOrDefault(p => string.Equals(p.Key, refName, StringComparison.OrdinalIgnoreCase));
        if (entry.Key is null)
            throw new InvalidDataException($"{path}: database '{db.Name}' targets {{{refName}}}, which {providersPath} does not define.");

        string Field(string field)
        {
            if (entry.Value.ValueKind == JsonValueKind.Object)
                foreach (var p in entry.Value.EnumerateObject())
                    if (string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase) &&
                        p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v)
                        return v;
            throw new InvalidDataException($"{providersPath}: provider '{entry.Key}' has no {field} (needed by database '{db.Name}' in {path}).");
        }

        // SqlClient takes the tenant from the server's authority challenge; EntraTokenCache keeps the token between runs.
        var cs = new SqlConnectionStringBuilder
        {
            DataSource = Field("Server"),
            InitialCatalog = db.Name,
            Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
            UserID = Field("ClientId"),
            Password = Field("ClientSecret"),
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            ApplicationName = "MindAttic.Scheduler DbSync",
        }.ConnectionString;
        EntraTokenCache.Use(cs);
        return cs;
    }

    private static Dictionary<string, JsonElement> ReadProviders(string providersPath)
    {
        if (!File.Exists(providersPath))
            throw new InvalidDataException($"{providersPath} does not exist, so no {{providerRef}} target can be resolved.");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(providersPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{providersPath}: expected a JSON object of named providers.");
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex) { throw new InvalidDataException($"{providersPath}: not valid JSON: {ex.Message}", ex); }
    }

    private static int Positive(string path, string name, int? value, int fallback) =>
        value is null ? fallback
        : value > 0 ? value.Value
        : throw new InvalidDataException($"{path}: {name} must be a positive whole number, not {value}.");

    private static IReadOnlyList<string> Properties(string path, string name, List<string?>? value, IReadOnlyList<string> fallback)
    {
        if (value is null) return fallback;
        foreach (var p in value)
            if (p is null || !p.TrimStart().StartsWith("/p:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{path}: every {name} entry must be a SqlPackage /p:Name=Value property, not '{p}'.");
        return value.Select(p => p!.Trim()).ToList();
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class FileDto
    {
        public int? ChangeRetentionDays { get; set; }
        public int? CommandTimeoutSeconds { get; set; }
        public int? ConnectTimeoutSeconds { get; set; }
        public int? BulkBatchSize { get; set; }
        public int? MaxParallelDatabases { get; set; }
        public int? HashDiffRangeRows { get; set; }
        public int? StrongHashMaxMB { get; set; }
        public string? SqlPackagePath { get; set; }
        public int? SchemaBootstrapTimeoutSeconds { get; set; }
        public List<string?>? SchemaExtractProperties { get; set; }
        public List<string?>? SchemaPublishProperties { get; set; }
        public List<DatabaseDto?>? Databases { get; set; }
    }

    private sealed class DatabaseDto
    {
        public string? Name { get; set; }
        public string? SourceInstance { get; set; }
        public string? TargetConnection { get; set; }
        public bool? Enabled { get; set; }
    }
}
