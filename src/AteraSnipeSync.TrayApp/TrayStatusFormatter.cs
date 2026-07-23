using AteraSnipeSync.Core.Runtime.Ipc;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Converts Worker and schedule state into bounded operator-facing Dashboard text.
/// </summary>
internal static class TrayStatusFormatter
{
    public static string FormatWorkerStatus(WorkerStatusSnapshot? status, bool workerOnline)
    {
        if (!workerOnline)
        {
            return "Offline";
        }

        return status?.IsRunning == true
            ? $"Online — {FormatOperation(status.ActiveOperation)}"
            : "Online — Idle";
    }

    public static string FormatNextRun(DateTimeOffset? nextRunUtc, TimeZoneInfo localTimeZone)
    {
        return nextRunUtc is null
            ? "Not scheduled"
            : TimeZoneInfo.ConvertTime(nextRunUtc.Value, localTimeZone).ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    public static string FormatOperation(string? operation)
    {
        return operation switch
        {
            "scheduled" => "Scheduled",
            "connection-test" => "Connection Test",
            "preview" => "Preview",
            "sync-now" => "Sync Now",
            null or "" => "Idle",
            _ => operation
        };
    }
}
