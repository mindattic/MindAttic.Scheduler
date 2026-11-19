using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Cli;

/// <summary><c>arrange [--optimize] [--dry-run] [--json] [--no-install]</c>.</summary>
internal static class ArrangeCommand
{
    public static int Execute(Flags flags)
    {
        flags.AllowOnly("optimize", "dry-run", "json", "no-install");
        var arranger = new Arranger(db: SchedulerDb.FromEnvironment());
        var result = arranger.Plan(optimize: flags.Has("optimize"));
        var dryRun = flags.Has("dry-run");

        if (flags.Has("json")) Json.Write(View(result, dryRun));
        else Print(result, dryRun);

        if (dryRun) return 0;
        arranger.Save(result, DateTimeOffset.Now);
        if (flags.Has("no-install")) return 0;

        var install = Arranger.Install();
        if (!flags.Has("json")) Console.Out.Write(install.StandardOutput);
        if (install.ExitCode != 0)
        {
            Console.Error.WriteLine($"install.ps1 failed: {install.StandardError.Trim()}");
            return install.ExitCode;
        }
        return 0;
    }

    /// <summary>The JSON shape of <c>arrange --json</c>.</summary>
    internal static object View(ArrangeResult r, bool dryRun) => new
    {
        dryRun,
        reshuffled = r.Plan.Reshuffled,
        fits = r.Plan.Fits,
        gapMinutes = r.Plan.GapMinutes,
        windowEnd = OvernightOptimizer.Format(r.Plan.WindowEnd),
        predictedFinish = OvernightOptimizer.Format(r.Plan.PredictedFinish),
        overrunMinutes = (int)r.Plan.Overrun.TotalMinutes,
        reasons = r.Plan.Reasons,
        database = r.DatabaseNote,
        jobs = r.Plan.Slots.Select(s => new
        {
            name = s.Name,
            currentStart = s.CurrentStart is { } c ? OvernightOptimizer.Format(c) : null,
            proposedStart = s.StartText,
            predictedEnd = s.EndText,
            durationMinutes = Math.Round(s.Duration.TotalMinutes, 1),
            durationSource = s.DurationSource,
            overruns = s.Overruns,
        }),
        pinned = r.Pinned.Select(p => new
        {
            name = p.Name,
            start = OvernightOptimizer.Format(p.Start),
            end = OvernightOptimizer.Format(p.Start + OvernightOptimizer.Minutes(p.Duration)),
        }),
    };

    private static void Print(ArrangeResult r, bool dryRun)
    {
        var p = r.Plan;
        if (r.DatabaseNote is { } note) Console.Error.WriteLine($"note: {note}");
        Console.WriteLine(p.Reshuffled
            ? (p.Reasons.Count > 0 ? $"Re-planned: {string.Join("; ", p.Reasons)}" : "Re-planned from scratch.")
            : "Today's slots are sound; kept.");
        Console.WriteLine($"{"Job",-28} {"Now",-6} {"Slot",-6} {"Ends",-6} {"Minutes",8}  Source");
        foreach (var s in p.Slots)
            Console.WriteLine($"{s.Name,-28} {(s.CurrentStart is { } c ? OvernightOptimizer.Format(c) : "-"),-6} {s.StartText,-6} {s.EndText,-6} {s.Duration.TotalMinutes,8:0.0}  {s.DurationSource}{(s.Overruns ? "  OVERRUNS" : "")}");
        foreach (var b in r.Pinned)
            Console.WriteLine($"  (pinned {b.Name} at {OvernightOptimizer.Format(b.Start)})");
        Console.WriteLine(p.Fits
            ? $"Fits: everything done by {OvernightOptimizer.Format(p.PredictedFinish)} (gap {p.GapMinutes} min)."
            : $"Does NOT fit: predicted finish {OvernightOptimizer.Format(p.PredictedFinish)}, {p.Overrun.TotalMinutes:0} min past {OvernightOptimizer.Format(p.WindowEnd)}.");
        if (dryRun) Console.WriteLine("Dry run: nothing written.");
    }
}
