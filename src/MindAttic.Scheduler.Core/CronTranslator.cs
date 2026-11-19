using System.Text.RegularExpressions;
using CronExpressionDescriptor;

namespace MindAttic.Scheduler.Core;

/// <summary>
/// Translates between a standard 5-field cron expression and this scheduler's own schedule
/// shape (M.SCHD-T38's schedule editor: "paste a cron, in any recognized format"). Only the
/// subset of cron that Windows Task Scheduler's own trigger types can represent round-trips;
/// anything else (day-of-month, month restrictions, step values on hour/day-of-week, lists
/// beyond a simple day-of-week set) is reported, not silently mistranslated.
/// </summary>
public static class CronTranslator
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>
    /// A human-readable description of any 5-field cron expression, recognized or not, for the
    /// schedule editor's live preview - even one <see cref="TryParse"/> can't map to a trigger.
    /// </summary>
    public static string ToHumanText(string cronExpression)
    {
        try { return ExpressionDescriptor.GetDescription(cronExpression); }
        catch (Exception ex) { return $"Not a recognized cron expression: {ex.Message}"; }
    }

    /// <summary>
    /// Parses a 5-field cron expression into this scheduler's schedule shape. Returns false,
    /// with a plain-English reason in <paramref name="error"/>, for anything this scheduler's
    /// triggers can't represent - never a wrong guess.
    /// </summary>
    public static bool TryParse(string cronExpression, out IReadOnlyList<ScheduleEntry> schedule, out string? error)
    {
        schedule = [];
        error = null;

        var fields = cronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "Only standard 5-field cron (minute hour day-of-month month day-of-week) is supported.";
            return false;
        }
        var (minute, hour, dayOfMonth, month, dayOfWeek) = (fields[0], fields[1], fields[2], fields[3], fields[4]);

        if (dayOfMonth != "*" || month != "*")
        {
            error = "Day-of-month and month restrictions aren't supported by this scheduler's triggers; " +
                    "only a minute/hour interval, or a fixed daily time with an optional day-of-week list, are.";
            return false;
        }

        // A cron step restarts at the top of each hour/day ("*/45" fires at :00 and :45), so it is
        // a steady "every N" only when N divides 60 minutes / 24 hours evenly.
        if (hour == "*" && dayOfWeek == "*" && TryParseStep(minute, 60, out var everyMinutes))
        {
            schedule = [new ScheduleEntry { Every = TimeSpan.FromMinutes(everyMinutes).ToString() }];
            return true;
        }

        if (dayOfWeek == "*" && minute == "0" && TryParseStep(hour, 24, out var everyHours))
        {
            schedule = [new ScheduleEntry { Every = TimeSpan.FromHours(everyHours).ToString() }];
            return true;
        }

        if (int.TryParse(minute, out var mm) && mm is >= 0 and <= 59 && int.TryParse(hour, out var hh) && hh is >= 0 and <= 23)
        {
            var daily = $"{hh:D2}:{mm:D2}";
            if (dayOfWeek == "*")
            {
                schedule = [new ScheduleEntry { Daily = daily }];
                return true;
            }
            if (TryParseDaysOfWeek(dayOfWeek, out var days))
            {
                schedule = [new ScheduleEntry { Daily = daily, DaysOfWeek = days }];
                return true;
            }
        }

        error = $"'{cronExpression}' describes a schedule this scheduler's triggers can't represent yet " +
                "(supported: every N minutes/hours where N divides an hour/day evenly, or a fixed daily time " +
                "with an optional day-of-week list).";
        return false;
    }

    /// <summary>The cron text for a schedule's first entry, for the editor's live preview; "" if it has none (atLogon, or empty).</summary>
    public static string ToCronText(IReadOnlyList<ScheduleEntry> schedule)
    {
        var entry = schedule.Count > 0 ? schedule[0] : null;
        if (entry is null || entry.AtLogon == true) return "";

        if (entry.Every is { } every && TimeSpan.TryParse(every, out var span))
        {
            // Only intervals that divide an hour/day evenly have an exact cron step (see TryParse);
            // anything else (e.g. every 90 minutes) has no cron equivalent, so no cron text.
            if (span.Seconds != 0 || span.Milliseconds != 0 || span <= TimeSpan.Zero) return "";
            var minutes = (int)span.TotalMinutes;
            if (minutes < 60) return 60 % minutes == 0 ? $"*/{minutes} * * * *" : "";
            if (minutes % 60 != 0) return "";
            var hours = minutes / 60;
            return hours < 24 && 24 % hours == 0 ? $"0 */{hours} * * *" : "";
        }

        if (entry.Daily is { } daily && TimeSpan.TryParse(daily, out var t))
        {
            var dow = entry.DaysOfWeek is { Length: > 0 } days
                ? string.Join(',', days.Select(d => Array.IndexOf(DayNames, d)).Where(i => i >= 0))
                : "*";
            return $"{t.Minutes} {t.Hours} * * {dow}";
        }

        return "";
    }

    /// <summary>"*/N" where N is 1..period-1 and divides <paramref name="period"/> evenly.</summary>
    private static bool TryParseStep(string field, int period, out int step)
    {
        step = 0;
        var match = Regex.Match(field, @"^\*/(\d{1,4})$");
        if (!match.Success) return false;
        step = int.Parse(match.Groups[1].Value);
        return step >= 1 && step < period && period % step == 0;
    }

    private static bool TryParseDaysOfWeek(string field, out string[] days)
    {
        days = [];
        var result = new List<string>();
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var n) || n is < 0 or > 7) return false;
            result.Add(DayNames[n % 7]);
        }
        days = result.Distinct().ToArray();
        return days.Length > 0;
    }
}
