using AteraSnipeSync.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Delivers notifications through every configured channel and provides independent sanitized Email/Webhook test outcomes.
/// </summary>
public sealed class CompositeNotificationPublisher : INotificationPublisher, INotificationTester
{
    private readonly IReadOnlyDictionary<string, INotificationChannelSender> _senders;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CompositeNotificationPublisher> _logger;

    public CompositeNotificationPublisher(
        IEnumerable<INotificationChannelSender> senders,
        TimeProvider timeProvider,
        ILogger<CompositeNotificationPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(senders);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _senders = senders.ToDictionary(sender => sender.ChannelName, StringComparer.OrdinalIgnoreCase);
        if (!_senders.ContainsKey("Email") || !_senders.ContainsKey("Webhook"))
        {
            throw new ArgumentException("Email and Webhook notification senders are required.", nameof(senders));
        }

        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Attempts every configured channel, continues after partial failure, and throws one sanitized aggregate result afterward.
    /// </summary>
    public async Task PublishAsync(
        NotificationRequest request,
        NotificationConfig config,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(config);
        var configured = _senders.Values.Where(sender => sender.IsConfigured(config)).ToArray();
        if (configured.Length == 0)
        {
            throw new NotificationDeliveryException("No notification channels are configured.");
        }

        var failed = new List<string>();
        foreach (var sender in configured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await sender.SendAsync(config, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed.Add(sender.ChannelName);
                _logger.LogWarning(
                    "Notification delivery failed for channel {Channel} with failure type {FailureType}.",
                    sender.ChannelName,
                    exception.GetType().Name);
            }
        }

        if (failed.Count > 0)
        {
            throw new NotificationDeliveryException(
                $"Notification delivery failed for: {string.Join(", ", failed)}.");
        }
    }

    /// <summary>
    /// Sends a fixed safe test request to Email and Webhook independently, returning only bounded channel status.
    /// </summary>
    public async Task<NotificationTestResult> TestAsync(
        NotificationConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var request = new NotificationRequest
        {
            EventType = NotificationEventTypes.NotificationTest,
            Severity = "Information",
            Subject = "Atera Snipe-IT Auto Sync test notification",
            Message = $"Notification delivery test generated at {_timeProvider.GetUtcNow():yyyy-MM-dd HH:mm:ss 'UTC'}.",
            Deleted = 0,
            SyncResult = null
        };

        var email = await TestChannelAsync(_senders["Email"], config, request, cancellationToken).ConfigureAwait(false);
        var webhook = await TestChannelAsync(_senders["Webhook"], config, request, cancellationToken).ConfigureAwait(false);
        return new NotificationTestResult { Email = email, Webhook = webhook };
    }

    private async Task<NotificationChannelTestResult> TestChannelAsync(
        INotificationChannelSender sender,
        NotificationConfig config,
        NotificationRequest request,
        CancellationToken cancellationToken)
    {
        if (!sender.IsConfigured(config))
        {
            return Result(configured: false, succeeded: false, $"{sender.ChannelName} is not configured.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await sender.SendAsync(config, request, cancellationToken).ConfigureAwait(false);
            return Result(
                configured: true,
                succeeded: true,
                $"{sender.ChannelName} endpoint accepted the test request; downstream delivery is not confirmed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Notification test failed for channel {Channel} with failure type {FailureType}.",
                sender.ChannelName,
                exception.GetType().Name);
            return Result(configured: true, succeeded: false, $"{sender.ChannelName} test failed. See Worker service logs.");
        }
    }

    private static NotificationChannelTestResult Result(bool configured, bool succeeded, string message)
        => new() { Configured = configured, Succeeded = succeeded, Message = message };

    private static void ValidateRequest(NotificationRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.EventType)
            || string.IsNullOrWhiteSpace(request.Severity)
            || string.IsNullOrWhiteSpace(request.Subject)
            || string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Notification request text fields must not be blank.", nameof(request));
        }
    }
}
