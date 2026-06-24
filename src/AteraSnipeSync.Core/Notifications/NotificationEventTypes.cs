namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Defines stable notification event names used by configuration, tests, and publishers.
/// </summary>
public static class NotificationEventTypes
{
    public const string ScheduledSyncCompleted = "ScheduledSyncCompleted";
    public const string ScheduledSyncFailed = "ScheduledSyncFailed";
    public const string ManualSyncCompleted = "ManualSyncCompleted";
    public const string ManualSyncFailed = "ManualSyncFailed";
    public const string ManualPreviewCompleted = "ManualPreviewCompleted";
    public const string ManualPreviewFailed = "ManualPreviewFailed";
    public const string SyncCompleted = "SyncCompleted";
    public const string SyncFailed = "SyncFailed";
}
