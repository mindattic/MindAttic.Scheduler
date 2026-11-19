namespace MindAttic.Scheduler.Core;

/// <summary>
/// Where the Scheduler keeps its machine-local state (M.SCHD-T42). Nothing here is in the repo:
/// arranged slots change nightly and must never leave a diff in jobs.json.
/// </summary>
public static class SchedulerPaths
{
    /// <summary>Overrides the repo checkout install.ps1 and jobs.json are read from.</summary>
    public const string RepoEnvVar = "MINDATTIC_SCHEDULER_REPO";

    /// <summary>The dev checkout, the default home of jobs.json and install.ps1.</summary>
    public const string DefaultRepo = @"D:\Projects\MindAttic\MindAttic.Scheduler";

    /// <summary>The published CLI, next to the dashboard (release.ps1).</summary>
    public const string DefaultCliPath = @"C:\Apps\MindAttic.Scheduler\mindattic-scheduler.exe";

    /// <summary>The repo checkout: <see cref="RepoEnvVar"/>, else <see cref="DefaultRepo"/>.</summary>
    public static string Repo =>
        Environment.GetEnvironmentVariable(RepoEnvVar) is { Length: > 0 } r ? r : DefaultRepo;

    /// <summary>jobs.json in <see cref="Repo"/>.</summary>
    public static string Manifest => Path.Combine(Repo, "jobs.json");

    /// <summary>install.ps1 in <see cref="Repo"/>.</summary>
    public static string InstallScript => Path.Combine(Repo, "install.ps1");

    /// <summary><c>%LOCALAPPDATA%\MindAttic\Scheduler</c>.</summary>
    public static string LocalState =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Scheduler");

    /// <summary>The arranged overnight slots (job to HH:mm), read by install.ps1.</summary>
    public static string Arrangement => Path.Combine(LocalState, "arrangement.json");

    /// <summary>Run-log rows waiting for the database to be reachable.</summary>
    public static string Spool => Path.Combine(LocalState, "spool.jsonl");

    /// <summary><c>%APPDATA%\MindAttic\MindAttic.Scheduler\database.json</c>: <c>{"SqlServer": "..."}</c>.</summary>
    public static string DatabaseConfig =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MindAttic", "MindAttic.Scheduler", "database.json");
}
