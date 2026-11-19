namespace MindAttic.Scheduler.Core;

/// <summary>One overnight job's night: its slot and what actually happened.</summary>
/// <param name="Name">The job.</param>
/// <param name="Planned">Its arranged slot, HH:mm, or null when it had none.</param>
/// <param name="Run">The night's run, or null when there was none.</param>
/// <param name="Outcome">pending, running, succeeded, failed, abandoned or missed.</param>
/// <param name="Overran">Still running at the window's end, or finished after it.</param>
public sealed record NightRow(string Name, string? Planned, JobRunRecord? Run, string Outcome, bool Overran)
{
    /// <summary>The outcome as the dashboard shows it, e.g. "failed (exit 2), overran".</summary>
    public string Display
    {
        get
        {
            var text = Outcome == "failed" && Run?.ExitCode is { } code ? $"failed (exit {code})" : Outcome;
            return Overran ? text + ", overran" : text;
        }
    }
}

/// <summary>
/// "Did last night work?" (M.SCHD-T42): each arranged job's planned slot next to its actual run.
/// A slot with no run is missed; a run still going (or ending) after the window's end overran.
/// </summary>
public static class NightReport
{
    /// <summary>How long after its slot a job with no run still counts as pending rather than missed.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    /// <summary>The night (the date whose 00:00 to 08:00 it covers) the dashboard shows by default: today's.</summary>
    public static DateOnly DefaultNight(DateTimeOffset now) => DateOnly.FromDateTime(now.LocalDateTime);

    /// <summary>Midnight of <paramref name="night"/>, local time.</summary>
    public static DateTimeOffset Midnight(DateOnly night)
    {
        var local = night.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>The runs that can belong to a night: queued from an hour before its midnight until noon.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Span(DateOnly night)
    {
        var midnight = Midnight(night);
        return (midnight.AddHours(-1), midnight.AddHours(12));
    }

    /// <summary>Builds the night's rows, in slot order.</summary>
    public static IReadOnlyList<NightRow> Build(
        DateOnly night,
        IEnumerable<string> autoJobs,
        Arrangement arrangement,
        IReadOnlyList<JobRunRecord> runs,
        DateTimeOffset now,
        int windowEnd = 8 * 60)
    {
        var midnight = Midnight(night);
        var (from, to) = Span(night);
        var end = midnight.AddMinutes(windowEnd);
        var rows = new List<NightRow>();
        foreach (var name in autoJobs)
        {
            var planned = arrangement.Slots.TryGetValue(name, out var slot) ? slot.Start : null;
            var run = runs.Where(r => r.JobName == name && r.QueuedAt >= from && r.QueuedAt < to)
                .OrderBy(r => r.QueuedAt).FirstOrDefault();
            if (run is null)
            {
                var due = OvernightOptimizer.Parse(planned) is { } m ? midnight.AddMinutes(m) + Grace : end;
                rows.Add(new NightRow(name, planned, null, now < due ? "pending" : "missed", false));
                continue;
            }
            var overran = run.Outcome == "running" ? now > end : run.EndedAt is { } e && e > end;
            rows.Add(new NightRow(name, planned, run, run.Outcome, overran));
        }
        return rows
            .OrderBy(r => OvernightOptimizer.Parse(r.Planned) ?? int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }
}
