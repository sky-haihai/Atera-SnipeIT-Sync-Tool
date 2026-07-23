using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Runtime.Ipc;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Reduces a full Core run result to IPC-safe counts and bounded diagnostics without serializing raw inventory or API responses.
/// </summary>
internal static class WorkerResultSanitizer
{
    private const int MaximumMessageLength = 500;
    private static readonly string[] SensitiveMarkers =
    [
        "api key",
        "apikey",
        "api_key",
        "authorization",
        "bearer",
        "password",
        "secret",
        "token"
    ];

    /// <summary>
    /// Creates a bounded count-only summary using run-level intent and asset-only failure counters.
    /// </summary>
    public static WorkerSyncResultSummary CreateSummary(SyncRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new WorkerSyncResultSummary
        {
            Success = result.Success,
            StartedAt = result.StartedAt,
            FinishedAt = result.FinishedAt,
            DryRun = result.DryRun,
            Cancelled = result.ImportResult?.Cancelled ?? false,
            Pulled = result.PullResult?.Summary.AgentCount ?? 0,
            Mapped = result.ImportBatch?.Summary.MappedAssetCount ?? 0,
            Created = result.ImportResult?.CreatedAssets ?? 0,
            Updated = result.ImportResult?.UpdatedAssets ?? 0,
            Deleted = result.ImportResult?.DeletedAssets ?? 0,
            Skipped = result.ImportResult?.SkippedAssets ?? 0,
            Failed = result.ImportResult?.FailedAssets ?? 0,
            WarningCount = result.Warnings.Count,
            Failures = result.Failures.Select(SanitizeFailure).ToArray()
        };
    }

    private static SyncFailure SanitizeFailure(SyncFailure failure)
    {
        return new SyncFailure
        {
            Stage = SanitizeStructured(failure.Stage, "Unknown"),
            Code = string.IsNullOrWhiteSpace(failure.Code)
                ? null
                : SanitizeStructured(failure.Code, "Unknown"),
            Message = SanitizeMessage(failure.Message)
        };
    }

    private static string SanitizeStructured(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }

        return trimmed.Length <= 100 ? trimmed : trimmed[..100];
    }

    private static string SanitizeMessage(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Operation failed.";
        }

        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            || (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            || SensitiveMarkers.Any(marker => trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return "[redacted sensitive details]";
        }

        return trimmed.Length <= MaximumMessageLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, MaximumMessageLength), "...");
    }
}
