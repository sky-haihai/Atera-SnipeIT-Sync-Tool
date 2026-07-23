namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Calculates future scheduler run times in the configured local time zone without performing any sync work.
/// </summary>
public sealed class ScheduleCalculator
{
    /// <summary>
    /// Returns the next UTC run time after the supplied UTC instant, or null when scheduling is disabled.
    /// </summary>
    public DateTimeOffset? GetNextRunUtc(
        SyncScheduleOptions options,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        if (!options.Enabled)
        {
            return null;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var normalizedNowUtc = nowUtc.ToUniversalTime();
        var localNow = TimeZoneInfo.ConvertTime(normalizedNowUtc, timeZone);
        var runTimes = options.RunTimes.Order().ToList();

        for (var dayOffset = 0; dayOffset <= 366 * 2; dayOffset++)
        {
            var localDate = DateOnly.FromDateTime(localNow.Date).AddDays(dayOffset);
            if (!DateMatches(options, localDate))
            {
                continue;
            }

            foreach (var runTime in runTimes)
            {
                var localDateTime = localDate.ToDateTime(runTime, DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(localDateTime))
                {
                    continue;
                }

                var candidateUtc = ToUtc(localDateTime, timeZone);
                if (candidateUtc > normalizedNowUtc)
                {
                    return candidateUtc;
                }
            }
        }

        throw new InvalidOperationException("No future scheduler run could be calculated within two years.");
    }

    /// <summary>
    /// Validates the configured recurrence before it is saved or used by the worker scheduler.
    /// </summary>
    public static void Validate(SyncScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.TimeZoneId))
        {
            throw new ArgumentException("Schedule time zone is required.", nameof(options));
        }

        TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);

        if (options.RunTimes.Count == 0)
        {
            throw new ArgumentException("At least one scheduler run time is required.", nameof(options));
        }

        if (options.RunTimes.Count != options.RunTimes.Distinct().Count())
        {
            throw new ArgumentException("Scheduler run times must be unique.", nameof(options));
        }

        if (options.Frequency == ScheduleFrequency.Weekly && options.DaysOfWeek.Count == 0)
        {
            throw new ArgumentException("Weekly schedules require at least one day of week.", nameof(options));
        }

        if (options.Frequency == ScheduleFrequency.Monthly)
        {
            if (options.DaysOfMonth.Any(day => day is < 1 or > 31))
            {
                throw new ArgumentException("Monthly days must be between 1 and 31.", nameof(options));
            }

            if (options.DaysOfMonth.Count == 0 && !options.RunOnLastDayOfMonth)
            {
                throw new ArgumentException("Monthly schedules require at least one day of month or last-day selection.", nameof(options));
            }
        }
    }

    private static bool DateMatches(SyncScheduleOptions options, DateOnly localDate)
    {
        return options.Frequency switch
        {
            ScheduleFrequency.Daily => true,
            ScheduleFrequency.Weekly => options.DaysOfWeek.Contains(localDate.DayOfWeek),
            ScheduleFrequency.Monthly => MonthlyDateMatches(options, localDate),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unsupported schedule frequency.")
        };
    }

    private static bool MonthlyDateMatches(SyncScheduleOptions options, DateOnly localDate)
    {
        var lastDay = DateTime.DaysInMonth(localDate.Year, localDate.Month);
        return options.DaysOfMonth.Contains(localDate.Day)
            || (options.RunOnLastDayOfMonth && localDate.Day == lastDay);
    }

    private static DateTimeOffset ToUtc(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        if (timeZone.IsAmbiguousTime(localDateTime))
        {
            // The smaller offset maps the repeated local clock value to the later UTC instant.
            var offset = timeZone.GetAmbiguousTimeOffsets(localDateTime).Min();
            return new DateTimeOffset(localDateTime, offset).ToUniversalTime();
        }

        return new DateTimeOffset(localDateTime, timeZone.GetUtcOffset(localDateTime)).ToUniversalTime();
    }
}
