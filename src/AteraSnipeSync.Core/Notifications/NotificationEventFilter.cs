using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Applies notification configuration to decide whether a prepared event should be published.
/// </summary>
public sealed class NotificationEventFilter
{
    /// <summary>
    /// Returns whether the request event is enabled by configuration without invoking any sender.
    /// </summary>
    public bool ShouldPublish(
        NotificationConfig config,
        NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        if (config.OnEvents is null)
        {
            throw new ArgumentException("Notification event subscriptions must not be null.", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            throw new ArgumentException("Notification event type must not be blank.", nameof(request));
        }

        if (!config.Enabled || config.OnEvents.Count == 0)
        {
            return false;
        }

        return config.OnEvents.Any(
            configuredEvent => !string.IsNullOrWhiteSpace(configuredEvent)
                && string.Equals(
                    configuredEvent.Trim(),
                    request.EventType.Trim(),
                    StringComparison.OrdinalIgnoreCase));
    }
}
