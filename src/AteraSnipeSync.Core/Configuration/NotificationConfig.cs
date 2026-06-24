namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Configures whether notification events are enabled and which event names may be published.
/// </summary>
public sealed class NotificationConfig
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<string> OnEvents { get; init; }
    public string? EmailTo { get; init; }
    public string? WebhookUrl { get; init; }
}
