namespace AteraSnipeSync.Core.Configuration;

public sealed class NotificationConfig
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<string> OnEvents { get; init; }
    public string? EmailTo { get; init; }
    public string? WebhookUrl { get; init; }
}
