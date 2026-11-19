namespace MindAttic.Scheduler.Core;

/// <summary>
/// One job as reported by <c>scheduler job status --json</c> (M.SCHD-T38): the one shape the
/// dashboard, and eventually the Azure mirror, both read.
/// </summary>
public sealed record JobStatus(
    string Name,
    string Source,
    string? Owner,
    string Task,
    string Description,
    IReadOnlyList<ScheduleEntry> ScheduleRaw,
    string ScheduleHuman,
    bool Enabled,
    string State,
    string? NextRun,
    string? LastRun,
    string Command)
{
    /// <summary>A manifest (jobs.json) job, vs. a runtime job an app registered itself.</summary>
    public bool IsManifest => string.Equals(Source, "manifest", StringComparison.OrdinalIgnoreCase);
}
