using MindAttic.Scheduler.Core;
using Xunit;

namespace MindAttic.Scheduler.Core.Tests;

public class OvernightOptimizerTests
{
    private static OptimizerJob Job(string name, int minutes, int? current = null) =>
        new(name, TimeSpan.FromMinutes(minutes), "estimate", current);

    private static OptimizerInput Input(IReadOnlyList<OptimizerJob> jobs, IReadOnlyList<PinnedBlock>? pinned = null,
        IReadOnlyList<TimeSpan>? every = null, int windowEnd = 480, int gap = 5) =>
        new(jobs, pinned ?? [], every ?? [], 0, windowEnd, gap);

    [Fact]
    public void PlacesHeaviestFirstFromMidnight()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 10), Job("B", 60), Job("C", 30)]));

        Assert.Equal(["B", "C", "A"], plan.Slots.Select(s => s.Name));
        Assert.Equal([0, 65, 100], plan.Slots.Select(s => s.Start));
        Assert.True(plan.Fits);
        Assert.Equal(110, plan.PredictedFinish);
    }

    [Fact]
    public void KeepsTheGapBetweenEveryPairOfJobs()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 7), Job("B", 13), Job("C", 21), Job("D", 2)], gap: 5));

        var ordered = plan.Slots.OrderBy(s => s.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].Start >= ordered[i - 1].End + 5, $"{ordered[i].Name} starts too soon after {ordered[i - 1].Name}");
    }

    [Fact]
    public void GoesAroundAPinnedJob()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("Big", 60), Job("Small", 10)],
            pinned: [new PinnedBlock("Pinned", 30, TimeSpan.FromMinutes(20))]));

        var big = plan.Slots.Single(s => s.Name == "Big");
        // Big can't finish (plus the gap) before 00:30, so it goes after the pinned job's end plus the gap.
        Assert.Equal(55, big.Start);
        foreach (var s in plan.Slots)
            Assert.False(s.Start < 50 && s.End > 30, $"{s.Name} overlaps the pinned job");
    }

    [Fact]
    public void NeverStartsOnAMinuteAnEveryNJobFires()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 60), Job("B", 30), Job("C", 15), Job("D", 4)],
            every: [TimeSpan.FromMinutes(5), TimeSpan.FromHours(1)]));

        Assert.All(plan.Slots, s => Assert.NotEqual(0, s.Start % 5));
        Assert.Equal(1, plan.Slots[0].Start);
    }

    [Fact]
    public void NoTwoJobsShareAStartMinute()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 1), Job("B", 1), Job("C", 1)], gap: 0));

        Assert.Equal(plan.Slots.Count, plan.Slots.Select(s => s.Start).Distinct().Count());
    }

    [Fact]
    public void ShrinksTheGapBeforeGivingUp()
    {
        // 48 + 5 + 48 = 101 > 100, but 48 + 4 + 48 = 100 fits.
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 48), Job("B", 48)], windowEnd: 100, gap: 5));

        Assert.True(plan.Fits);
        Assert.Equal(4, plan.GapMinutes);
        Assert.Equal(100, plan.PredictedFinish);
    }

    [Fact]
    public void ReportsTheOverrunAndDropsNoJobWhenItCannotFit()
    {
        var plan = OvernightOptimizer.Optimize(Input([Job("A", 300), Job("B", 300)]));

        Assert.False(plan.Fits);
        Assert.Equal(2, plan.Slots.Count);
        Assert.Equal(1, plan.GapMinutes);
        Assert.Equal(TimeSpan.FromMinutes(601 - 480), plan.Overrun);
        Assert.True(plan.Slots.Single(s => s.Name == "B").Overruns);
        Assert.False(plan.Slots.Single(s => s.Name == "A").Overruns);
    }

    [Fact]
    public void ArrangeKeepsASoundPlan()
    {
        var first = OvernightOptimizer.Optimize(Input([Job("A", 10), Job("B", 60), Job("C", 30)]));
        var current = first.Slots.Select(s => Job(s.Name, (int)s.Duration.TotalMinutes, s.Start)).ToList();

        var again = OvernightOptimizer.Arrange(Input(current));

        Assert.False(again.Reshuffled);
        Assert.Empty(again.Reasons);
        Assert.Equal(first.Slots.ToDictionary(s => s.Name, s => s.Start), again.Slots.ToDictionary(s => s.Name, s => s.Start));
    }

    [Fact]
    public void ArrangeKeepsASoundPlanEvenWhenOptimizeWouldOrderItDifferently()
    {
        // Light job first is sound (no collision, fits), so plain arrange leaves it.
        var again = OvernightOptimizer.Arrange(Input([Job("Light", 5, 1), Job("Heavy", 60, 20)]));

        Assert.False(again.Reshuffled);
        Assert.Equal(1, again.Slots.Single(s => s.Name == "Light").Start);
    }

    [Theory]
    [InlineData(null, "has no slot")]
    [InlineData(10, "both start at")]
    [InlineData(475, "would finish")]
    [InlineData(15, "starts before")]
    public void ArrangeReplansWhenSomethingIsWrong(int? bStart, string reason)
    {
        var again = OvernightOptimizer.Arrange(Input([Job("A", 30, 10), Job("B", 30, bStart)]));

        Assert.True(again.Reshuffled);
        Assert.Contains(again.Reasons, r => r.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(2, again.Slots.Count);
    }

    [Fact]
    public void ArrangeReplansWhenASlotLandsOnAnEveryNFire()
    {
        var again = OvernightOptimizer.Arrange(Input([Job("A", 30, 10)], every: [TimeSpan.FromMinutes(5)]));

        Assert.True(again.Reshuffled);
        Assert.NotEqual(0, again.Slots[0].Start % 5);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(75, "01:15")]
    [InlineData(1441, "00:01")]
    public void FormatsMinutesAsHhMm(int minutes, string text)
    {
        Assert.Equal(text, OvernightOptimizer.Format(minutes));
        if (minutes < 1440) Assert.Equal(minutes, OvernightOptimizer.Parse(text));
    }
}
