using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Core.Notifications;

public sealed class NotificationRequest
{
    public required string EventType { get; init; }
    public required string Severity { get; init; }
    public required string Subject { get; init; }
    public required string Message { get; init; }
    public required SyncRunResult? SyncResult { get; init; }
}
