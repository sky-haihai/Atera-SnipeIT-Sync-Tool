using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Owns the Worker's versioned in-memory schedule, reloads it from the shared JSON file, and wakes the scheduler when it changes.
/// </summary>
public sealed class WorkerScheduleManager
{
    private const string DisabledRuleFingerprint = "DISABLED";
    private readonly ILocalAppSettingsReader _settingsReader;
    private readonly IScheduleRuntimeStateStore _stateStore;
    private readonly ScheduleCalculator _calculator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerScheduleManager> _logger;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly object _stateGate = new();

    private WorkerScheduleSnapshot _current = new()
    {
        Version = 0,
        ConfigurationValid = false,
        Enabled = false,
        Error = "Schedule has not been initialized."
    };
    private TaskCompletionSource<long> _changed = NewChangeSource();
    private bool _initialized;

    public WorkerScheduleManager(
        ILocalAppSettingsReader settingsReader,
        IScheduleRuntimeStateStore stateStore,
        ScheduleCalculator calculator,
        TimeProvider timeProvider,
        ILogger<WorkerScheduleManager> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsReader);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(calculator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _settingsReader = settingsReader;
        _stateStore = stateStore;
        _calculator = calculator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public WorkerScheduleSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Loads the initial schedule once; invalid configuration keeps IPC alive but disables future scheduled triggers.
    /// </summary>
    public async Task<ScheduleReloadResult> InitializeAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return new ScheduleReloadResult
                {
                    Applied = Current.ConfigurationValid,
                    Snapshot = Current,
                    Message = "Schedule is already initialized."
                };
            }

            _initialized = true;
            return await ReloadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// Reloads schedule settings from disk and atomically replaces future scheduling state without affecting an active run.
    /// </summary>
    public async Task<ScheduleReloadResult> ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _initialized = true;
            return await ReloadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// Waits until a newer schedule version is published; returns immediately when the caller already has a stale version.
    /// </summary>
    public Task WaitForChangeAsync(long observedVersion, CancellationToken cancellationToken)
    {
        Task changeTask;
        lock (_stateGate)
        {
            if (_current.Version != observedVersion)
            {
                return Task.CompletedTask;
            }

            changeTask = _changed.Task;
        }

        return changeTask.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Atomically claims one due UTC occurrence before execution and advances directly to the first future occurrence.
    /// </summary>
    public async Task<bool> ClaimDueOccurrenceAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _current;
            if (!snapshot.ConfigurationValid
                || !snapshot.Enabled
                || snapshot.Options is null
                || snapshot.RuleFingerprint is null
                || snapshot.NextRunUtc is null
                || snapshot.NextRunUtc.Value > nowUtc.ToUniversalTime())
            {
                return false;
            }

            var dueUtc = snapshot.NextRunUtc.Value.ToUniversalTime();
            var nextRunUtc = _calculator.GetNextRunUtc(snapshot.Options, nowUtc.ToUniversalTime());
            await _stateStore.SaveAsync(
                    new ScheduleRuntimeState
                    {
                        RuleFingerprint = snapshot.RuleFingerprint,
                        NextRunUtc = nextRunUtc,
                        LastTriggeredUtc = dueUtc
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            Publish(
                configurationValid: true,
                enabled: true,
                snapshot.Options,
                snapshot.RuleFingerprint,
                nextRunUtc,
                dueUtc,
                error: null);
            return true;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<ScheduleReloadResult> ReloadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = await _settingsReader
                .LoadSyncScheduleOptionsAsync(cancellationToken)
                .ConfigureAwait(false);

            if (options is null || !options.Enabled)
            {
                await _stateStore.SaveAsync(
                        new ScheduleRuntimeState
                        {
                            RuleFingerprint = DisabledRuleFingerprint,
                            NextRunUtc = null,
                            LastTriggeredUtc = null
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                var disabled = Publish(
                    configurationValid: true,
                    enabled: false,
                    options,
                    ruleFingerprint: null,
                    nextRunUtc: null,
                    lastTriggeredUtc: null,
                    error: null);
                _logger.LogInformation("Worker schedule is disabled.");
                return new ScheduleReloadResult
                {
                    Applied = true,
                    Snapshot = disabled,
                    Message = "Schedule is disabled."
                };
            }

            ScheduleCalculator.Validate(options);
            var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            var ruleFingerprint = ScheduleRuleFingerprint.Create(options);
            var state = await LoadStateOrNullAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset? nextRunUtc;
            DateTimeOffset? lastTriggeredUtc;
            if (state is not null
                && string.Equals(state.RuleFingerprint, ruleFingerprint, StringComparison.Ordinal))
            {
                nextRunUtc = state.NextRunUtc;
                lastTriggeredUtc = state.LastTriggeredUtc;
            }
            else
            {
                nextRunUtc = null;
                lastTriggeredUtc = null;
            }

            if (nextRunUtc is null)
            {
                nextRunUtc = _calculator.GetNextRunUtc(options, nowUtc);
                await _stateStore.SaveAsync(
                        new ScheduleRuntimeState
                        {
                            RuleFingerprint = ruleFingerprint,
                            NextRunUtc = nextRunUtc,
                            LastTriggeredUtc = lastTriggeredUtc
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var applied = Publish(
                configurationValid: true,
                enabled: true,
                options,
                ruleFingerprint,
                nextRunUtc,
                lastTriggeredUtc,
                error: null);
            _logger.LogInformation(
                "Worker schedule version {ScheduleVersion} applied; next run is {NextRunUtc}.",
                applied.Version,
                applied.NextRunUtc);
            return new ScheduleReloadResult
            {
                Applied = true,
                Snapshot = applied,
                Message = nextRunUtc is null
                    ? "Schedule was applied without a future trigger."
                    : "Schedule was applied."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            const string safeMessage = "Schedule configuration is invalid and scheduled runs are disabled.";
            var invalid = Publish(
                configurationValid: false,
                enabled: false,
                options: null,
                ruleFingerprint: null,
                nextRunUtc: null,
                lastTriggeredUtc: null,
                error: safeMessage);
            _logger.LogError(exception, "Worker schedule reload failed; scheduled runs are disabled.");
            return new ScheduleReloadResult
            {
                Applied = false,
                Snapshot = invalid,
                Message = safeMessage
            };
        }
    }

    private WorkerScheduleSnapshot Publish(
        bool configurationValid,
        bool enabled,
        SyncScheduleOptions? options,
        string? ruleFingerprint,
        DateTimeOffset? nextRunUtc,
        DateTimeOffset? lastTriggeredUtc,
        string? error)
    {
        TaskCompletionSource<long> previousChange;
        WorkerScheduleSnapshot snapshot;
        lock (_stateGate)
        {
            snapshot = new WorkerScheduleSnapshot
            {
                Version = _current.Version + 1,
                ConfigurationValid = configurationValid,
                Enabled = enabled,
                Options = options,
                RuleFingerprint = ruleFingerprint,
                NextRunUtc = nextRunUtc,
                LastTriggeredUtc = lastTriggeredUtc,
                Error = error
            };
            previousChange = _changed;
            _changed = NewChangeSource();
            Volatile.Write(ref _current, snapshot);
        }

        previousChange.TrySetResult(snapshot.Version);
        return snapshot;
    }

    /// <summary>
    /// Treats malformed or unreadable runtime state as recoverable while preserving cancellation semantics.
    /// </summary>
    private async Task<ScheduleRuntimeState?> LoadStateOrNullAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Worker schedule runtime state is invalid and will be rebuilt.");
            return null;
        }
    }

    private static TaskCompletionSource<long> NewChangeSource()
    {
        return new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
