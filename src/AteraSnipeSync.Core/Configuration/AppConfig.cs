namespace AteraSnipeSync.Core.Configuration;

public sealed class AppConfig
{
    public required AteraConfig Atera { get; init; }
    public required SnipeItConfig SnipeIt { get; init; }
    public required SyncConfig Sync { get; init; }
    public required NotificationConfig Notifications { get; init; }
}
