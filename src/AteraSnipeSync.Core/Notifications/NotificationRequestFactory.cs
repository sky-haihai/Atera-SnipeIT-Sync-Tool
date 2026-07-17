using System.Text;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Builds safe, user-readable notification requests from completed sync run results.
/// </summary>
public static class NotificationRequestFactory
{
    private const string SeverityInformation = "Information";
    private const string SeverityWarning = "Warning";
    private const string SeverityError = "Error";
    private const string SeverityCritical = "Critical";
    private const int MaximumFailureMessageLength = 300;

    private static readonly string[] CriticalFailureCodes =
    [
        "AuthenticationFailed",
        "Unauthorized",
        "Forbidden",
        "InvalidApiKey",
        "InvalidToken"
    ];

    private static readonly string[] SensitiveMarkers =
    [
        "api key",
        "apikey",
        "api_key",
        "authorization",
        "bearer",
        "client_secret",
        "password",
        "secret",
        "snipe-it token",
        "token",
        "webhookurl",
        "webhook url"
    ];

    /// <summary>
    /// Converts one completed sync result into an event, severity, subject, and safe summary message.
    /// </summary>
    public static NotificationRequest CreateForSyncResult(
        SyncRunResult result,
        string triggeredBy)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrWhiteSpace(triggeredBy))
        {
            throw new ArgumentException("Sync trigger source must not be blank.", nameof(triggeredBy));
        }

        var normalizedTrigger = triggeredBy.Trim();
        var eventType = ResolveEventType(result.Success, normalizedTrigger);
        var severity = ResolveSeverity(result);
        var subject = ResolveSubject(eventType);

        return new NotificationRequest
        {
            EventType = eventType,
            Severity = severity,
            Subject = subject,
            Message = BuildMessage(result, subject),
            SyncResult = result
        };
    }

    private static string ResolveEventType(
        bool success,
        string triggeredBy)
    {
        if (string.Equals(triggeredBy, "scheduled", StringComparison.OrdinalIgnoreCase))
        {
            return success
                ? NotificationEventTypes.ScheduledSyncCompleted
                : NotificationEventTypes.ScheduledSyncFailed;
        }

        if (string.Equals(triggeredBy, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return success
                ? NotificationEventTypes.ManualSyncCompleted
                : NotificationEventTypes.ManualSyncFailed;
        }

        if (string.Equals(triggeredBy, "manual-preview", StringComparison.OrdinalIgnoreCase))
        {
            return success
                ? NotificationEventTypes.ManualPreviewCompleted
                : NotificationEventTypes.ManualPreviewFailed;
        }

        return success
            ? NotificationEventTypes.SyncCompleted
            : NotificationEventTypes.SyncFailed;
    }

    private static string ResolveSeverity(SyncRunResult result)
    {
        if (result.Success)
        {
            return result.Warnings.Count > 0 ? SeverityWarning : SeverityInformation;
        }

        return result.Failures.Any(IsCriticalFailure) ? SeverityCritical : SeverityError;
    }

    private static bool IsCriticalFailure(SyncFailure failure)
    {
        if (string.IsNullOrWhiteSpace(failure.Code))
        {
            return false;
        }

        var code = failure.Code.Trim();
        return CriticalFailureCodes.Any(
            criticalCode => string.Equals(criticalCode, code, StringComparison.OrdinalIgnoreCase)
                || code.EndsWith("." + criticalCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSubject(string eventType)
    {
        return eventType switch
        {
            NotificationEventTypes.ScheduledSyncCompleted => "Scheduled sync completed",
            NotificationEventTypes.ScheduledSyncFailed => "Scheduled sync failed",
            NotificationEventTypes.ManualSyncCompleted => "Manual sync completed",
            NotificationEventTypes.ManualSyncFailed => "Manual sync failed",
            NotificationEventTypes.ManualPreviewCompleted => "Manual preview completed",
            NotificationEventTypes.ManualPreviewFailed => "Manual preview failed",
            NotificationEventTypes.SyncCompleted => "Sync completed",
            NotificationEventTypes.SyncFailed => "Sync failed",
            _ => "Sync notification"
        };
    }

    private static string BuildMessage(
        SyncRunResult result,
        string subject)
    {
        var builder = new StringBuilder();
        var failedAssets = result.ImportResult?.FailedAssets ?? result.Failures.Count;

        builder.AppendLine($"Result: {(result.Success ? "Succeeded" : "Failed")}");
        builder.AppendLine($"Event: {subject}");
        builder.AppendLine($"StartedAtUtc: {result.StartedAt.UtcDateTime:O}");
        builder.AppendLine($"FinishedAtUtc: {result.FinishedAt.UtcDateTime:O}");
        builder.AppendLine($"PulledAgents: {result.PullResult?.Summary.AgentCount ?? 0}");
        builder.AppendLine($"MappedAssets: {result.ImportBatch?.Summary.MappedAssetCount ?? 0}");
        builder.AppendLine($"CreatedAssets: {result.ImportResult?.CreatedAssets ?? 0}");
        builder.AppendLine($"UpdatedAssets: {result.ImportResult?.UpdatedAssets ?? 0}");
        builder.AppendLine($"SkippedAssets: {result.ImportResult?.SkippedAssets ?? 0}");
        builder.AppendLine($"FailedAssets: {failedAssets}");
        builder.AppendLine($"WarningCount: {result.Warnings.Count}");

        var firstFailure = result.Failures.FirstOrDefault();
        if (firstFailure is not null)
        {
            builder.AppendLine(
                "FirstFailure: "
                + $"Stage={SanitizeStructuredValue(firstFailure.Stage)}; "
                + $"Code={SanitizeStructuredValue(firstFailure.Code)}; "
                + $"Message={SanitizeFreeText(firstFailure.Message)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string SanitizeStructuredValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        var trimmed = value.Trim();
        return LooksLikeRawPayload(trimmed) ? "[redacted sensitive details]" : trimmed;
    }

    private static string SanitizeFreeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        var trimmed = value.Trim();
        if (LooksLikeRawPayload(trimmed) || ContainsSensitiveMarker(trimmed))
        {
            return "[redacted sensitive details]";
        }

        return trimmed.Length <= MaximumFailureMessageLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, MaximumFailureMessageLength), "...");
    }

    private static bool LooksLikeRawPayload(string value)
    {
        return (value.StartsWith('{') && value.EndsWith('}'))
            || (value.StartsWith('[') && value.EndsWith(']'));
    }

    private static bool ContainsSensitiveMarker(string value)
    {
        return SensitiveMarkers.Any(
            marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
