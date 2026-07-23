namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Reports that the local Worker pipe could not be reached without exposing transport internals.
/// </summary>
internal sealed class WorkerUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Reports an incompatible or malformed Worker response without retaining the raw IPC frame.
/// </summary>
internal sealed class WorkerProtocolException(string message) : Exception(message);

/// <summary>
/// Reports the Worker's global non-overlap rejection and the sanitized active operation name.
/// </summary>
internal sealed class WorkerBusyException(string? activeOperation)
    : Exception("Worker is busy with another operation.")
{
    public string? ActiveOperation { get; } = activeOperation;
}

/// <summary>
/// Reports a sanitized Worker terminal Error event.
/// </summary>
internal sealed class WorkerCommandException(string message) : Exception(message);
