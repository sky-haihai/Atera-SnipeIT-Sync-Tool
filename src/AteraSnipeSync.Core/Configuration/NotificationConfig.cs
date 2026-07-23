namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Configures whether notification events are enabled and which event names may be published.
/// </summary>
public sealed class NotificationConfig
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<string> OnEvents { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public bool SmtpUseSsl { get; init; } = true;
    public string? SmtpUsername { get; init; }
    public string? SmtpPassword { get; init; }
    public string? EmailFrom { get; init; }
    public string? EmailTo { get; init; }
    public WebhookPayloadFormat WebhookPayloadFormat { get; init; } = WebhookPayloadFormat.TeamsAdaptiveCard;
    public string? WebhookUrl { get; init; }
}
