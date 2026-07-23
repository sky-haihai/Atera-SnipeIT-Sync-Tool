namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Carries one validated SMTP delivery envelope from the email sender to the transport implementation.
/// </summary>
public sealed class SmtpNotificationEnvelope
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required bool UseSsl { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required string From { get; init; }
    public required IReadOnlyList<string> Recipients { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
}
