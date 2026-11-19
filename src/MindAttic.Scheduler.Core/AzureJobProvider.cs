namespace MindAttic.Scheduler.Core;

/// <summary>
/// Placeholder for reading job status mirrored to Azure SQL (M.SCHD-T38's abstraction: a remote
/// or Azure-hosted reader can't query a Windows machine's Task Scheduler directly). The mirror
/// table, the push script that populates it, and this provider's real implementation are a
/// follow-up ticket; this stub exists so the abstraction boundary is real in code now, rather
/// than only promised, and so the dashboard's source selector has something honest to show.
/// </summary>
public sealed class AzureJobProvider : IJobProvider
{
    /// <inheritdoc />
    public string DisplayName => "Azure (not yet available)";

    /// <inheritdoc />
    public bool CanEdit => false;

    /// <inheritdoc />
    public IReadOnlyList<JobStatus> List() => [];

    /// <inheritdoc />
    public void SetEnabled(JobStatus job, bool enabled) =>
        throw new NotSupportedException("The Azure job mirror isn't implemented yet (a follow-up to M.SCHD-T38).");

    /// <inheritdoc />
    public void SetSchedule(JobStatus job, IReadOnlyList<ScheduleEntry> schedule) =>
        throw new NotSupportedException("The Azure job mirror isn't implemented yet (a follow-up to M.SCHD-T38).");
}
