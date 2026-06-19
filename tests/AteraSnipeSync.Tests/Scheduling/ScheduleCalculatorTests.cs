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
        bool runOnLastDayOfMonth = false)
    {
        return new SyncScheduleOptions
        {
            Enabled = true,
            Frequency = frequency,
            TimeZoneId = "UTC",
            RunTimes = runTimes,
            DaysOfWeek = daysOfWeek ?? [],
            DaysOfMonth = daysOfMonth ?? [],
            RunOnLastDayOfMonth = runOnLastDayOfMonth,
            PreventOverlappingRuns = true
        };
    }
}
