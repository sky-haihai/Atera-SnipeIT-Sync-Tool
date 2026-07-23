namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Reports one notification channel test without exposing its address, endpoint, credentials, or raw response.
/// </summary>
public sealed class NotificationChannelTestResult
{
    public required bool Configured { get; init; }
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Groups the independent Email and Webhook outcomes returned by one Worker notification test.
/// </summary>
public sealed class NotificationTestResult
{
    public required NotificationChannelTestResult Email { get; init; }
    public required NotificationChannelTestResult Webhook { get; init; }
}
