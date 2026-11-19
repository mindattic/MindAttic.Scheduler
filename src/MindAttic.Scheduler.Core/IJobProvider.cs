namespace MindAttic.Scheduler.Core;

/// <summary>
/// A source of job status and control, so the dashboard isn't hard-wired to the local machine's
/// own Task Scheduler (M.SCHD-T38). <see cref="LocalTaskSchedulerProvider"/> is the only
/// implementation this milestone wires up for real; <see cref="AzureJobProvider"/> is a stub
/// until the Azure mirror (a follow-up ticket) exists.
/// </summary>
public interface IJobProvider
{
    /// <summary>Shown in the dashboard's status bar, e.g. "Local Task Scheduler".</summary>
    string DisplayName { get; }

    /// <summary>Whether this provider can change a job (enable/disable, edit its schedule).</summary>
    bool CanEdit { get; }

    /// <summary>Every job this provider currently knows about.</summary>
    IReadOnlyList<JobStatus> List();

    /// <summary>Enables or disables a job. A manifest job's change is made through jobs.json.</summary>
    void SetEnabled(JobStatus job, bool enabled);

    /// <summary>
    /// Replaces a manifest job's schedule. Throws for a runtime job: those are owned by the app
    /// that registered them, not editable from here (see README.md's "Jobs made at runtime").
    /// </summary>
    void SetSchedule(JobStatus job, IReadOnlyList<ScheduleEntry> schedule);
}
