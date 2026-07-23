using System.Net;
using System.Net.Mail;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Sends one validated plain-text notification through System.Net.Mail and disposes all network/message resources per attempt.
/// </summary>
public sealed class SystemNetSmtpNotificationTransport : ISmtpNotificationTransport
{
    /// <summary>
    /// Creates one SMTP client and mail message, honors cancellation, and propagates transport failures to the caller.
    /// </summary>
    public async Task SendAsync(
        SmtpNotificationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        using var message = new MailMessage
        {
            From = new MailAddress(envelope.From),
            Subject = envelope.Subject,
            Body = envelope.Body,
            IsBodyHtml = false
        };
        foreach (var recipient in envelope.Recipients)
        {
            message.To.Add(new MailAddress(recipient));
        }

        using var client = new SmtpClient(envelope.Host, envelope.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = envelope.UseSsl,
            // The Worker runs as LocalSystem on the server. Blank explicit credentials mean
            // unauthenticated/IP-based relay, never implicit Windows credentials.
            UseDefaultCredentials = false
        };
        if (!string.IsNullOrWhiteSpace(envelope.Username))
        {
            client.Credentials = new NetworkCredential(envelope.Username, envelope.Password);
        }

        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
