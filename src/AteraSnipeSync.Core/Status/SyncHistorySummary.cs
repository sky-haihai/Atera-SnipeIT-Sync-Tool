namespace AteraSnipeSync.Core.Status;

/// <summary>
/// Stores count-level sync history totals for fast UI summaries without parsing every item list.
/// </summary>
internal sealed class SyncHistorySummary
{
    public required int Pulled { get; init; }
    public required int Mapped { get; init; }
    public required int AssetsCreated { get; init; }
    public required int AssetsUpdated { get; init; }
    public required int AssetsDeleted { get; init; }
    public required int AssetsSkipped { get; init; }
    public required int AssetsFailed { get; init; }
    public required int CompaniesCreated { get; init; }
    public required int CompaniesUpdated { get; init; }
    public required int CompaniesDeleted { get; init; }
    public required int ModelsCreated { get; init; }
    public required int ModelsUpdated { get; init; }
    public required int ModelsDeleted { get; init; }
    public required int CategoriesCreated { get; init; }
    public required int CategoriesUpdated { get; init; }
    public required int CategoriesDeleted { get; init; }
    public required int WarningCount { get; init; }
    public required int FailureCount { get; init; }
}
