using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Runs unattended sync jobs on the configured schedule, saves final status, and prevents overlapping executions when enabled.
/// </summary>
public sealed class SyncScheduler : ISyncScheduler
{
    private readonly ISyncOrchestrator _orchestrator;
    private readonly ISyncStatusStore _statusStore;
    private readonly INotificationPublisher _notificationPublisher;
    private readonly ScheduleCalculator _scheduleCalculator;
    private readonly SyncScheduleOptions _scheduleOptions;
    private readonly SyncRunRequest _baseRequest;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncScheduler> _logger;
    private int _isRunning;

    public SyncScheduler(
        ISyncOrchestrator orchestrator,
        ISyncStatusStore statusStore,
        INotificationPublisher notificationPublisher,
        ScheduleCalculator scheduleCalculator,
        SyncScheduleOptions scheduleOptions,
        SyncRunRequest baseRequest,
        TimeProvider timeProvider,
        ILogger<SyncScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(statusStore);
        ArgumentNullException.ThrowIfNull(notificationPublisher);
        ArgumentNullException.ThrowIfNull(scheduleCalculator);
        ArgumentNullException.ThrowIfNull(scheduleOptions);
        ArgumentNullException.ThrowIfNull(baseRequest);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestrator = orchestrator;
        _statusStore = statusStore;
        _notificationPublisher = notificationPublisher;
        _scheduleCalculator = scheduleCalculator;
        _scheduleOptions = scheduleOptions;
        _baseRequest = baseRequest;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Starts the unattended scheduling loop and keeps running until cancellation is requested.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var nextRunUtc = _scheduleCalculator.GetNextRunUtc(_scheduleOptions, _timeProvider.GetUtcNow());
            if (nextRunUtc is null)
            {
                _logger.LogInformation("Sync scheduler is disabled.");
                return;
            }

            var delay = nextRunUtc.Value - _timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            await RunScheduledSyncAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes one scheduled run, skipping it when overlap prevention is enabled and a prior run is still active.
    /// </summary>
    public async Task<bool> RunScheduledSyncAsync(CancellationToken cancellationToken)
    {
        if (_scheduleOptions.PreventOverlappingRuns
            && Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            _logger.LogWarning("Skipping scheduled sync because the previous run is still active.");
            return false;
        }

        if (!_scheduleOptions.PreventOverlappingRuns)
        {
            Interlocked.Exchange(ref _isRunning, 1);
        }

        try
        {
            var request = ScheduledSyncRequestFactory.CreateScheduledRequest(_baseRequest);
            var result = await _orchestrator.RunOnceAsync(request, cancellationToken).ConfigureAwait(false);
            await _statusStore.SaveAsync(result, cancellationToken).ConfigureAwait(false);

            await _notificationPublisher.PublishAsync(
                new NotificationRequest
                {
                    EventType = "ScheduledSyncCompleted",
                    Severity = result.Success ? "Information" : "Error",
                    Subject = result.Success ? "Scheduled sync completed" : "Scheduled sync failed",
                    Message = result.Success
                        ? "The scheduled Atera to Snipe-IT sync completed."
                        : "The scheduled Atera to Snipe-IT sync completed with failures.",
                    SyncResult = result
                },
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
