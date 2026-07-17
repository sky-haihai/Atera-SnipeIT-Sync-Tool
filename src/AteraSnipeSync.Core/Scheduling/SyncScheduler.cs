using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Configuration;
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
    private readonly NotificationConfig _notificationConfig;
    private readonly NotificationEventFilter _notificationEventFilter;
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
        : this(
            orchestrator,
            statusStore,
            notificationPublisher,
            new NotificationConfig
            {
                Enabled = true,
                OnEvents =
                [
                    NotificationEventTypes.ScheduledSyncCompleted,
                    NotificationEventTypes.ScheduledSyncFailed
                ]
            },
            new NotificationEventFilter(),
            scheduleCalculator,
            scheduleOptions,
            baseRequest,
            timeProvider,
            logger)
    {
    }

    public SyncScheduler(
        ISyncOrchestrator orchestrator,
        ISyncStatusStore statusStore,
        INotificationPublisher notificationPublisher,
        NotificationConfig notificationConfig,
        NotificationEventFilter notificationEventFilter,
        ScheduleCalculator scheduleCalculator,
        SyncScheduleOptions scheduleOptions,
        SyncRunRequest baseRequest,
        TimeProvider timeProvider,
        ILogger<SyncScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(statusStore);
        ArgumentNullException.ThrowIfNull(notificationPublisher);
        ArgumentNullException.ThrowIfNull(notificationConfig);
        ArgumentNullException.ThrowIfNull(notificationEventFilter);
        ArgumentNullException.ThrowIfNull(scheduleCalculator);
        ArgumentNullException.ThrowIfNull(scheduleOptions);
        ArgumentNullException.ThrowIfNull(baseRequest);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestrator = orchestrator;
        _statusStore = statusStore;
        _notificationPublisher = notificationPublisher;
        _notificationConfig = notificationConfig;
        _notificationEventFilter = notificationEventFilter;
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

            try
            {
                await RunScheduledSyncAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected scheduled sync loop failure; the scheduler will continue.");
            }
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
            try
            {
                var request = ScheduledSyncRequestFactory.CreateScheduledRequest(_baseRequest);
                var result = await _orchestrator.RunOnceAsync(request, cancellationToken).ConfigureAwait(false);
                await _statusStore.SaveAsync(result, CancellationToken.None).ConfigureAwait(false);

                var notification = NotificationRequestFactory.CreateForSyncResult(result, "scheduled");
                if (_notificationEventFilter.ShouldPublish(_notificationConfig, notification))
                {
                    await _notificationPublisher.PublishAsync(notification, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Scheduled sync failed outside the orchestrated pipeline; the scheduler remains active.");
                return false;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }
}
