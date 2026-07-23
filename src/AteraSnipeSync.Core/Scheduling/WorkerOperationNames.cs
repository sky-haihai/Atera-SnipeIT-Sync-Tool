namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Provides stable operation names used by the coordinator, status responses, logs, and Tray UI.
/// </summary>
public static class WorkerOperationNames
{
    public const string Scheduled = "scheduled";
    public const string ConnectionTest = "connection-test";
    public const string NotificationTest = "notification-test";
    public const string Preview = "preview";
    public const string SyncNow = "sync-now";
}
