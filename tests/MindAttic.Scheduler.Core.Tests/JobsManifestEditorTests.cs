using System.Text.Json;
using MindAttic.Scheduler.Core;
using Xunit;

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

    [Fact]
    public void SetsEnabledOnTheNamedJobOnly()
    {
        var path = WriteManifest(TwoJobs);
        try
        {
            JobsManifestEditor.SetEnabled(path, "B", false);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var jobs = doc.RootElement.GetProperty("jobs");
            Assert.False(jobs[1].TryGetProperty("enabled", out var bEnabled) is false, "job B should have an enabled field");
            Assert.False(bEnabled.GetBoolean());
            Assert.False(jobs[0].TryGetProperty("enabled", out _));
            Assert.Equal("kept as-is", doc.RootElement.GetProperty("$comment").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReplacesTheScheduleOnTheNamedJobOnly()
    {
        var path = WriteManifest(TwoJobs);
        try
        {
            JobsManifestEditor.SetSchedule(path, "A", [new ScheduleEntry { Daily = "09:00", DaysOfWeek = ["Mon", "Tue"] }]);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var jobs = doc.RootElement.GetProperty("jobs");
            var schedule = jobs[0].GetProperty("schedule");
            Assert.Equal("09:00", schedule[0].GetProperty("daily").GetString());
            Assert.Equal(["Mon", "Tue"], schedule[0].GetProperty("daysOfWeek").EnumerateArray().Select(e => e.GetString()));
            // Job B's own schedule is untouched.
            Assert.Equal("02:00", jobs[1].GetProperty("schedule").GetProperty("daily").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
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
