using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Wpf.ViewModels;

/// <summary>How run-history values read in the dashboard (M.SCHD-T42).</summary>
public static class Display
{
    /// <summary>"45s", "12m 30s" or "1h 05m".</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes:00}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds:00}s";
        return $"{(int)Math.Round(d.TotalSeconds)}s";
    }

    /// <summary>A run's result: "succeeded", "failed (exit N)", "running" or "abandoned".</summary>
    public static string Outcome(JobRunRecord run) =>
        run.Outcome == "failed" && run.ExitCode is { } code ? $"failed (exit {code})" : run.Outcome;

    /// <summary>The first line of an exception's message, for a status bar.</summary>
    public static string Reason(Exception ex) =>
        ex.Message.Split('\n')[0].Trim() is { Length: > 0 } line ? line : ex.GetType().Name;
}
