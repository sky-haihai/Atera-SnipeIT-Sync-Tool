namespace AteraSnipeSync.Core.Common;

/// <summary>
/// Describes a non-fatal module warning that should be surfaced with sync results without stopping the run.
/// </summary>
public sealed class ModuleWarning
{
    public required string Source { get; init; }
    public required string Message { get; init; }
    public string? Code { get; init; }
}
