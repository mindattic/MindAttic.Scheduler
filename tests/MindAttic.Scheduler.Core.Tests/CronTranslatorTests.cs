using MindAttic.Scheduler.Core;
using Xunit;

namespace MindAttic.Scheduler.Core.Tests;

public class CronTranslatorTests
{
    [Theory]
    [InlineData("*/5 * * * *", "00:05:00")]
    [InlineData("*/30 * * * *", "00:30:00")]
    public void ParsesAnEveryNMinutesExpression(string cron, string expectedEvery)
    {
        Assert.True(CronTranslator.TryParse(cron, out var schedule, out var error));
        Assert.Null(error);
        Assert.Single(schedule);
        Assert.Equal(expectedEvery, schedule[0].Every);
    }

    [Fact]
    public void ParsesAnEveryNHoursExpression()
    {
        Assert.True(CronTranslator.TryParse("0 */2 * * *", out var schedule, out var error));
        Assert.Null(error);
        Assert.Equal("02:00:00", schedule[0].Every);
    }

    [Fact]
    public void ParsesAFixedDailyTime()
    {
        Assert.True(CronTranslator.TryParse("15 2 * * *", out var schedule, out var error));
        Assert.Null(error);
        Assert.Equal("02:15", schedule[0].Daily);
        Assert.Null(schedule[0].DaysOfWeek);
    }

    [Fact]
    public void ParsesAFixedTimeWithDaysOfWeek()
    {
        Assert.True(CronTranslator.TryParse("0 9 * * 1,2,3,4,5", out var schedule, out var error));
        Assert.Null(error);
        Assert.Equal("09:00", schedule[0].Daily);
        Assert.NotNull(schedule[0].DaysOfWeek);
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri"], schedule[0].DaysOfWeek!);
    }

    [Theory]
    [InlineData("0 9 1 * *")]   // day-of-month
    [InlineData("0 9 * 6 *")]   // month
    [InlineData("*/5 * * * * *")] // 6 fields
    [InlineData("not a cron")]
    public void RejectsWhatTheTriggerModelCannotRepresent(string cron)
    {
        Assert.False(CronTranslator.TryParse(cron, out var schedule, out var error));
        Assert.Empty(schedule);
        Assert.NotNull(error);
    }

    [Fact]
    public void RoundTripsDailyWithWeekdaysThroughCronText()
    {
        var schedule = new[] { new ScheduleEntry { Daily = "09:00", DaysOfWeek = ["Mon", "Tue", "Wed", "Thu", "Fri"] } };
        var cron = CronTranslator.ToCronText(schedule);
        Assert.True(CronTranslator.TryParse(cron, out var reparsed, out var error));
        Assert.Null(error);
        Assert.Equal("09:00", reparsed[0].Daily);
        Assert.Equal(schedule[0].DaysOfWeek!, reparsed[0].DaysOfWeek!);
    }

    [Fact]
    public void AtLogonHasNoCronText() =>
        Assert.Equal("", CronTranslator.ToCronText([new ScheduleEntry { AtLogon = true }]));
}
