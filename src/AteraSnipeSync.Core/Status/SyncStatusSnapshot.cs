namespace AteraSnipeSync.Core.Status;

public sealed class SyncStatusSnapshot
{
    public required bool IsRunning { get; init; }
    public required string LastResult { get; init; }
    public required DateTimeOffset? LastRunStartedAt { get; init; }
    public required DateTimeOffset? LastRunFinishedAt { get; init; }
    public required DateTimeOffset? LastSuccessAt { get; init; }
    public required bool DryRun { get; init; }
    public required int Pulled { get; init; }
    public required int Mapped { get; init; }
    public required int Created { get; init; }
    public required int Updated { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }
    public string? LastError { get; init; }
}
