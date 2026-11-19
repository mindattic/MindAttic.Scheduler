using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MindAttic.Scheduler.Core;

/// <summary>
/// Patches one job's <c>enabled</c> or <c>schedule</c> field in jobs.json in place, leaving
/// every other job and field untouched, so install.ps1's own idempotent logic can apply the
/// change (M.SCHD-T38: the dashboard edits the manifest, it never re-registers a task itself).
/// </summary>
public static class JobsManifestEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Sets a manifest job's <c>enabled</c> field.</summary>
    public static void SetEnabled(string manifestPath, string jobName, bool enabled) =>
        Mutate(manifestPath, jobName, job => job["enabled"] = enabled);

    /// <summary>Replaces a manifest job's <c>schedule</c> array.</summary>
    public static void SetSchedule(string manifestPath, string jobName, IReadOnlyList<ScheduleEntry> schedule)
    {
        if (schedule.Count == 0) throw new ArgumentException("A job needs at least one schedule entry.", nameof(schedule));
        var entries = new JsonArray();
        foreach (var entry in schedule) entries.Add(ToJson(entry));
        Mutate(manifestPath, jobName, job => job["schedule"] = entries);
    }

    private static JsonObject ToJson(ScheduleEntry entry)
    {
        var node = new JsonObject();
        if (entry.Every is { } every) node["every"] = every;
        else if (entry.Daily is { } daily)
        {
            node["daily"] = daily;
            if (entry.DaysOfWeek is { Length: > 0 } days)
            {
                var array = new JsonArray();
                foreach (var d in days) array.Add(d);
                node["daysOfWeek"] = array;
            }
        }
        else if (entry.AtLogon == true) node["atLogon"] = true;
        else throw new ArgumentException("A schedule entry needs every, daily or atLogon set.", nameof(entry));
        return node;
    }

    private static void Mutate(string manifestPath, string jobName, Action<JsonObject> apply)
    {
        var text = File.ReadAllText(manifestPath);
        var root = JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidOperationException($"'{manifestPath}' is not a valid manifest.");
        var jobs = root["jobs"]?.AsArray()
            ?? throw new InvalidOperationException($"'{manifestPath}' has no 'jobs' list.");
        var job = jobs.OfType<JsonObject>().FirstOrDefault(j => string.Equals((string?)j["name"], jobName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No job named '{jobName}' in '{manifestPath}'.");

        apply(job);

        File.WriteAllText(manifestPath, root.ToJsonString(WriteOptions));
    }
}
