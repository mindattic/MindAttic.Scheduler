using System.Text.Json;
using MindAttic.Scheduler.Core;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

public class JobsManifestEditorTests
{
    private static string WriteManifest(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jobs-manifest-tests-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string TwoJobs = """
        {
          "$comment": "kept as-is",
          "jobs": [
            { "name": "A", "command": "a.exe", "schedule": { "every": "00:05:00" } },
            { "name": "B", "command": "b.exe", "schedule": { "daily": "02:00" } }
          ]
        }
        """;

    [Test]
    public void SetsEnabledOnTheNamedJobOnly()
    {
        var path = WriteManifest(TwoJobs);
        try
        {
            JobsManifestEditor.SetEnabled(path, "B", false);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var jobs = doc.RootElement.GetProperty("jobs");
            Assert.That(jobs[1].TryGetProperty("enabled", out var bEnabled), Is.True, "job B should have an enabled field");
            Assert.That(bEnabled.GetBoolean(), Is.False);
            Assert.That(jobs[0].TryGetProperty("enabled", out _), Is.False);
            Assert.That(doc.RootElement.GetProperty("$comment").GetString(), Is.EqualTo("kept as-is"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void ReplacesTheScheduleOnTheNamedJobOnly()
    {
        var path = WriteManifest(TwoJobs);
        try
        {
            JobsManifestEditor.SetSchedule(path, "A", [new ScheduleEntry { Daily = "09:00", DaysOfWeek = ["Mon", "Tue"] }]);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var jobs = doc.RootElement.GetProperty("jobs");
            var schedule = jobs[0].GetProperty("schedule");
            Assert.That(schedule[0].GetProperty("daily").GetString(), Is.EqualTo("09:00"));
            Assert.That(schedule[0].GetProperty("daysOfWeek").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "Mon", "Tue" }));
            // Job B's own schedule is untouched.
            Assert.That(jobs[1].GetProperty("schedule").GetProperty("daily").GetString(), Is.EqualTo("02:00"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void ThrowsForAnUnknownJobName()
    {
        var path = WriteManifest(TwoJobs);
        try
        {
            Assert.Throws<InvalidOperationException>(() => JobsManifestEditor.SetEnabled(path, "Nope", true));
        }
        finally { File.Delete(path); }
    }
}
