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
    [InlineData("*/45 * * * *")]  // really :00 and :45, not every 45 minutes (M.SCHD-T41)
    [InlineData("0 */5 * * *")]   // really 0,5,10,15,20h, not every 5 hours
    [InlineData("*/0 * * * *")]
    [InlineData("*/99999999999 * * * *")]
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

    [Theory]
    [InlineData("00:15:00", "*/15 * * * *")]
    [InlineData("02:00:00", "0 */2 * * *")]
    [InlineData("01:30:00", "")]   // no exact cron step (M.SCHD-T41)
    [InlineData("00:45:00", "")]
    [InlineData("05:00:00", "")]
    public void IntervalCronTextIsExactOrEmpty(string every, string expected) =>
        Assert.Equal(expected, CronTranslator.ToCronText([new ScheduleEntry { Every = every }]));

    [Fact]
    public void AtLogonHasNoCronText() =>
        Assert.Equal("", CronTranslator.ToCronText([new ScheduleEntry { AtLogon = true }]));
}
