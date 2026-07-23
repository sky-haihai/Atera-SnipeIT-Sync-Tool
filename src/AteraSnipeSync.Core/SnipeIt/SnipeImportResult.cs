using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Reports the structured outcome of a Snipe-IT import run for orchestration and status reporting.
/// </summary>
public sealed class SnipeImportResult
{
    public required int CreatedAssets { get; init; }
    public required int UpdatedAssets { get; init; }
    public required int DeletedAssets { get; init; }
    public required int SkippedAssets { get; init; }
    public required int FailedAssets { get; init; }
    public required int CreatedCompanies { get; init; }
    public required int CreatedCategories { get; init; }
    public required int CreatedModels { get; init; }
    public required int UpdatedModels { get; init; }
    public required bool DryRun { get; init; }
    public bool Cancelled { get; init; }
    public required IReadOnlyList<ImportAction> Actions { get; init; }
    public required IReadOnlyList<ImportFailure> Failures { get; init; }
    public required IReadOnlyList<ModuleWarning> Warnings { get; init; }
}
