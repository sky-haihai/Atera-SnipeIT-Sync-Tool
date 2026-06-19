using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Groups mapped Snipe-IT asset records and mapping metadata for one import run.
/// </summary>
public sealed class SnipeImportBatch
{
    public required IReadOnlyList<SnipeAssetImportRecord> Assets { get; init; }
    public required MappingSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
