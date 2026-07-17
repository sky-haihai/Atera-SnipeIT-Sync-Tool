namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Hosts the configured sync scheduler and keeps configuration failures from issuing any API requests.
/// </summary>
public sealed class Worker(
    WorkerRuntimeFactory runtimeFactory,
    ILogger<Worker> logger) : BackgroundService
{
    /// <summary>
    /// Builds the runtime once at service start, then delegates lifetime and cancellation to the scheduler.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var scheduler = await runtimeFactory.CreateSchedulerAsync(stoppingToken).ConfigureAwait(false);
            await scheduler.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                exception,
                "Worker runtime configuration is invalid; no sync was started. Correct appsettings.local.json/environment variables and restart the service.");
        }
    }
}
