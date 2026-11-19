using MindAttic.Scheduler.Cli;
using MindAttic.Scheduler.Core;
using Xunit;

namespace MindAttic.Scheduler.Core.Tests;

public class ArrangementTests
{
    [Fact]
    public void RoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"arrangement-tests-{Guid.NewGuid():N}.json");
        try
        {
            var plan = OvernightOptimizer.Optimize(new OptimizerInput(
                [new OptimizerJob("A", TimeSpan.FromMinutes(42), "measured")], [], [TimeSpan.FromMinutes(5)]));
            Arrangement.From(plan, DateTimeOffset.Now).Save(path);

            var back = Arrangement.Load(path);

            Assert.Equal("00:01", back.Slots["A"].Start);
            Assert.Equal(42, back.Slots["A"].DurationMinutes);
            Assert.Equal("measured", back.Slots["A"].Source);
            Assert.Equal(1, back.StartOf("A"));
            Assert.Contains("\"slots\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMissingFileIsAnEmptyArrangement() =>
        Assert.Empty(Arrangement.Load(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json")).Slots);

    [Fact]
    public void ManifestReaderSeesAutoEntriesAndEstimates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jobs-auto-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "jobs": [
              { "name": "A", "command": "a.exe", "schedule": { "daily": "auto" }, "estimate": "00:20:00" },
              { "name": "B", "command": "b.exe", "schedule": [{ "daily": "02:00" }, { "every": "00:05:00" }], "enabled": false }
            ] }
            """);
        try
        {
            var jobs = ManifestJob.Load(path);

            Assert.True(jobs[0].IsAuto);
            Assert.Equal(TimeSpan.FromMinutes(20), jobs[0].Estimate);
            Assert.False(jobs[1].IsAuto);
            Assert.False(jobs[1].Enabled);
            Assert.Equal(2, jobs[1].Schedule.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NightReportCallsAnUnrunPastSlotMissedAndALateFinishOverran()
    {
        var night = new DateOnly(2026, 11, 20);
        var midnight = NightReport.Midnight(night);
        var arrangement = new Arrangement
        {
            Slots =
            {
                ["Missed"] = new ArrangedSlot { Start = "00:01" },
                ["Late"] = new ArrangedSlot { Start = "01:01" },
                ["Later"] = new ArrangedSlot { Start = "07:56" },
            },
        };
        var late = new JobRunRecord(1, Guid.NewGuid(), "Late", "PC", 1, true,
            midnight.AddMinutes(61), midnight.AddMinutes(61), midnight.AddHours(9), 0, "succeeded");

        var rows = NightReport.Build(night, ["Missed", "Late", "Later"], arrangement, [late], midnight.AddHours(7.5));

        Assert.Equal("missed", rows.Single(r => r.Name == "Missed").Outcome);
        Assert.True(rows.Single(r => r.Name == "Late").Overran);
        Assert.Equal("succeeded, overran", rows.Single(r => r.Name == "Late").Display);
        Assert.Equal("pending", rows.Single(r => r.Name == "Later").Outcome);
    }

    [Fact]
    public void RunCommandKeepsTheCommandLineExactlyAsGiven()
    {
        const string raw = "\"C:\\Apps\\mindattic-scheduler.exe\" run Azure.BackupNightly --exclusive -- powershell.exe -File \"D:\\A B\\x.ps1\" -Tier \"Basic,Small\"";
        var args = new[] { "run", "Azure.BackupNightly", "--exclusive", "--", "powershell.exe", "-File", "D:\\A B\\x.ps1", "-Tier", "Basic,Small" };

        var parsed = RunCommand.Parse(args, raw);

        Assert.Equal("Azure.BackupNightly", parsed.Job);
        Assert.True(parsed.Exclusive);
        Assert.Equal("powershell.exe -File \"D:\\A B\\x.ps1\" -Tier \"Basic,Small\"", parsed.Command);
        Assert.Equal(("C:\\A B\\x.exe", "--keep 30"), RunCommand.Split("\"C:\\A B\\x.exe\" --keep 30"));
        Assert.Equal(("powershell.exe", "-NoProfile"), RunCommand.Split("powershell.exe -NoProfile"));
    }
}
