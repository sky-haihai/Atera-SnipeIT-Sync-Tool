namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Implements the process-wide non-overlap gate for scheduled, connection-test, preview, and sync-now runs.
/// </summary>
public sealed class SyncRunCoordinator : ISyncRunCoordinator
{
    private ActiveRun? _activeRun;

    public bool IsRunning => Volatile.Read(ref _activeRun) is not null;

    public string? ActiveOperationId => Volatile.Read(ref _activeRun)?.OperationId;

    public string? ActiveOperation => Volatile.Read(ref _activeRun)?.Operation;

    public DateTimeOffset? ActiveStartedUtc => Volatile.Read(ref _activeRun)?.StartedUtc;

    /// <inheritdoc />
    public bool TryAcquire(
        string operationId,
        string operation,
        DateTimeOffset startedUtc,
        out IDisposable? lease)
    {
        var normalizedId = Require(operationId, nameof(operationId));
        var normalizedOperation = Require(operation, nameof(operation));
        var activeRun = new ActiveRun(normalizedId, normalizedOperation, startedUtc);
        if (Interlocked.CompareExchange(ref _activeRun, activeRun, null) is not null)
        {
            lease = null;
            return false;
        }

        lease = new RunLease(this, activeRun);
        return true;
    }

    private void Release(ActiveRun activeRun)
    {
        Interlocked.CompareExchange(ref _activeRun, null, activeRun);
    }

    private static string Require(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-blank value is required.", parameterName)
            : value.Trim();
    }

    /// <summary>
    /// Captures the immutable identity and start time of the one active Worker operation.
    /// </summary>
    private sealed record ActiveRun(
        string OperationId,
        string Operation,
        DateTimeOffset StartedUtc);

    /// <summary>
    /// Releases only the active run that created this lease and tolerates repeated disposal.
    /// </summary>
    private sealed class RunLease(SyncRunCoordinator owner, ActiveRun activeRun) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(activeRun);
            }
        }
    }
}
