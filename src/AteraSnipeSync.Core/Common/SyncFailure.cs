namespace AteraSnipeSync.Core.Common;

/// <summary>
/// Describes a run-level failure produced by orchestration so callers can identify the failed stage.
/// </summary>
public sealed class SyncFailure
{
    public required string Stage { get; init; }
    public required string Message { get; init; }
    public string? Code { get; init; }
}
