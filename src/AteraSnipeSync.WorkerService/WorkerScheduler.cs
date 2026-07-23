using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.Status;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Runs the reloadable unattended schedule, rebuilding runtime configuration for each due trigger and sharing the global run gate.
/// </summary>
public sealed class WorkerScheduler
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly WorkerScheduleManager _scheduleManager;
    private readonly ISyncRunCoordinator _runCoordinator;
    private readonly IWorkerRuntimeFactory _runtimeFactory;
    private readonly ISyncStatusStore _statusStore;
    private readonly INotificationPublisher _notificationPublisher;
    private readonly NotificationEventFilter _notificationFilter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerScheduler> _logger;

    public WorkerScheduler(
        WorkerScheduleManager scheduleManager,
        ISyncRunCoordinator runCoordinator,
        IWorkerRuntimeFactory runtimeFactory,
        ISyncStatusStore statusStore,
        INotificationPublisher notificationPublisher,
        NotificationEventFilter notificationFilter,
        TimeProvider timeProvider,
        ILogger<WorkerScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(scheduleManager);
        ArgumentNullException.ThrowIfNull(runCoordinator);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(statusStore);
        ArgumentNullException.ThrowIfNull(notificationPublisher);
        ArgumentNullException.ThrowIfNull(notificationFilter);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scheduleManager = scheduleManager;
        _runCoordinator = runCoordinator;
        _runtimeFactory = runtimeFactory;
        _statusStore = statusStore;
        _notificationPublisher = notificationPublisher;
        _notificationFilter = notificationFilter;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Polls UTC schedule state at a fixed cadence so arbitrary future occurrences never become long timer delays.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ProcessDueOccurrenceAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await ProcessDueOccurrenceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts one scheduled run without queuing; runtime load, external calls, status save, and notifications stay inside the lease.
    /// </summary>
    public async Task<bool> RunScheduledSyncAsync(CancellationToken cancellationToken)
    {
        var operationId = $"scheduled-{Guid.NewGuid():N}";
        if (!_runCoordinator.TryAcquire(
                operationId,
                WorkerOperationNames.Scheduled,
                _timeProvider.GetUtcNow(),
                out var lease))
        {
            _logger.LogWarning(
                "Skipping scheduled sync because Worker operation {ActiveOperation} is active.",
                _runCoordinator.ActiveOperation);
            return false;
        }

        using (lease)
        {
            var runtime = await _runtimeFactory
                .CreateSyncRuntimeAsync(cancellationToken)
                .ConfigureAwait(false);
            var request = ScheduledSyncRequestFactory.CreateScheduledRequest(runtime.BaseRequest);
            var result = await runtime.Orchestrator
                .RunOnceAsync(request, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await _statusStore.SaveAsync(result, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Scheduled sync completed but its latest status could not be saved.");
            }

            var notification = NotificationRequestFactory.CreateForSyncResult(
                result,
                WorkerOperationNames.Scheduled);
            if (_notificationFilter.ShouldPublish(runtime.NotificationConfig, notification))
            {
                await _notificationPublisher
                    .PublishAsync(notification, runtime.NotificationConfig, cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }
    }

    /// <summary>
    /// Claims a due occurrence before execution; persistence failures leave it unclaimed for a later poll.
    /// </summary>
    private async Task ProcessDueOccurrenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var claimed = await _scheduleManager
                .ClaimDueOccurrenceAsync(_timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (claimed)
            {
                await RunScheduledSyncAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected scheduled trigger failure; the scheduler remains active.");
        }
    }
}
