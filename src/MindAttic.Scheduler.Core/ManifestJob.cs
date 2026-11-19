using System.Text.Json;

namespace MindAttic.Scheduler.Core;

/// <summary>
/// The fields of one jobs.json job the arranger needs (M.SCHD-T42). install.ps1 stays the
/// manifest's validator; this reader only takes what it needs and skips what it can't read.
/// </summary>
public sealed record ManifestJob(
    string Name,
    IReadOnlyList<ScheduleEntry> Schedule,
    TimeSpan? Estimate,
    bool KeepRunning,
    bool Enabled)
{
    /// <summary>The value of <c>daily</c> that means "the arranger picks the time".</summary>
    public const string Auto = "auto";

    /// <summary>True for a <c>{"daily": "auto"}</c> entry.</summary>
    public static bool IsAutoEntry(ScheduleEntry e) =>
        string.Equals(e.Daily, Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>An arranged overnight job: it has a <c>"daily": "auto"</c> entry.</summary>
    public bool IsAuto => Schedule.Any(IsAutoEntry);

    /// <summary>Reads every job in <paramref name="path"/>.</summary>
    public static IReadOnlyList<ManifestJob> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Manifest {path} has no 'jobs' list.");
        var list = new List<ManifestJob>();
        foreach (var j in jobs.EnumerateArray())
        {
            if (j.ValueKind != JsonValueKind.Object) continue;
            var name = Str(j, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var schedule = new List<ScheduleEntry>();
            if (j.TryGetProperty("schedule", out var s))
            {
                if (s.ValueKind == JsonValueKind.Array)
                    foreach (var e in s.EnumerateArray()) { if (e.ValueKind == JsonValueKind.Object) schedule.Add(Entry(e)); }
                else if (s.ValueKind == JsonValueKind.Object) schedule.Add(Entry(s));
            }
            TimeSpan? estimate = TimeSpan.TryParse(Str(j, "estimate"), out var est) && est > TimeSpan.Zero ? est : null;
            list.Add(new ManifestJob(name, schedule, estimate,
                KeepRunning: Bool(j, "keepRunning") == true,
                Enabled: Bool(j, "enabled") != false));
        }
        return list;
    }

    private static ScheduleEntry Entry(JsonElement e)
    {
        string[]? days = null;
        if (e.TryGetProperty("daysOfWeek", out var d) && d.ValueKind == JsonValueKind.Array)
            days = d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
        return new ScheduleEntry
        {
            Every = Str(e, "every"),
            Daily = Str(e, "daily"),
            DaysOfWeek = days,
            AtLogon = Bool(e, "atLogon"),
        };
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
