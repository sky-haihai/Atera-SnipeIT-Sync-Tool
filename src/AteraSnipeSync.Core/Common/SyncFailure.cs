namespace AteraSnipeSync.Core.Common;

public sealed class SyncFailure
{
    public required string Stage { get; init; }
    public required string Message { get; init; }
    public string? Code { get; init; }
}
