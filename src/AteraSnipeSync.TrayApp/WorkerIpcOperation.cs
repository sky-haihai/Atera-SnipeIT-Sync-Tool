using AteraSnipeSync.Core.Runtime.Ipc;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Exposes a command request id immediately so Cancel can target the exact Worker operation while completion remains asynchronous.
/// </summary>
internal sealed class WorkerIpcOperation
{
    public required string RequestId { get; init; }
    public required Task<WorkerIpcEvent> Completion { get; init; }
}
