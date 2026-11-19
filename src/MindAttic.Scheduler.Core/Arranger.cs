namespace MindAttic.Scheduler.Core;

/// <summary>A plan, with what it was made from (M.SCHD-T42).</summary>
/// <param name="Plan">The proposed slots.</param>
/// <param name="Current">arrangement.json as it was before.</param>
/// <param name="Pinned">The pinned jobs inside the window the plan went around.</param>
/// <param name="EveryIntervals">The every-N intervals whose fire minutes were avoided.</param>
/// <param name="DatabaseNote">Null when measured durations came from the database; otherwise why not.</param>
public sealed record ArrangeResult(
    ArrangementPlan Plan,
    Arrangement Current,
    IReadOnlyList<PinnedBlock> Pinned,
    IReadOnlyList<TimeSpan> EveryIntervals,
    string? DatabaseNote);

/// <summary>
/// Arranges the overnight jobs: reads jobs.json, the measured durations (the database, else the
/// durations cached in arrangement.json), plans with <see cref="OvernightOptimizer"/>, and saves
/// arrangement.json. Shared by the CLI's <c>arrange</c> verb and the dashboard's Optimize button.
/// </summary>
public sealed class Arranger(string? manifestPath = null, string? arrangementPath = null, SchedulerDb? db = null)
{
    /// <summary>The duration used for a job with no history and no <c>estimate</c>.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(15);

    private readonly string manifest = manifestPath ?? SchedulerPaths.Manifest;
    private readonly string arrangement = arrangementPath ?? SchedulerPaths.Arrangement;

    /// <summary>The overnight window's start, minutes after midnight.</summary>
    public int WindowStart { get; init; }

    /// <summary>The overnight window's end (everything finished by), minutes after midnight.</summary>
    public int WindowEnd { get; init; } = 8 * 60;

    /// <summary>The preferred gap between arranged jobs.</summary>
    public int GapMinutes { get; init; } = 5;

    /// <summary>
    /// Plans the night. <paramref name="optimize"/> re-plans from scratch; otherwise today's slots
    /// are kept when they're still sound (see <see cref="OvernightOptimizer.Arrange"/>).
    /// </summary>
    public ArrangeResult Plan(bool optimize)
    {
        var jobs = ManifestJob.Load(manifest);
        var current = Arrangement.Load(arrangement);

        IReadOnlyDictionary<string, JobSummary> measured = new Dictionary<string, JobSummary>();
        string? note = null;
        if (db is null) note = "no database configured; using cached and estimated durations";
        else
        {
            try
            {
                var log = new RunLog(db);
                try { log.SweepAbandoned(); } catch (Exception) { /* best effort */ }
                measured = log.Summaries();
            }
            catch (Exception ex)
            {
                note = $"database unreachable ({ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}); using cached and estimated durations";
            }
        }

        (TimeSpan, string) DurationOf(ManifestJob j)
        {
            if (measured.TryGetValue(j.Name, out var s) && s.UsualDuration is { } u && s.MeasuredRuns > 0) return (u, "measured");
            if (note is not null && current.Slots.TryGetValue(j.Name, out var cached) && cached.DurationMinutes > 0)
                return (TimeSpan.FromMinutes(cached.DurationMinutes), cached.Source is { Length: > 0 } src ? src : "cached");
            if (j.Estimate is { } e) return (e, "estimate");
            return (DefaultDuration, "default");
        }

        var enabled = jobs.Where(j => j.Enabled && !j.KeepRunning).ToList();
        var auto = enabled.Where(j => j.IsAuto)
            .Select(j => { var (d, src) = DurationOf(j); return new OptimizerJob(j.Name, d, src, current.StartOf(j.Name)); })
            .ToList();
        var pinned = new List<PinnedBlock>();
        foreach (var j in enabled)
            foreach (var e in j.Schedule)
                if (!ManifestJob.IsAutoEntry(e) && OvernightOptimizer.Parse(e.Daily) is { } at && at >= WindowStart && at < WindowEnd)
                    pinned.Add(new PinnedBlock(j.Name, at, DurationOf(j).Item1));
        var every = jobs.Where(j => j.Enabled)
            .SelectMany(j => j.Schedule)
            .Where(e => TimeSpan.TryParse(e.Every, out _))
            .Select(e => TimeSpan.Parse(e.Every!))
            .Distinct()
            .ToList();

        var input = new OptimizerInput(auto, pinned, every, WindowStart, WindowEnd, GapMinutes);
        var plan = optimize ? OvernightOptimizer.Optimize(input) : OvernightOptimizer.Arrange(input);
        return new ArrangeResult(plan, current, pinned, every, note);
    }

    /// <summary>
    /// Writes the plan to arrangement.json. A disabled auto job keeps the slot it had, so
    /// install.ps1 can still register its (disabled) task.
    /// </summary>
    public Arrangement Save(ArrangeResult result, DateTimeOffset now)
    {
        var next = Arrangement.From(result.Plan, now);
        foreach (var (name, slot) in result.Current.Slots)
            if (!next.Slots.ContainsKey(name)) next.Slots[name] = slot;
        // Drop slots of jobs that are no longer auto at all.
        var autoNames = ManifestJob.Load(manifest).Where(j => j.IsAuto).Select(j => j.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in next.Slots.Keys.Where(n => !autoNames.Contains(n)).ToList()) next.Slots.Remove(name);
        next.Save(arrangement);
        return next;
    }

    /// <summary>Runs install.ps1 so Task Scheduler picks the new slots up.</summary>
    public static PowerShellResult Install(string? installScript = null) =>
        PowerShellRunner.Run(installScript ?? SchedulerPaths.InstallScript);
}
