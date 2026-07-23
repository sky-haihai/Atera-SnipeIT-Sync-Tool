using System.Net.Mail;
using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Validates SMTP/email configuration and composes one plain-text envelope without owning network transport policy.
/// </summary>
public sealed class EmailNotificationSender(ISmtpNotificationTransport transport) : INotificationChannelSender
{
    public string ChannelName => "Email";

    public bool IsConfigured(NotificationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return HasValue(config.SmtpHost)
            || HasValue(config.SmtpUsername)
            || HasValue(config.SmtpPassword)
            || HasValue(config.EmailFrom)
            || HasValue(config.EmailTo);
    }

    /// <summary>
    /// Validates host, port, optional credentials and mail addresses before invoking exactly one SMTP transport send.
    /// </summary>
    public Task SendAsync(
        NotificationConfig config,
        NotificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var host = Require(config.SmtpHost, nameof(config.SmtpHost));
        if (config.SmtpPort is < 1 or > 65535)
        {
            throw new ArgumentException("SMTP port must be between 1 and 65535.", nameof(config));
        }

        var usernameConfigured = HasValue(config.SmtpUsername);
        var passwordConfigured = HasValue(config.SmtpPassword);
        if (usernameConfigured != passwordConfigured)
        {
            throw new ArgumentException("SMTP username and password must be configured together.", nameof(config));
        }

        var from = ValidateAddress(Require(config.EmailFrom, nameof(config.EmailFrom)), nameof(config.EmailFrom));
        var recipients = SplitRecipients(Require(config.EmailTo, nameof(config.EmailTo)));
        var envelope = new SmtpNotificationEnvelope
        {
            Host = host,
            Port = config.SmtpPort,
            UseSsl = config.SmtpUseSsl,
            Username = config.SmtpUsername?.Trim(),
            Password = config.SmtpPassword,
            From = from,
            Recipients = recipients,
            Subject = Require(request.Subject, nameof(request.Subject)),
            Body = Require(request.Message, nameof(request.Message))
        };
        return transport.SendAsync(envelope, cancellationToken);
    }

    private static IReadOnlyList<string> SplitRecipients(string value)
    {
        var recipients = value
            .Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(recipient => ValidateAddress(recipient, nameof(NotificationConfig.EmailTo)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return recipients.Length > 0
            ? recipients
            : throw new ArgumentException("At least one email recipient is required.", nameof(value));
    }

    private static string ValidateAddress(string value, string parameterName)
    {
        try
        {
            return new MailAddress(value).Address;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"{parameterName} must contain valid email addresses.", parameterName, exception);
        }
    }

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameterName} is required.", parameterName)
            : value.Trim();

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
}
