using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.Tests.Scheduling;

/// <summary>
/// Verifies scheduler recurrence calculations without launching background services.
/// </summary>
public sealed class ScheduleCalculatorTests
{
    [Fact]
    public void GetNextRunUtc_ReturnsNextDailyTime()
    {
        var calculator = new ScheduleCalculator();

        var result = calculator.GetNextRunUtc(
            CreateOptions(ScheduleFrequency.Daily, runTimes: [new TimeOnly(9, 0), new TimeOnly(15, 30)]),
            new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 6, 18, 15, 30, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void GetNextRunUtc_ReturnsNextWeeklyDay()
    {
        var calculator = new ScheduleCalculator();

        var result = calculator.GetNextRunUtc(
            CreateOptions(
                ScheduleFrequency.Weekly,
                runTimes: [new TimeOnly(8, 0)],
                daysOfWeek: [DayOfWeek.Monday]),
            new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 6, 22, 8, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void GetNextRunUtc_SkipsMonthlyDay_WhenMonthDoesNotContainThatDay()
    {
        var calculator = new ScheduleCalculator();

        var result = calculator.GetNextRunUtc(
            CreateOptions(
                ScheduleFrequency.Monthly,
                runTimes: [new TimeOnly(2, 0)],
                daysOfMonth: [31]),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 3, 31, 2, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void GetNextRunUtc_ReturnsMonthlyLastDay_WhenConfigured()
    {
        var calculator = new ScheduleCalculator();

        var result = calculator.GetNextRunUtc(
            CreateOptions(
                ScheduleFrequency.Monthly,
                runTimes: [new TimeOnly(2, 0)],
                runOnLastDayOfMonth: true),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 2, 28, 2, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void GetNextRunUtc_SupportsMonthlyIntervalsLongerThanTaskDelayLimit()
    {
        var calculator = new ScheduleCalculator();
        var now = new DateTimeOffset(2026, 3, 31, 9, 0, 1, TimeSpan.Zero);

        var result = calculator.GetNextRunUtc(
            CreateOptions(
                ScheduleFrequency.Monthly,
                runTimes: [new TimeOnly(9, 0)],
                daysOfMonth: [31]),
            now);

        Assert.Equal(new DateTimeOffset(2026, 5, 31, 9, 0, 0, TimeSpan.Zero), result);
        Assert.True(result - now > TimeSpan.FromDays(60));
    }

    [Theory]
    [InlineData("2026-03-07T16:00:00Z")]
    [InlineData("2026-03-08T15:00:00Z")]
    public void GetNextRunUtc_KeepsEdmontonNineAmAcrossDaylightSaving(string expectedUtc)
    {
        var calculator = new ScheduleCalculator();
        var expected = DateTimeOffset.Parse(expectedUtc);

        var result = calculator.GetNextRunUtc(
            CreateOptions(
                ScheduleFrequency.Daily,
                runTimes: [new TimeOnly(9, 0)],
                timeZoneId: "America/Edmonton"),
            expected.AddMinutes(-1));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetNextRunUtc_ChoosesLaterUtcOccurrenceForRepeatedLocalTime_OnlyOnce()
    {
        var calculator = new ScheduleCalculator();
        var options = CreateOptions(
            ScheduleFrequency.Daily,
            runTimes: [new TimeOnly(1, 30)],
            timeZoneId: "America/Edmonton");

        var repeatedOccurrence = calculator.GetNextRunUtc(
            options,
            DateTimeOffset.Parse("2026-11-01T06:00:00Z"));
        var followingOccurrence = calculator.GetNextRunUtc(
            options,
            DateTimeOffset.Parse("2026-11-01T08:30:00Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-11-01T08:30:00Z"), repeatedOccurrence);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T08:30:00Z"), followingOccurrence);
    }

    [Fact]
    public void GetNextRunUtc_SkipsNonexistentSpringForwardLocalTime()
    {
        var calculator = new ScheduleCalculator();

        var result = calculator.GetNextRunUtc(
            CreateOptions(
                ScheduleFrequency.Daily,
                runTimes: [new TimeOnly(2, 30)],
                timeZoneId: "America/Edmonton"),
            DateTimeOffset.Parse("2026-03-08T08:00:00Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-03-09T08:30:00Z"), result);
    }

    [Fact]
    public void Validate_RejectsWeeklyScheduleWithoutDays()
    {
        var options = CreateOptions(ScheduleFrequency.Weekly, runTimes: [new TimeOnly(8, 0)]);

        Assert.Throws<ArgumentException>(() => ScheduleCalculator.Validate(options));
    }

    private static SyncScheduleOptions CreateOptions(
        ScheduleFrequency frequency,
        IReadOnlyList<TimeOnly> runTimes,
        IReadOnlyList<DayOfWeek>? daysOfWeek = null,
        IReadOnlyList<int>? daysOfMonth = null,
        bool runOnLastDayOfMonth = false,
        string timeZoneId = "UTC")
    {
        return new SyncScheduleOptions
        {
            Enabled = true,
            Frequency = frequency,
            TimeZoneId = timeZoneId,
            RunTimes = runTimes,
            DaysOfWeek = daysOfWeek ?? [],
            DaysOfMonth = daysOfMonth ?? [],
            RunOnLastDayOfMonth = runOnLastDayOfMonth,
            PreventOverlappingRuns = true
        };
    }
}
