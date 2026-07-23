namespace AteraSnipeSync.Core.Runtime.Ipc;

/// <summary>
/// Carries one payload-free Worker command and, for cancellation only, the target operation request id.
/// </summary>
public sealed class WorkerIpcRequest
{
    public required int ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public required string Command { get; init; }
    public string? TargetRequestId { get; init; }
}
