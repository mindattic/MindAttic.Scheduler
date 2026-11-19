namespace MindAttic.Scheduler.Core;

/// <summary>
/// One entry of a job's schedule, in the exact shape jobs.json and scheduler.ps1's
/// <c>job status --json</c> both use: exactly one of <see cref="Every"/>, <see cref="Daily"/>
/// (optionally with <see cref="DaysOfWeek"/>), <see cref="AtLogon"/> or <see cref="Once"/> is set.
/// </summary>
public sealed class ScheduleEntry
{
    /// <summary>A repeat interval, "hh:mm:ss".</summary>
    public string? Every { get; set; }

    /// <summary>A fixed daily (or, with <see cref="DaysOfWeek"/>, weekly) time, "HH:mm".</summary>
    public string? Daily { get; set; }

    /// <summary>With <see cref="Daily"/>: only these days ("Mon".."Sun"), instead of every day.</summary>
    public string[]? DaysOfWeek { get; set; }

    /// <summary>At logon.</summary>
    public bool? AtLogon { get; set; }

    /// <summary>
    /// A runtime job's single future fire time (local ISO 8601). Read-only: a one-shot runtime
    /// job's schedule isn't edited from the dashboard (see <see cref="IJobProvider.SetSchedule"/>).
    /// </summary>
    public string? Once { get; set; }
}
