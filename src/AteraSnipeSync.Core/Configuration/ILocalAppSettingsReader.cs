using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Exposes the read-only local configuration boundary used by WorkerService so it cannot mutate settings.
/// </summary>
public interface ILocalAppSettingsReader
{
    Task<ManualSyncSettings?> LoadWorkerSyncSettingsAsync(CancellationToken cancellationToken);
    Task<SyncScheduleOptions?> LoadSyncScheduleOptionsAsync(CancellationToken cancellationToken);
    Task<bool> LoadSyncDryRunAsync(CancellationToken cancellationToken);
    Task<NotificationConfig> LoadNotificationConfigAsync(CancellationToken cancellationToken);
}
