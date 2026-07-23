using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Runtime.Ipc;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Dispatches local IPC commands, applies the shared run gate, rebuilds runtime per run, and owns cancellable Tray request state.
/// </summary>
public sealed class WorkerCommandHandler : IWorkerCommandHandler
{
    private static readonly Regex RequestIdPattern = new(
        "^[A-Za-z0-9._-]{1,128}$",
        RegexOptions.CultureInvariant);

    private readonly ISyncRunCoordinator _runCoordinator;
    private readonly WorkerScheduleManager _scheduleManager;
    private readonly IWorkerRuntimeFactory _runtimeFactory;
    private readonly WorkerConnectionTester _connectionTester;
    private readonly ILocalAppSettingsReader _settingsReader;
    private readonly INotificationTester _notificationTester;
    private readonly INotificationPublisher _notificationPublisher;
    private readonly NotificationEventFilter _notificationFilter;
    private readonly ISyncStatusStore _statusStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerCommandHandler> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);

    public WorkerCommandHandler(
        ISyncRunCoordinator runCoordinator,
        WorkerScheduleManager scheduleManager,
        IWorkerRuntimeFactory runtimeFactory,
        WorkerConnectionTester connectionTester,
        ILocalAppSettingsReader settingsReader,
        INotificationTester notificationTester,
        INotificationPublisher notificationPublisher,
        NotificationEventFilter notificationFilter,
        ISyncStatusStore statusStore,
        TimeProvider timeProvider,
        ILogger<WorkerCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(runCoordinator);
        ArgumentNullException.ThrowIfNull(scheduleManager);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(connectionTester);
        ArgumentNullException.ThrowIfNull(settingsReader);
        ArgumentNullException.ThrowIfNull(notificationTester);
        ArgumentNullException.ThrowIfNull(notificationPublisher);
        ArgumentNullException.ThrowIfNull(notificationFilter);
        ArgumentNullException.ThrowIfNull(statusStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _runCoordinator = runCoordinator;
        _scheduleManager = scheduleManager;
        _runtimeFactory = runtimeFactory;
        _connectionTester = connectionTester;
        _settingsReader = settingsReader;
        _notificationTester = notificationTester;
        _notificationPublisher = notificationPublisher;
        _notificationFilter = notificationFilter;
        _statusStore = statusStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WorkerCommandResult> ExecuteAsync(
        WorkerIpcRequest request,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return Error(validationError);
        }

        return request.Command switch
        {
            WorkerIpcCommands.Ping => Completed("Worker is ready."),
            WorkerIpcCommands.GetStatus => await GetStatusAsync(cancellationToken).ConfigureAwait(false),
            WorkerIpcCommands.ReloadSchedule => await ReloadScheduleAsync(cancellationToken).ConfigureAwait(false),
            WorkerIpcCommands.Cancel => Completed(
                TryCancel(request.TargetRequestId!)
                    ? "Cancellation requested."
                    : "No matching active request was found."),
            WorkerIpcCommands.TestConnections => await ExecuteRunAsync(
                request,
                WorkerOperationNames.ConnectionTest,
                progress,
                cancellationToken).ConfigureAwait(false),
            WorkerIpcCommands.TestNotifications => await ExecuteNotificationTestAsync(
                request,
                cancellationToken).ConfigureAwait(false),
            WorkerIpcCommands.PreviewChanges => await ExecuteRunAsync(
                request,
                WorkerOperationNames.Preview,
                progress,
                cancellationToken).ConfigureAwait(false),
            WorkerIpcCommands.SyncNow => await ExecuteRunAsync(
                request,
                WorkerOperationNames.SyncNow,
                progress,
                cancellationToken).ConfigureAwait(false),
            _ => Error("Unknown Worker command.")
        };
    }

    /// <inheritdoc />
    public bool TryCancel(string targetRequestId)
    {
        if (string.IsNullOrWhiteSpace(targetRequestId)
            || !RequestIdPattern.IsMatch(targetRequestId)
            || !_activeRequests.TryGetValue(targetRequestId, out var cancellation))
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task<WorkerCommandResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var latest = await _statusStore.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
        var schedule = _scheduleManager.Current;
        return new WorkerCommandResult
        {
            EventType = WorkerIpcEventTypes.Completed,
            Message = "Worker status returned.",
            WorkerStatus = new WorkerStatusSnapshot
            {
                IsRunning = _runCoordinator.IsRunning,
                ActiveOperation = _runCoordinator.ActiveOperation,
                ActiveStartedUtc = _runCoordinator.ActiveStartedUtc,
                ScheduleConfigurationValid = schedule.ConfigurationValid,
                ScheduleEnabled = schedule.Enabled,
                NextRunUtc = schedule.NextRunUtc,
                ScheduleError = schedule.Error,
                LatestSync = latest
            }
        };
    }

    private async Task<WorkerCommandResult> ReloadScheduleAsync(CancellationToken cancellationToken)
    {
        var result = await _scheduleManager.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return new WorkerCommandResult
        {
            EventType = result.Applied
                ? WorkerIpcEventTypes.Completed
                : WorkerIpcEventTypes.Error,
            Message = result.Message,
            ScheduleReload = result
        };
    }

    private async Task<WorkerCommandResult> ExecuteRunAsync(
        WorkerIpcRequest request,
        string operation,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken hostCancellationToken)
    {
        if (!_runCoordinator.TryAcquire(
                request.RequestId,
                operation,
                _timeProvider.GetUtcNow(),
                out var lease))
        {
            return new WorkerCommandResult
            {
                EventType = WorkerIpcEventTypes.Busy,
                Message = "Worker is busy with another operation.",
                ActiveOperation = _runCoordinator.ActiveOperation
            };
        }

        using (lease)
        using (var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken))
        {
            if (!_activeRequests.TryAdd(request.RequestId, commandCancellation))
            {
                return Error("Duplicate request id.");
            }

            try
            {
                var runtime = await _runtimeFactory
                    .CreateSyncRuntimeAsync(commandCancellation.Token)
                    .ConfigureAwait(false);

                if (operation == WorkerOperationNames.ConnectionTest)
                {
                    var connectionResult = await _connectionTester
                        .TestAllAsync(runtime, progress, commandCancellation.Token)
                        .ConfigureAwait(false);
                    return new WorkerCommandResult
                    {
                        EventType = WorkerIpcEventTypes.Completed,
                        Message = "Connection tests completed.",
                        ConnectionTest = connectionResult
                    };
                }

                var preflightDirectory = operation == WorkerOperationNames.Preview
                    ? LocalAppSettingsStore.GetDefaultManualPreflightDirectory(request.RequestId)
                    : null;
                var syncRequest = operation == WorkerOperationNames.Preview
                    ? ManualSyncRequestFactory.CreatePreviewChangesRequest(
                        runtime.BaseRequest,
                        preflightDirectory!)
                    : ManualSyncRequestFactory.CreateSyncNowRequest(runtime.BaseRequest);
                var result = await runtime.Orchestrator
                    .RunOnceAsync(syncRequest, commandCancellation.Token, progress)
                    .ConfigureAwait(false);
                var statusSaved = await TrySaveStatusAsync(result, operation).ConfigureAwait(false);
                await TryPublishRunNotificationAsync(
                        result,
                        operation,
                        runtime.NotificationConfig,
                        commandCancellation.Token)
                    .ConfigureAwait(false);

                return new WorkerCommandResult
                {
                    EventType = WorkerIpcEventTypes.Completed,
                    Message = BuildRunMessage(result.Success, statusSaved),
                    SyncResult = WorkerResultSanitizer.CreateSummary(result),
                    PreflightDirectory = preflightDirectory
                };
            }
            catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
            {
                return new WorkerCommandResult
                {
                    EventType = WorkerIpcEventTypes.Cancelled,
                    Message = "Worker operation was cancelled."
                };
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Worker operation {Operation} failed before a structured result was produced.",
                    operation);
                return Error("Worker operation failed. See the Worker service log for details.");
            }
            finally
            {
                _activeRequests.TryRemove(request.RequestId, out _);
            }
        }
    }

    /// <summary>
    /// Tests configured notification channels under the global run gate without constructing API clients or exposing channel settings to IPC.
    /// </summary>
    private async Task<WorkerCommandResult> ExecuteNotificationTestAsync(
        WorkerIpcRequest request,
        CancellationToken hostCancellationToken)
    {
        if (!_runCoordinator.TryAcquire(
                request.RequestId,
                WorkerOperationNames.NotificationTest,
                _timeProvider.GetUtcNow(),
                out var lease))
        {
            return new WorkerCommandResult
            {
                EventType = WorkerIpcEventTypes.Busy,
                Message = "Worker is busy with another operation.",
                ActiveOperation = _runCoordinator.ActiveOperation
            };
        }

        using (lease)
        using (var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken))
        {
            if (!_activeRequests.TryAdd(request.RequestId, commandCancellation))
            {
                return Error("Duplicate request id.");
            }

            try
            {
                var config = await _settingsReader
                    .LoadNotificationConfigAsync(commandCancellation.Token)
                    .ConfigureAwait(false);
                var result = await _notificationTester
                    .TestAsync(config, commandCancellation.Token)
                    .ConfigureAwait(false);
                return new WorkerCommandResult
                {
                    EventType = WorkerIpcEventTypes.Completed,
                    Message = "Notification tests completed.",
                    NotificationTest = result
                };
            }
            catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
            {
                return new WorkerCommandResult
                {
                    EventType = WorkerIpcEventTypes.Cancelled,
                    Message = "Worker operation was cancelled."
                };
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Notification test failed with failure type {FailureType}.",
                    exception.GetType().Name);
                return Error("Notification test failed. See the Worker service log for details.");
            }
            finally
            {
                _activeRequests.TryRemove(request.RequestId, out _);
            }
        }
    }

    private async Task<bool> TrySaveStatusAsync(SyncRunResult result, string operation)
    {
        try
        {
            await _statusStore.SaveAsync(result, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Worker operation {Operation} completed but its latest status could not be saved.",
                operation);
            return false;
        }
    }

    /// <summary>
    /// Publishes configured manual Preview/Sync outcomes without changing the completed sync result when delivery fails.
    /// </summary>
    private async Task TryPublishRunNotificationAsync(
        SyncRunResult result,
        string operation,
        NotificationConfig config,
        CancellationToken cancellationToken)
    {
        var trigger = operation == WorkerOperationNames.Preview ? "manual-preview" : "manual";
        var notification = NotificationRequestFactory.CreateForSyncResult(result, trigger);
        if (!_notificationFilter.ShouldPublish(config, notification))
        {
            return;
        }

        try
        {
            await _notificationPublisher
                .PublishAsync(notification, config, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Manual operation notification failed with failure type {FailureType}.",
                exception.GetType().Name);
        }
    }

    private static string BuildRunMessage(bool success, bool statusSaved)
    {
        var outcome = success
            ? "Sync operation completed."
            : "Sync operation completed with failures.";
        return statusSaved
            ? outcome
            : $"{outcome} Latest status could not be saved; see the Worker service log.";
    }

    private static string? Validate(WorkerIpcRequest? request)
    {
        if (request is null)
        {
            return "Worker request is required.";
        }

        if (request.ProtocolVersion != WorkerIpcProtocol.Version)
        {
            return "Unsupported Worker protocol version.";
        }

        if (string.IsNullOrWhiteSpace(request.RequestId)
            || !RequestIdPattern.IsMatch(request.RequestId))
        {
            return "Invalid request id.";
        }

        if (!WorkerIpcCommands.IsKnown(request.Command))
        {
            return "Unknown Worker command.";
        }

        if (request.Command == WorkerIpcCommands.Cancel)
        {
            return request.TargetRequestId is not null
                && RequestIdPattern.IsMatch(request.TargetRequestId)
                    ? null
                    : "Cancel requires a valid target request id.";
        }

        return request.TargetRequestId is null
            ? null
            : "Only Cancel accepts a target request id.";
    }

    private static WorkerCommandResult Completed(string message)
    {
        return new WorkerCommandResult
        {
            EventType = WorkerIpcEventTypes.Completed,
            Message = message
        };
    }

    private static WorkerCommandResult Error(string message)
    {
        return new WorkerCommandResult
        {
            EventType = WorkerIpcEventTypes.Error,
            Message = message
        };
    }
}
