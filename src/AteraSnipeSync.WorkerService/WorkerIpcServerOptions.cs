using AteraSnipeSync.Core.Runtime.Ipc;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Defines the local pipe identity, first-line read deadline, and bounded concurrent connection capacity.
/// </summary>
public sealed class WorkerIpcServerOptions
{
    public string PipeName { get; init; } = WorkerIpcProtocol.DefaultPipeName;
    public int MaxConcurrentConnections { get; init; } = 16;
    public TimeSpan RequestReadTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
