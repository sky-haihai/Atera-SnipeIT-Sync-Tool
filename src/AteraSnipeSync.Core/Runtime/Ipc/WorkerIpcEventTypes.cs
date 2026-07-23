namespace AteraSnipeSync.Core.Runtime.Ipc;

/// <summary>
/// Provides stable event names for accepted, progress, and terminal IPC responses.
/// </summary>
public static class WorkerIpcEventTypes
{
    public const string Accepted = "Accepted";
    public const string Progress = "Progress";
    public const string Completed = "Completed";
    public const string Busy = "Busy";
    public const string Cancelled = "Cancelled";
    public const string Error = "Error";
}
