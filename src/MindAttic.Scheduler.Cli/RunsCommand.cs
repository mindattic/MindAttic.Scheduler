using System.Globalization;
using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Cli;

/// <summary><c>runs [--job X] [--since DATE] [--limit N] [--summary] [--night yyyy-MM-dd|last] [--json]</c>.</summary>
internal static class RunsCommand
{
    public static int Execute(Flags flags)
    {
        flags.AllowOnly("job", "since", "limit", "summary", "night", "json");
        var json = flags.Has("json");
        var log = new RunLog(SchedulerDb.FromEnvironment());

        if (flags.Has("summary"))
        {
            var summaries = log.Summaries().Values.OrderBy(s => s.JobName, StringComparer.Ordinal).ToList();
            if (json) Json.Write(summaries.Select(s => new
            {
                jobName = s.JobName,
                last = s.Last is { } l ? Row(l) : null,
                usualMinutes = s.UsualDuration is { } u ? Math.Round(u.TotalMinutes, 1) : (double?)null,
                measuredRuns = s.MeasuredRuns,
            }));
            else
            {
                Console.WriteLine($"{"Job",-32} {"Last result",-20} {"Last start",-17} {"Usual",8}");
                foreach (var s in summaries)
                    Console.WriteLine($"{s.JobName,-32} {Result(s.Last),-20} {s.Last?.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-",-17} {(s.UsualDuration is { } u ? $"{u.TotalMinutes:0.0}m" : "-"),8}");
            }
            return 0;
        }

        if (flags.Value("night") is { } nightText)
        {
            var now = DateTimeOffset.Now;
            var night = nightText.Equals("last", StringComparison.OrdinalIgnoreCase)
                ? NightReport.DefaultNight(now)
                : DateOnly.ParseExact(nightText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var autoJobs = ManifestJob.Load(SchedulerPaths.Manifest).Where(j => j.IsAuto).Select(j => j.Name);
            var runs = log.Runs(since: NightReport.Span(night).From, limit: 1000);
            var rows = NightReport.Build(night, autoJobs, Arrangement.Load(), runs, now);
            if (json) Json.Write(new
            {
                night = night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                jobs = rows.Select(r => new
                {
                    name = r.Name,
                    planned = r.Planned,
                    startedAt = r.Run?.StartedAt,
                    endedAt = r.Run?.EndedAt,
                    waitSeconds = r.Run is { } x ? Math.Round(x.Wait.TotalSeconds) : (double?)null,
                    exitCode = r.Run?.ExitCode,
                    outcome = r.Outcome,
                    overran = r.Overran,
                    display = r.Display,
                }),
            });
            else
            {
                Console.WriteLine($"Night of {night:yyyy-MM-dd}");
                Console.WriteLine($"{"Job",-28} {"Slot",-6} {"Started",-6} {"Ended",-6} {"Wait",6}  Outcome");
                foreach (var r in rows)
                    Console.WriteLine($"{r.Name,-28} {r.Planned ?? "-",-6} {Hm(r.Run?.StartedAt),-6} {Hm(r.Run?.EndedAt),-6} {(r.Run is { } x ? $"{x.Wait.TotalMinutes:0}m" : "-"),6}  {r.Display}");
            }
            return 0;
        }

        DateTimeOffset? since = flags.Value("since") is { } s2
            ? DateTimeOffset.Parse(s2, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal)
            : null;
        var limit = flags.Value("limit") is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : 50;
        var list = log.Runs(flags.Value("job"), since, limit);
        if (json) Json.Write(list.Select(Row));
        else
        {
            Console.WriteLine($"{"Started",-17} {"Job",-32} {"Minutes",8} {"Wait",6}  Result");
            foreach (var r in list)
                Console.WriteLine($"{r.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm} {r.JobName,-32} {(r.Duration is { } d ? d.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture) : "-"),8} {r.Wait.TotalMinutes,5:0}m  {Result(r)}");
        }
        return 0;
    }

    private static object Row(JobRunRecord r) => new
    {
        id = r.Id,
        jobName = r.JobName,
        host = r.Host,
        exclusive = r.Exclusive,
        queuedAt = r.QueuedAt,
        startedAt = r.StartedAt,
        endedAt = r.EndedAt,
        exitCode = r.ExitCode,
        outcome = r.Outcome,
        waitSeconds = Math.Round(r.Wait.TotalSeconds),
        durationSeconds = r.Duration is { } d ? Math.Round(d.TotalSeconds) : (double?)null,
    };

    private static string Result(JobRunRecord? r) => r is null ? "-"
        : r.Outcome == "failed" && r.ExitCode is { } c ? $"failed (exit {c})" : r.Outcome;

    private static string Hm(DateTimeOffset? t) => t is { } v ? v.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) : "-";
}
