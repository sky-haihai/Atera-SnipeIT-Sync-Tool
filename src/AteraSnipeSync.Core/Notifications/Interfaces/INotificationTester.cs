using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Sends one explicit test through every configured notification channel and returns independent sanitized outcomes.
/// </summary>
public interface INotificationTester
{
    Task<NotificationTestResult> TestAsync(
        NotificationConfig config,
        CancellationToken cancellationToken);
}
