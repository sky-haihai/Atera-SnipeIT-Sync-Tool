namespace AteraSnipeSync.Core.Common;

/// <summary>
/// Describes a target-specific import failure that callers can surface without parsing logs.
/// </summary>
public sealed class ImportFailure
{
    public required string TargetType { get; init; }
    public required string TargetName { get; init; }
    public required string Message { get; init; }
    public string? Code { get; init; }
}
