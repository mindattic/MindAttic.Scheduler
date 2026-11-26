using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace MindAttic.Scheduler.Core.DbSync;

/// <summary>What a target database holds before a run.</summary>
public enum TargetSchemaState
{
    /// <summary>The database does not exist (or cannot be opened by this login).</summary>
    Missing,

    /// <summary>The database exists with no user tables.</summary>
    Empty,

    /// <summary>The database has user tables, so its schema is left alone.</summary>
    HasTables,
}

/// <summary>
/// Creates a new target's schema from its source (M.SCHD-T72): SqlPackage extracts a schema-only dacpac from the source
/// and publishes it to the target, creating the database when it is missing, so the run's full copy can fill it. Only a
/// missing or table-less target is bootstrapped; a target with tables is never altered here. The tool and its
/// <c>/p:</c> properties come from <see cref="DbSyncSettings"/>.
/// </summary>
public static class SchemaBootstrap
{
    /// <summary>True when <paramref name="state"/> is one the bootstrap creates a schema for.</summary>
    public static bool Needed(TargetSchemaState state) => state != TargetSchemaState.HasTables;

    /// <summary>Opens the target (unpooled, so a failed open leaves no cached error) and counts its user tables.</summary>
    public static TargetSchemaState Probe(string targetConnectionString, DbSyncSettings settings)
    {
        var cs = new SqlConnectionStringBuilder(targetConnectionString) { Pooling = false }.ConnectionString;
        using var target = new SqlConnection(cs);
        try { target.Open(); }
        catch (SqlException ex) when (ex.Number == 4060) { return TargetSchemaState.Missing; }
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0", target) { CommandTimeout = settings.CommandTimeoutSeconds };
        return Convert.ToInt32(cmd.ExecuteScalar()) == 0 ? TargetSchemaState.Empty : TargetSchemaState.HasTables;
    }

    /// <summary>Extracts <paramref name="database"/>'s source schema (no data) and publishes it to its target. Throws on failure.</summary>
    public static void Apply(DbSyncDatabase database, DbSyncSettings settings)
    {
        if (!File.Exists(settings.SqlPackagePath))
            throw new FileNotFoundException($"SqlPackage is not at {settings.SqlPackagePath} (sqlPackagePath in dbsync.json), so the new target's schema cannot be created.");

        var dacpac = Path.Combine(Path.GetTempPath(), $"dbsync-{database.Name}-{Guid.NewGuid():N}.dacpac");
        try
        {
            SqlPackage(settings, "extract the source schema",
                ["/Action:Extract", $"/SourceConnectionString:{database.SourceConnectionString(settings.ConnectTimeoutSeconds)}", $"/TargetFile:{dacpac}", "/OverwriteFiles:True",
                 .. settings.SchemaExtractProperties]);
            SqlPackage(settings, "publish the schema to the target",
                ["/Action:Publish", $"/SourceFile:{dacpac}", $"/TargetConnectionString:{database.TargetConnectionString}",
                 .. settings.SchemaPublishProperties]);
        }
        finally
        {
            try { File.Delete(dacpac); } catch (IOException) { /* best effort */ }
        }
    }

    // The arguments carry connection strings, so a failure reports only the tool's own output, never the command line.
    private static void SqlPackage(DbSyncSettings settings, string what, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(settings.SqlPackagePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {settings.SqlPackagePath} to {what}.");
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(settings.SchemaBootstrapTimeoutSeconds)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw new TimeoutException($"SqlPackage did not {what} within {settings.SchemaBootstrapTimeoutSeconds} seconds (schemaBootstrapTimeoutSeconds).");
        }
        process.WaitForExit();
        if (process.ExitCode == 0) return;

        var output = (stdErr.GetAwaiter().GetResult() + "\n" + stdOut.GetAwaiter().GetResult())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var errors = output.Where(l => l.StartsWith("***", StringComparison.Ordinal) || l.StartsWith("Error", StringComparison.OrdinalIgnoreCase)).ToList();
        var detail = string.Join(" ", (errors.Count > 0 ? errors : output.TakeLast(3)).Take(5));
        throw new InvalidOperationException($"SqlPackage could not {what} (exit {process.ExitCode}): {detail}");
    }
}
