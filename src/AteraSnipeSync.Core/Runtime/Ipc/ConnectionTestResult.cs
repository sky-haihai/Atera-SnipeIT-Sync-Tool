namespace AteraSnipeSync.Core.Runtime.Ipc;

/// <summary>
/// Reports the sanitized outcome of one read-only endpoint connection probe.
/// </summary>
public sealed class ConnectionEndpointTestResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Groups the independent Atera and Snipe-IT connection outcomes produced under one Worker run lease.
/// </summary>
public sealed class ConnectionTestResult
{
    public required ConnectionEndpointTestResult Atera { get; init; }
    public required ConnectionEndpointTestResult SnipeIt { get; init; }
}
