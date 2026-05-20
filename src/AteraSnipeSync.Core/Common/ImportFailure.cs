namespace AteraSnipeSync.Core.Common;

public sealed class ImportFailure
{
    public required string TargetType { get; init; }
    public required string TargetName { get; init; }
    public required string Message { get; init; }
    public string? Code { get; init; }
}
