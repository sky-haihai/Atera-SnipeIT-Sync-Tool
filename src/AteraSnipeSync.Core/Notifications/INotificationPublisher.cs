namespace AteraSnipeSync.Core.Notifications;

public interface INotificationPublisher
{
    Task PublishAsync(
        NotificationRequest request,
        CancellationToken cancellationToken);
}
