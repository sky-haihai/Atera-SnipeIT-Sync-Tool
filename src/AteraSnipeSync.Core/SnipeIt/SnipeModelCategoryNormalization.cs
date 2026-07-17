namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Holds the Snipe-IT endpoint, selected source categories, and target category used by model category maintenance.
/// </summary>
public sealed class SnipeModelCategoryNormalizationOptions
{
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiToken { get; init; } = string.Empty;

    public string TargetCategoryName { get; init; } = "Computer";

    public IReadOnlyList<string> SourceCategoryNames { get; init; } = ["Server", "Laptop", "Desktop"];

    public int PageSize { get; init; } = 500;
}

/// <summary>
/// Describes one model whose current category differs from the reviewed normalization target.
/// </summary>
public sealed record SnipeModelCategoryNormalizationCandidate(
    int ModelId,
    string ModelName,
    string SourceCategoryName);

/// <summary>
/// Captures the read-only scan result that must be reviewed before any model category update is sent.
/// </summary>
public sealed class SnipeModelCategoryNormalizationPlan
{
    public int ScannedModelCount { get; init; }

    public int TargetCategoryId { get; init; }

    public string TargetCategoryName { get; init; } = string.Empty;

    public IReadOnlyList<string> SourceCategoryNames { get; init; } = [];

    public IReadOnlyList<SnipeModelCategoryNormalizationCandidate> Models { get; init; } = [];
}

/// <summary>
/// Records the confirmed success or safe failure detail for one model update without exposing credentials or raw responses.
/// </summary>
public sealed record SnipeModelCategoryNormalizationOutcome(
    int ModelId,
    string ModelName,
    string SourceCategoryName,
    string TargetCategoryName,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Summarizes all model updates completed before success, partial failure, or operator cancellation.
/// </summary>
public sealed class SnipeModelCategoryNormalizationResult
{
    public SnipeModelCategoryNormalizationPlan Plan { get; init; } = new();

    public IReadOnlyList<SnipeModelCategoryNormalizationOutcome> Outcomes { get; init; } = [];

    public bool Cancelled { get; init; }

    public int UpdatedModelCount => Outcomes.Count(outcome => outcome.Success);

    public int FailedModelCount => Outcomes.Count(outcome => !outcome.Success);

    public bool Success => !Cancelled && FailedModelCount == 0 && Outcomes.Count == Plan.Models.Count;
}
