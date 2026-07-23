namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Serializes every Worker operation that can access an external API and exposes the active operation for status reporting.
/// </summary>
public interface ISyncRunCoordinator
{
    bool IsRunning { get; }
    string? ActiveOperationId { get; }
    string? ActiveOperation { get; }
    DateTimeOffset? ActiveStartedUtc { get; }

    /// <summary>
    /// Attempts to acquire the single run slot without queuing; the returned lease releases that exact slot when disposed.
    /// </summary>
    bool TryAcquire(
        string operationId,
        string operation,
        DateTimeOffset startedUtc,
        out IDisposable? lease);
}
