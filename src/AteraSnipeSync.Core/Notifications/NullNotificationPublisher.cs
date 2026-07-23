using Microsoft.Extensions.Logging;
using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Accepts notification requests without sending external messages, providing a safe default publisher.
/// </summary>
public sealed class NullNotificationPublisher : INotificationPublisher
{
    private readonly ILogger<NullNotificationPublisher> logger;

    public NullNotificationPublisher(
        ILogger<NullNotificationPublisher> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Validates and records that a notification was accepted without contacting external systems.
    /// </summary>
    public Task PublishAsync(
        NotificationRequest request,
        NotificationConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        ValidateRequiredText(request.EventType, nameof(request.EventType));
        ValidateRequiredText(request.Severity, nameof(request.Severity));
        ValidateRequiredText(request.Subject, nameof(request.Subject));
        ValidateRequiredText(request.Message, nameof(request.Message));

        cancellationToken.ThrowIfCancellationRequested();

        logger.LogDebug(
            "Null notification publisher accepted event {EventType} with severity {Severity} and subject {Subject}.",
            request.EventType,
            request.Severity,
            request.Subject);

        return Task.CompletedTask;
    }

    private static void ValidateRequiredText(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Notification request text fields must not be blank.", parameterName);
        }
    }
}
