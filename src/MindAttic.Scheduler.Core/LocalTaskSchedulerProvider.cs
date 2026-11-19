using System.Text.Json;

namespace MindAttic.Scheduler.Core;

/// <summary>
/// Reads and edits jobs through this machine's own Windows Task Scheduler, via scheduler.ps1 and
/// install.ps1 (M.SCHD-T38) rather than reimplementing their Task Scheduler logic in C#. This is a
/// single-machine ops tool tied to one repo checkout, not a redistributable product, so
/// <paramref name="repoPath"/> defaulting to the dev checkout is the right call, not a hack -
/// override it only if this app is ever run from somewhere else.
/// </summary>
public sealed class LocalTaskSchedulerProvider(string? repoPath = null) : IJobProvider
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string repoRoot = repoPath ?? @"D:\Projects\MindAttic\MindAttic.Scheduler";

    private string SchedulerScript => Path.Combine(repoRoot, "scheduler.ps1");
    private string InstallScript => Path.Combine(repoRoot, "install.ps1");
    private string ManifestPath => Path.Combine(repoRoot, "jobs.json");

    /// <inheritdoc />
    public string DisplayName => "Local Task Scheduler";

    /// <inheritdoc />
    public bool CanEdit => true;

    /// <inheritdoc />
    public IReadOnlyList<JobStatus> List()
    {
        var result = PowerShellRunner.Run(SchedulerScript, "job", "status", "--json")
            .EnsureSuccess("scheduler job status --json");
        var text = result.StandardOutput.Trim();
        if (text.Length == 0) return [];
        return JsonSerializer.Deserialize<List<JobStatus>>(text, ReadOptions) ?? [];
    }

    /// <inheritdoc />
    public void SetEnabled(JobStatus job, bool enabled)
    {
        if (job.IsManifest)
        {
            EditManifestAndApply(() => JobsManifestEditor.SetEnabled(ManifestPath, job.Name, enabled));
        }
        else
        {
            var verb = enabled ? "enable" : "disable";
            PowerShellRunner.Run(SchedulerScript, "job", verb, job.Name, "--owner", job.Owner ?? "")
                .EnsureSuccess($"scheduler job {verb} {job.Name}");
        }
    }

    /// <inheritdoc />
    public void SetSchedule(JobStatus job, IReadOnlyList<ScheduleEntry> schedule)
    {
        if (!job.IsManifest)
            throw new InvalidOperationException(
                $"'{job.Name}' is a runtime job owned by '{job.Owner}'; its schedule is set by that app, not from here.");
        EditManifestAndApply(() => JobsManifestEditor.SetSchedule(ManifestPath, job.Name, schedule));
    }

    /// <summary>
    /// Patches jobs.json, then runs install.ps1 to apply it. If install.ps1 rejects the edit, the
    /// original jobs.json is put back: leaving the rejected edit in place would make every later
    /// install.ps1 run fail too, and the manifest would no longer match Task Scheduler.
    /// </summary>
    private void EditManifestAndApply(Action edit)
    {
        var original = File.ReadAllText(ManifestPath);
        try
        {
            edit();
            PowerShellRunner.Run(InstallScript).EnsureSuccess("install.ps1");
        }
        catch
        {
            File.WriteAllText(ManifestPath, original);
            throw;
        }
    }
}
