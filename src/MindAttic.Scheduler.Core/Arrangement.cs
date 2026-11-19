using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindAttic.Scheduler.Core;

/// <summary>One arranged job's slot in arrangement.json.</summary>
public sealed class ArrangedSlot
{
    /// <summary>The start, HH:mm. install.ps1 registers the job's daily trigger at this time.</summary>
    public string Start { get; set; } = "";

    /// <summary>The duration the plan used, in minutes: the cache the arranger falls back on when the database is unreachable.</summary>
    public double DurationMinutes { get; set; }

    /// <summary>measured, estimate or default.</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// <c>%LOCALAPPDATA%\MindAttic\Scheduler\arrangement.json</c> (M.SCHD-T42): the arranged overnight
/// slots, kept out of jobs.json so a nightly re-arrange never leaves a diff in the repo. install.ps1
/// reads <c>slots.&lt;job&gt;.start</c> for every <c>"daily": "auto"</c> entry.
/// </summary>
public sealed class Arrangement
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>When the slots were last written.</summary>
    public DateTimeOffset? ArrangedAt { get; set; }

    /// <summary>The gap between jobs the plan used.</summary>
    public int GapMinutes { get; set; }

    /// <summary>Whether the plan finishes by the window's end.</summary>
    public bool Fits { get; set; } = true;

    /// <summary>Job name to slot.</summary>
    public Dictionary<string, ArrangedSlot> Slots { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Reads <paramref name="path"/>; an empty arrangement when it's missing or unreadable.</summary>
    public static Arrangement Load(string? path = null)
    {
        path ??= SchedulerPaths.Arrangement;
        try
        {
            if (!File.Exists(path)) return new Arrangement();
            var a = JsonSerializer.Deserialize<Arrangement>(File.ReadAllText(path), Options) ?? new Arrangement();
            a.Slots = new Dictionary<string, ArrangedSlot>(a.Slots ?? [], StringComparer.Ordinal);
            return a;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Arrangement();
        }
    }

    /// <summary>Writes <paramref name="path"/> atomically (a temp file renamed over it).</summary>
    public void Save(string? path = null)
    {
        path ??= SchedulerPaths.Arrangement;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The arrangement a plan describes.</summary>
    public static Arrangement From(ArrangementPlan plan, DateTimeOffset now) => new()
    {
        ArrangedAt = now,
        GapMinutes = plan.GapMinutes,
        Fits = plan.Fits,
        Slots = plan.Slots.ToDictionary(
            s => s.Name,
            s => new ArrangedSlot { Start = s.StartText, DurationMinutes = Math.Round(s.Duration.TotalMinutes, 1), Source = s.DurationSource },
            StringComparer.Ordinal),
    };

    /// <summary>The job's current start in minutes after midnight, or null.</summary>
    public int? StartOf(string job) =>
        Slots.TryGetValue(job, out var s) ? OvernightOptimizer.Parse(s.Start) : null;
}
