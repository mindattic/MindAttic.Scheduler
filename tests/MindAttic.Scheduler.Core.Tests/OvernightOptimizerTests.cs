using MindAttic.Scheduler.Core;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

public class OvernightOptimizerTests
{
    private static OptimizerJob Job(string name, int minutes, int? current = null) =>
        new(name, TimeSpan.FromMinutes(minutes), "estimate", current);

    private static OptimizerInput Input(IReadOnlyList<OptimizerJob> jobs, IReadOnlyList<PinnedBlock>? pinned = null,
        IReadOnlyList<TimeSpan>? every = null, int windowEnd = 480, int gap = 5) =>
        new(jobs, pinned ?? [], every ?? [], 0, windowEnd, gap);

    [Test]
    public void PlacesHeaviestFirstFromMidnight()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 10), Job("B", 60), Job("C", 30)]));

        Assert.That(plan.Slots.Select(s => s.Name), Is.EqualTo(new[] { "B", "C", "A" }));
        Assert.That(plan.Slots.Select(s => s.Start), Is.EqualTo(new[] { 0, 65, 100 }));
        Assert.That(plan.Fits, Is.True);
        Assert.That(plan.PredictedFinish, Is.EqualTo(110));
    }

    [Test]
    public void KeepsTheGapBetweenEveryPairOfJobs()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 7), Job("B", 13), Job("C", 21), Job("D", 2)], gap: 5));

        var ordered = plan.Slots.OrderBy(s => s.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
            Assert.That(ordered[i].Start, Is.GreaterThanOrEqualTo(ordered[i - 1].End + 5), $"{ordered[i].Name} starts too soon after {ordered[i - 1].Name}");
    }

    [Test]
    public void GoesAroundAPinnedJob()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("Big", 60), Job("Small", 10)],
            pinned: [new PinnedBlock("Pinned", 30, TimeSpan.FromMinutes(20))]));

        var big = plan.Slots.Single(s => s.Name == "Big");
        // Big can't finish (plus the gap) before 00:30, so it goes after the pinned job's end plus the gap.
        Assert.That(big.Start, Is.EqualTo(55));
        foreach (var s in plan.Slots)
            Assert.That(s.Start < 50 && s.End > 30, Is.False, $"{s.Name} overlaps the pinned job");
    }

    [Test]
    public void NeverStartsOnAMinuteAnEveryNJobFires()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 60), Job("B", 30), Job("C", 15), Job("D", 4)],
            every: [TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)]));

        Assert.That(plan.Slots.Select(s => s.Start % 5), Has.None.EqualTo(0));
        Assert.That(plan.Slots[0].Start, Is.EqualTo(1));
    }

    [Test]
    public void NoTwoJobsShareAStartMinute()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 1), Job("B", 1), Job("C", 1)], gap: 0));

        Assert.That(plan.Slots.Select(s => s.Start), Is.Unique);
    }

    [Test]
    public void ShrinksTheGapBeforeGivingUp()
    {
        // 48 + 5 + 48 = 101 > 100, but 48 + 4 + 48 = 100 fits.
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 48), Job("B", 48)], windowEnd: 100, gap: 5));

        Assert.That(plan.Fits, Is.True);
        Assert.That(plan.GapMinutes, Is.EqualTo(4));
        Assert.That(plan.PredictedFinish, Is.EqualTo(100));
    }

    [Test]
    public void ReportsTheOverrunAndDropsNoJobWhenItCannotFit()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 300), Job("B", 300)]));

        Assert.That(plan.Fits, Is.False);
        Assert.That(plan.Slots, Has.Count.EqualTo(2));
        Assert.That(plan.GapMinutes, Is.EqualTo(1));
        Assert.That(plan.Overrun, Is.EqualTo(TimeSpan.FromMinutes(601 - 480)));
        Assert.That(plan.Slots.Single(s => s.Name == "B").Overruns, Is.True);
        Assert.That(plan.Slots.Single(s => s.Name == "A").Overruns, Is.False);
    }

    [Test]
    public void ArrangeKeepsASoundPlan()
    {
        var first = OvernightOptimizer.Optimize(Input([Job("A", 10), Job("B", 60), Job("C", 30)]));
        var current = first.Slots.Select(s => Job(s.Name, (int)s.Duration.TotalMinutes, s.Start)).ToList();

        var again = OvernightOptimizer.Arrange(Input(current));

        Assert.That(again.Reshuffled, Is.False);
        Assert.That(again.Reasons, Is.Empty);
        Assert.That(again.Slots.ToDictionary(s => s.Name, s => s.Start), Is.EquivalentTo(first.Slots.ToDictionary(s => s.Name, s => s.Start)));
    }

    [Test]
    public void ArrangeKeepsASoundPlanEvenWhenOptimizeWouldOrderItDifferently()
    {
        // Light job first is sound (no collision, fits), so plain arrange leaves it.
        var again = OvernightOptimizer.Arrange(Input([Job("Light", 5, 1), Job("Heavy", 60, 20)]));

        Assert.That(again.Reshuffled, Is.False);
        Assert.That(again.Slots.Single(s => s.Name == "Light").Start, Is.EqualTo(1));
    }

    [TestCase(null, "has no slot")]
    [TestCase(10, "both start at")]
    [TestCase(475, "would finish")]
    [TestCase(15, "starts before")]
    public void ArrangeReplansWhenSomethingIsWrong(int? bStart, string reason)
    {
        var again = OvernightOptimizer.Arrange(Input([Job("A", 30, 10), Job("B", 30, bStart)]));

        Assert.That(again.Reshuffled, Is.True);
        Assert.That(again.Reasons, Has.Some.Contains(reason));
        Assert.That(again.Slots, Has.Count.EqualTo(2));
    }

    [Test]
    public void ArrangeReplansWhenASlotLandsOnAnEveryNFire()
    {
        var again = OvernightOptimizer.Arrange(Input([Job("A", 30, 10)], every: [TimeSpan.FromMinutes(5)]));

        Assert.That(again.Reshuffled, Is.True);
        Assert.That(again.Slots[0].Start % 5, Is.Not.EqualTo(0));
    }

    [TestCase(0, "00:00")]
    [TestCase(75, "01:15")]
    [TestCase(1441, "00:01")]
    public void FormatsMinutesAsHhMm(int minutes, string text)
    {
        Assert.That(OvernightOptimizer.Format(minutes), Is.EqualTo(text));
        if (minutes < 1440) Assert.That(OvernightOptimizer.Parse(text), Is.EqualTo(minutes));
    }
}
