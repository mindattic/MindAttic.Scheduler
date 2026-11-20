using MindAttic.Scheduler.Core;
using NUnit.Framework;

namespace MindAttic.Scheduler.Core.Tests;

public class CronTranslatorTests
{
    [TestCase("*/5 * * * *", "00:05:00")]
    [TestCase("*/30 * * * *", "00:30:00")]
    public void ParsesAnEveryNMinutesExpression(string cron, string expectedEvery)
    {
        Assert.That(CronTranslator.TryParse(cron, out var schedule, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(schedule, Has.Count.EqualTo(1));
        Assert.That(schedule[0].Every, Is.EqualTo(expectedEvery));
    }

    [Test]
    public void ParsesAnEveryNHoursExpression()
    {
        Assert.That(CronTranslator.TryParse("0 */2 * * *", out var schedule, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(schedule[0].Every, Is.EqualTo("02:00:00"));
    }

    [Test]
    public void ParsesAFixedDailyTime()
    {
        Assert.That(CronTranslator.TryParse("15 2 * * *", out var schedule, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(schedule[0].Daily, Is.EqualTo("02:15"));
        Assert.That(schedule[0].DaysOfWeek, Is.Null);
    }

    [Test]
    public void ParsesAFixedTimeWithDaysOfWeek()
    {
        Assert.That(CronTranslator.TryParse("0 9 * * 1,2,3,4,5", out var schedule, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(schedule[0].Daily, Is.EqualTo("09:00"));
        Assert.That(schedule[0].DaysOfWeek, Is.EqualTo(new[] { "Mon", "Tue", "Wed", "Thu", "Fri" }));
    }

    [TestCase("0 9 1 * *")]   // day-of-month
    [TestCase("0 9 * 6 *")]   // month
    [TestCase("*/5 * * * * *")] // 6 fields
    [TestCase("not a cron")]
    [TestCase("*/45 * * * *")]  // really :00 and :45, not every 45 minutes (M.SCHD-T41)
    [TestCase("0 */5 * * *")]   // really 0,5,10,15,20h, not every 5 hours
    [TestCase("*/0 * * * *")]
    [TestCase("*/99999999999 * * * *")]
    public void RejectsWhatTheTriggerModelCannotRepresent(string cron)
    {
        Assert.That(CronTranslator.TryParse(cron, out var schedule, out var error), Is.False);
        Assert.That(schedule, Is.Empty);
        Assert.That(error, Is.Not.Null);
    }

    [Test]
    public void RoundTripsDailyWithWeekdaysThroughCronText()
    {
        var schedule = new[] { new ScheduleEntry { Daily = "09:00", DaysOfWeek = ["Mon", "Tue", "Wed", "Thu", "Fri"] } };
        var cron = CronTranslator.ToCronText(schedule);
        Assert.That(CronTranslator.TryParse(cron, out var reparsed, out var error), Is.True);
        Assert.That(error, Is.Null);
        Assert.That(reparsed[0].Daily, Is.EqualTo("09:00"));
        Assert.That(reparsed[0].DaysOfWeek, Is.EqualTo(schedule[0].DaysOfWeek));
    }

    [TestCase("00:15:00", "*/15 * * * *")]
    [TestCase("02:00:00", "0 */2 * * *")]
    [TestCase("01:30:00", "")]   // no exact cron step (M.SCHD-T41)
    [TestCase("00:45:00", "")]
    [TestCase("05:00:00", "")]
    public void IntervalCronTextIsExactOrEmpty(string every, string expected) =>
        Assert.That(CronTranslator.ToCronText([new ScheduleEntry { Every = every }]), Is.EqualTo(expected));

    [Test]
    public void AtLogonHasNoCronText() =>
        Assert.That(CronTranslator.ToCronText([new ScheduleEntry { AtLogon = true }]), Is.EqualTo(""));
}
