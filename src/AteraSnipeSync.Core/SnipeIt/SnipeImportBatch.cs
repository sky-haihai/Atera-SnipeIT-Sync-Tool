using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;

namespace AteraSnipeSync.Core.SnipeIt;

public sealed class SnipeImportBatch
{
    public required IReadOnlyList<SnipeAssetImportRecord> Assets { get; init; }
    public required MappingSummary Summary { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
