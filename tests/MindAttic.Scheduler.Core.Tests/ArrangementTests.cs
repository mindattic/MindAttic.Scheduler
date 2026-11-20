using MindAttic.Scheduler.Cli;
using MindAttic.Scheduler.Core;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

public class ArrangementTests
{
    [Test]
    public void RoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"arrangement-tests-{Guid.NewGuid():N}.json");
        try
        {
            var plan = OvernightOptimizer.Optimize(new OptimizerInput(
                [new OptimizerJob("A", TimeSpan.FromMinutes(42), "measured")], [], [TimeSpan.FromMinutes(5)]));
            Arrangement.From(plan, DateTimeOffset.Now).Save(path);

            var back = Arrangement.Load(path);

            Assert.That(back.Slots["A"].Start, Is.EqualTo("00:01"));
            Assert.That(back.Slots["A"].DurationMinutes, Is.EqualTo(42));
            Assert.That(back.Slots["A"].Source, Is.EqualTo("measured"));
            Assert.That(back.StartOf("A"), Is.EqualTo(1));
            Assert.That(File.ReadAllText(path), Does.Contain("\"slots\""));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void AMissingFileIsAnEmptyArrangement() =>
        Assert.That(Arrangement.Load(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json")).Slots, Is.Empty);

    [Test]
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

            Assert.That(jobs[0].IsAuto, Is.True);
            Assert.That(jobs[0].Estimate, Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(jobs[1].IsAuto, Is.False);
            Assert.That(jobs[1].Enabled, Is.False);
            Assert.That(jobs[1].Schedule, Has.Count.EqualTo(2));
        }
        finally { File.Delete(path); }
    }

    [Test]
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

        Assert.That(rows.Single(r => r.Name == "Missed").Outcome, Is.EqualTo("missed"));
        Assert.That(rows.Single(r => r.Name == "Late").Overran, Is.True);
        Assert.That(rows.Single(r => r.Name == "Late").Display, Is.EqualTo("succeeded, overran"));
        Assert.That(rows.Single(r => r.Name == "Later").Outcome, Is.EqualTo("pending"));
    }

    [Test]
    public void RunCommandKeepsTheCommandLineExactlyAsGiven()
    {
        const string raw = "\"C:\\Apps\\mindattic-scheduler.exe\" run Azure.BackupNightly --exclusive -- powershell.exe -File \"D:\\A B\\x.ps1\" -Tier \"Basic,Small\"";
        var args = new[] { "run", "Azure.BackupNightly", "--exclusive", "--", "powershell.exe", "-File", "D:\\A B\\x.ps1", "-Tier", "Basic,Small" };

        var parsed = RunCommand.Parse(args, raw);

        Assert.That(parsed.Job, Is.EqualTo("Azure.BackupNightly"));
        Assert.That(parsed.Exclusive, Is.True);
        Assert.That(parsed.Command, Is.EqualTo("powershell.exe -File \"D:\\A B\\x.ps1\" -Tier \"Basic,Small\""));
        Assert.That(RunCommand.Split("\"C:\\A B\\x.exe\" --keep 30"), Is.EqualTo(("C:\\A B\\x.exe", "--keep 30")));
        Assert.That(RunCommand.Split("powershell.exe -NoProfile"), Is.EqualTo(("powershell.exe", "-NoProfile")));
    }
}
