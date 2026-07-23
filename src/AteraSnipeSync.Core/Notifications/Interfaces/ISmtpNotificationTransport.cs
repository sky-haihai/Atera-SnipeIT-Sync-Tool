namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Abstracts the final SMTP network send so email composition is testable without contacting a real server.
/// </summary>
public interface ISmtpNotificationTransport
{
    Task SendAsync(
        SmtpNotificationEnvelope envelope,
        CancellationToken cancellationToken);
}
