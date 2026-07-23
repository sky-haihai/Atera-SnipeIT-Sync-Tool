namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Initializes the disk-backed schedule and owns the independent scheduler lifetime while IPC is hosted separately.
/// </summary>
public sealed class Worker(
    WorkerScheduleManager scheduleManager,
    WorkerScheduler scheduler,
    ILogger<Worker> logger) : BackgroundService
{
    /// <summary>
    /// Starts scheduling even when the Tray is absent; invalid schedule state waits for a later ReloadSchedule command.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var initialSchedule = await scheduleManager
                .InitializeAsync(stoppingToken)
                .ConfigureAwait(false);
            if (!initialSchedule.Applied)
            {
                logger.LogError(
                    "Worker started with an invalid schedule; IPC remains available for status and reload commands.");
            }

            await scheduler.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Worker scheduler stopped unexpectedly.");
        }
    }
}
