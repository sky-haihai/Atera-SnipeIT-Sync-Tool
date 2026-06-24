namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Publishes a prepared notification request through a concrete sender implementation.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Sends or accepts a validated notification request and propagates cancellation or sender failures.
    /// </summary>
    Task PublishAsync(
        NotificationRequest request,
        CancellationToken cancellationToken);
}
