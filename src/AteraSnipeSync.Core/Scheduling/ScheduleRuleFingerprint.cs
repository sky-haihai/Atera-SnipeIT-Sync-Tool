using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Produces a stable rule identity so persisted UTC occurrences cannot be reused after schedule edits.
/// </summary>
public static class ScheduleRuleFingerprint
{
    /// <summary>
    /// Hashes normalized recurrence fields; collection ordering and cosmetic whitespace do not change the identity.
    /// </summary>
    public static string Create(SyncScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ScheduleCalculator.Validate(options);

        var runTimes = string.Join(",", options.RunTimes
            .Order()
            .Select(value => value.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)));
        var daysOfWeek = string.Join(",", options.DaysOfWeek
            .Distinct()
            .OrderBy(value => (int)value)
            .Select(value => ((int)value).ToString(CultureInfo.InvariantCulture)));
        var daysOfMonth = string.Join(",", options.DaysOfMonth
            .Distinct()
            .Order()
            .Select(value => value.ToString(CultureInfo.InvariantCulture)));
        var canonical = string.Join(
            "\n",
            options.Enabled ? "1" : "0",
            options.Frequency.ToString(),
            options.TimeZoneId.Trim(),
            runTimes,
            daysOfWeek,
            daysOfMonth,
            options.RunOnLastDayOfMonth ? "1" : "0",
            options.PreventOverlappingRuns ? "1" : "0");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
