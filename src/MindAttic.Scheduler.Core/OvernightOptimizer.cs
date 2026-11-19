namespace MindAttic.Scheduler.Core;

/// <summary>An arranged (<c>"daily": "auto"</c>) job to place in the overnight window.</summary>
/// <param name="Name">The job.</param>
/// <param name="Duration">How long it usually runs.</param>
/// <param name="DurationSource">Where <paramref name="Duration"/> came from: measured, estimate or default.</param>
/// <param name="CurrentStart">Its slot today, in minutes after midnight, or null when it has none yet.</param>
public sealed record OptimizerJob(string Name, TimeSpan Duration, string DurationSource, int? CurrentStart = null);

/// <summary>A pinned job (an explicit HH:mm) that starts inside the window: arranged jobs go around it.</summary>
public sealed record PinnedBlock(string Name, int Start, TimeSpan Duration);

/// <summary>Everything the optimizer looks at. Times are minutes after midnight.</summary>
public sealed record OptimizerInput(
    IReadOnlyList<OptimizerJob> Jobs,
    IReadOnlyList<PinnedBlock> Pinned,
    IReadOnlyList<TimeSpan> EveryIntervals,
    int WindowStart = 0,
    int WindowEnd = 8 * 60,
    int GapMinutes = 5,
    int MinGapMinutes = 1);

/// <summary>One job's place in a plan.</summary>
public sealed record PlannedSlot(
    string Name,
    int Start,
    int End,
    TimeSpan Duration,
    string DurationSource,
    int? CurrentStart,
    bool Overruns)
{
    /// <summary><see cref="Start"/> as HH:mm.</summary>
    public string StartText => OvernightOptimizer.Format(Start);

    /// <summary><see cref="End"/> (the predicted finish) as HH:mm.</summary>
    public string EndText => OvernightOptimizer.Format(End);
}

/// <summary>A whole night's arrangement.</summary>
public sealed record ArrangementPlan(
    IReadOnlyList<PlannedSlot> Slots,
    int GapMinutes,
    int WindowEnd,
    bool Fits,
    TimeSpan Overrun,
    bool Reshuffled,
    IReadOnlyList<string> Reasons)
{
    /// <summary>The latest predicted finish, minutes after midnight (the window start when empty).</summary>
    public int PredictedFinish => Slots.Count == 0 ? 0 : Slots.Max(s => s.End);
}

/// <summary>
/// Places the overnight (<c>"daily": "auto"</c>) jobs (M.SCHD-T42): heaviest first from midnight,
/// one after another with a gap, around pinned jobs, never on a minute an every-N job fires, and
/// all finished by the window's end. Pure: no clock, no files, no database.
/// </summary>
public static class OvernightOptimizer
{
    /// <summary>Minutes after midnight as HH:mm (wrapping past 24:00).</summary>
    public static string Format(int minutes)
    {
        var m = ((minutes % 1440) + 1440) % 1440;
        return $"{m / 60:00}:{m % 60:00}";
    }

    /// <summary>HH:mm to minutes after midnight, or null.</summary>
    public static int? Parse(string? hhmm) =>
        TimeOnly.TryParseExact(hhmm ?? "", "HH:mm", out var t) ? t.Hour * 60 + t.Minute : null;

    /// <summary>A duration in whole minutes, rounded up, at least one.</summary>
    public static int Minutes(TimeSpan d) => Math.Max(1, (int)Math.Ceiling(d.TotalMinutes));

    /// <summary>
    /// Keeps today's slots when they're still sound, otherwise re-plans from scratch. Sound means
    /// every job has a slot, no two starts share a minute (with each other, a pinned job, or an
    /// every-N fire), no arranged job overlaps another or a pinned job, and all finish in time.
    /// </summary>
    public static ArrangementPlan Arrange(OptimizerInput input)
    {
        var problems = Problems(input);
        if (problems.Count > 0) return Optimize(input) with { Reasons = problems };
        var slots = input.Jobs
            .Select(j =>
            {
                var start = j.CurrentStart!.Value;
                var end = start + Minutes(j.Duration);
                return new PlannedSlot(j.Name, start, end, j.Duration, j.DurationSource, j.CurrentStart, end > input.WindowEnd);
            })
            .OrderBy(s => s.Start).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        return new ArrangementPlan(slots, input.GapMinutes, input.WindowEnd, Fits: true, TimeSpan.Zero, Reshuffled: false, []);
    }

    /// <summary>Why today's slots can't be kept; empty when they can.</summary>
    public static IReadOnlyList<string> Problems(OptimizerInput input)
    {
        var problems = new List<string>();
        var forbidden = ForbiddenStarts(input);
        var seen = new Dictionary<int, string>();
        foreach (var j in input.Jobs)
        {
            if (j.CurrentStart is not { } start) { problems.Add($"{j.Name} has no slot yet"); continue; }
            if (start < input.WindowStart || start >= input.WindowEnd) { problems.Add($"{j.Name} at {Format(start)} is outside the window"); continue; }
            var end = start + Minutes(j.Duration);
            if (end > input.WindowEnd) problems.Add($"{j.Name} would finish at {Format(end)}, after {Format(input.WindowEnd)}");
            if (forbidden.Contains(start)) problems.Add($"{j.Name} at {Format(start)} starts with a pinned or every-N job");
            if (seen.TryGetValue(start, out var other)) problems.Add($"{j.Name} and {other} both start at {Format(start)}");
            else seen[start] = j.Name;
            foreach (var p in input.Pinned)
                if (Overlaps(start, end, p.Start, p.Start + Minutes(p.Duration)))
                    problems.Add($"{j.Name} overlaps pinned {p.Name}");
        }
        var placed = input.Jobs.Where(j => j.CurrentStart is { } s && s >= input.WindowStart && s < input.WindowEnd)
            .OrderBy(j => j.CurrentStart).ToList();
        for (var i = 1; i < placed.Count; i++)
        {
            var prev = placed[i - 1];
            if (prev.CurrentStart!.Value + Minutes(prev.Duration) > placed[i].CurrentStart!.Value)
                problems.Add($"{placed[i].Name} starts before {prev.Name} is predicted to finish");
        }
        return problems;
    }

    /// <summary>
    /// Plans from scratch: heaviest first, each at the earliest free minute after the previous
    /// one's predicted end plus the gap. If that doesn't finish by the window's end the gap shrinks
    /// one minute at a time to the minimum; if it still doesn't, the plan says by how much and
    /// which jobs overrun. No job is ever dropped.
    /// </summary>
    public static ArrangementPlan Optimize(OptimizerInput input)
    {
        var order = input.Jobs
            .OrderByDescending(j => Minutes(j.Duration))
            .ThenBy(j => j.Name, StringComparer.Ordinal)
            .ToList();
        var forbidden = ForbiddenStarts(input);
        var minGap = Math.Clamp(input.MinGapMinutes, 0, Math.Max(0, input.GapMinutes));

        List<PlannedSlot> slots = [];
        var gap = Math.Max(0, input.GapMinutes);
        for (; ; gap--)
        {
            slots = Place(order, input, forbidden, gap);
            if (slots.All(s => !s.Overruns) || gap <= minGap) break;
        }
        var finish = slots.Count == 0 ? input.WindowStart : slots.Max(s => s.End);
        var overrun = TimeSpan.FromMinutes(Math.Max(0, finish - input.WindowEnd));
        return new ArrangementPlan(slots, gap, input.WindowEnd, overrun == TimeSpan.Zero, overrun, Reshuffled: true, []);
    }

    private static List<PlannedSlot> Place(List<OptimizerJob> order, OptimizerInput input, HashSet<int> forbidden, int gap)
    {
        var slots = new List<PlannedSlot>();
        var taken = new HashSet<int>();
        var cursor = input.WindowStart;
        foreach (var job in order)
        {
            var length = Minutes(job.Duration);
            var t = cursor;
            for (var guard = 0; guard < 100_000; guard++)
            {
                if (forbidden.Contains(t % 1440) || taken.Contains(t)) { t++; continue; }
                var clash = input.Pinned.FirstOrDefault(p =>
                    Overlaps(t, t + length + gap, p.Start, p.Start + Minutes(p.Duration) + gap));
                if (clash is not null) { t = Math.Max(t + 1, clash.Start + Minutes(clash.Duration) + gap); continue; }
                break;
            }
            var end = t + length;
            slots.Add(new PlannedSlot(job.Name, t, end, job.Duration, job.DurationSource, job.CurrentStart, end > input.WindowEnd));
            taken.Add(t);
            cursor = end + gap;
        }
        return slots;
    }

    /// <summary>The minutes no arranged job may start on: every pinned start and every every-N fire.</summary>
    internal static HashSet<int> ForbiddenStarts(OptimizerInput input)
    {
        var set = new HashSet<int>();
        foreach (var p in input.Pinned) set.Add(((p.Start % 1440) + 1440) % 1440);
        foreach (var every in input.EveryIntervals)
        {
            // Every-N triggers are anchored at midnight (install.ps1), so they fire at k * N.
            var n = (int)Math.Round(every.TotalMinutes);
            if (n < 1) continue;
            for (var m = 0; m < 1440; m += n) set.Add(m);
        }
        return set;
    }

    private static bool Overlaps(int aStart, int aEnd, int bStart, int bEnd) => aStart < bEnd && bStart < aEnd;
}
