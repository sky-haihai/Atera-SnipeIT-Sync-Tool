using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Carries one prepared notification event and optional sync result context to a publisher.
/// </summary>
public sealed class NotificationRequest
{
    public required string EventType { get; init; }
    public required string Severity { get; init; }
    public required string Subject { get; init; }
    public required string Message { get; init; }
    public required SyncRunResult? SyncResult { get; init; }
}
