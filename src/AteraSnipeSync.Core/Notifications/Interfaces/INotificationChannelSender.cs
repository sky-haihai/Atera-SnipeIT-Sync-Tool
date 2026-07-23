using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Defines one concrete notification transport that validates and sends a prepared request using one configuration snapshot.
/// </summary>
public interface INotificationChannelSender
{
    string ChannelName { get; }

    bool IsConfigured(NotificationConfig config);

    Task SendAsync(
        NotificationConfig config,
        NotificationRequest request,
        CancellationToken cancellationToken);
}
