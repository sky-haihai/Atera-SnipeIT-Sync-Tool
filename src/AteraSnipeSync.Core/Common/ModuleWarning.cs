namespace AteraSnipeSync.Core.Common;

public sealed class ModuleWarning
{
    public required string Source { get; init; }
    public required string Message { get; init; }
    public string? Code { get; init; }
}
