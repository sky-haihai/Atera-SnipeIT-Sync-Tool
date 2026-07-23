using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Sync;

namespace AteraSnipeSync.Tests.Notifications;

/// <summary>
/// Verifies sync notification request construction without reading secrets or calling external systems.
/// </summary>
public sealed class NotificationRequestFactoryTests
{
    [Fact]
    public void CreateForSyncResult_ReturnsScheduledCompleted_WhenScheduledRunSucceeds()
    {
        var result = CreateResult(success: true);

        var request = NotificationRequestFactory.CreateForSyncResult(result, "scheduled");

        Assert.Equal(NotificationEventTypes.ScheduledSyncCompleted, request.EventType);
        Assert.Equal("Information", request.Severity);
        Assert.Equal("Scheduled sync completed", request.Subject);
        Assert.Contains("Result: Completed", request.Message);
        Assert.Same(result, request.SyncResult);
    }

    [Fact]
    public void CreateForSyncResult_ReturnsScheduledFailed_WhenScheduledRunFails()
    {
        var result = CreateResult(
            success: false,
            failures: [CreateFailure("SnipeImport", "ImportFailed", "import failed")]);

        var request = NotificationRequestFactory.CreateForSyncResult(result, " scheduled ");

        Assert.Equal(NotificationEventTypes.ScheduledSyncFailed, request.EventType);
        Assert.Equal("Error", request.Severity);
        Assert.Equal("Scheduled sync failed", request.Subject);
    }

    [Fact]
    public void CreateForSyncResult_ReturnsManualCompleted_WhenManualRunSucceeds()
    {
        var request = NotificationRequestFactory.CreateForSyncResult(CreateResult(success: true), "MANUAL");

        Assert.Equal(NotificationEventTypes.ManualSyncCompleted, request.EventType);
        Assert.Equal("Manual sync completed", request.Subject);
    }

    [Fact]
    public void CreateForSyncResult_ReturnsManualPreviewCompleted_WhenManualPreviewSucceeds()
    {
        var request = NotificationRequestFactory.CreateForSyncResult(CreateResult(success: true), "manual-preview");

        Assert.Equal(NotificationEventTypes.ManualPreviewCompleted, request.EventType);
        Assert.Equal("Manual preview completed", request.Subject);
    }

    [Fact]
    public void CreateForSyncResult_ReturnsGenericEvent_WhenTriggeredByUnknown()
    {
        var successRequest = NotificationRequestFactory.CreateForSyncResult(CreateResult(success: true), "tray");
        var failedRequest = NotificationRequestFactory.CreateForSyncResult(
            CreateResult(success: false, failures: [CreateFailure("AteraPull", "PullFailed", "pull failed")]),
            "tray");

        Assert.Equal(NotificationEventTypes.SyncCompleted, successRequest.EventType);
        Assert.Equal(NotificationEventTypes.SyncFailed, failedRequest.EventType);
    }

    [Fact]
    public void CreateForSyncResult_UsesWarningSeverity_WhenSuccessfulRunHasWarnings()
    {
        var result = CreateResult(
            success: true,
            warnings: [new ModuleWarning { Source = "Mapping", Message = "missing serial", Code = "MissingSerial" }]);

        var request = NotificationRequestFactory.CreateForSyncResult(result, "manual");

        Assert.Equal("Warning", request.Severity);
    }

    [Fact]
    public void CreateForSyncResult_ReturnsCompletedWarning_WhenCompletedRunHasRecordFailures()
    {
        var result = CreateResult(
            success: true,
            createdAssets: 2,
            failedAssets: 1,
            failures: [CreateFailure("SnipeImport", "SerialConflict", "serial already exists")]);

        var request = NotificationRequestFactory.CreateForSyncResult(result, "scheduled");

        Assert.Equal(NotificationEventTypes.ScheduledSyncCompleted, request.EventType);
        Assert.Equal("Warning", request.Severity);
        Assert.Equal("Scheduled sync completed", request.Subject);
        Assert.Contains("Result: Completed", request.Message);
        Assert.Contains("CreatedAssets: 2", request.Message);
        Assert.Contains("FailedAssets: 1", request.Message);
        Assert.Contains("FirstFailure: Stage=SnipeImport; Code=SerialConflict; Message=serial already exists", request.Message);
    }

    [Fact]
    public void CreateForSyncResult_UsesCriticalSeverity_ForAuthenticationFailureCode()
    {
        var result = CreateResult(
            success: false,
            failures: [CreateFailure("AteraPull", "Unauthorized", "request was rejected")]);

        var request = NotificationRequestFactory.CreateForSyncResult(result, "scheduled");

        Assert.Equal("Critical", request.Severity);
    }

    [Fact]
    public void CreateForSyncResult_IncludesSummaryCountsAndFirstFailure()
    {
        var result = CreateResult(
            success: false,
            pullAgentCount: 4,
            mappedAssetCount: 3,
            createdAssets: 1,
            updatedAssets: 2,
            deletedAssets: 3,
            skippedAssets: 3,
            failedAssets: 4,
            warnings: [new ModuleWarning { Source = "AteraPull", Message = "minor issue", Code = "Minor" }],
            failures: [CreateFailure("SnipeImport", "SerialConflict", "serial already exists")]);

        var request = NotificationRequestFactory.CreateForSyncResult(result, "manual");

        Assert.Contains("Result: Failed", request.Message);
        Assert.Contains("StartedAtUtc: 2026-06-18T10:00:00.0000000Z", request.Message);
        Assert.Contains("FinishedAtUtc: 2026-06-18T10:00:05.0000000Z", request.Message);
        Assert.Contains("DryRun: True", request.Message);
        Assert.Contains("PulledAgents: 4", request.Message);
        Assert.Contains("MappedAssets: 3", request.Message);
        Assert.Contains("CreatedAssets: 1", request.Message);
        Assert.Contains("UpdatedAssets: 2", request.Message);
        Assert.Equal(3, request.Deleted);
        Assert.Contains("DeletedAssets: 3", request.Message);
        Assert.Contains("SkippedAssets: 3", request.Message);
        Assert.Contains("FailedAssets: 4", request.Message);
        Assert.Contains("WarningCount: 1", request.Message);
        Assert.Contains("FirstFailure: Stage=SnipeImport; Code=SerialConflict; Message=serial already exists", request.Message);
    }

    [Fact]
    public void CreateForSyncResult_DoesNotCountPipelineFailureAsFailedAsset()
    {
        var result = CreateResult(
            success: false,
            failedAssets: 0,
            failures: [CreateFailure("Mapping", "Mapping.Invalid", "mapping failed")]);

        var request = NotificationRequestFactory.CreateForSyncResult(result, "scheduled");

        Assert.Contains("FailedAssets: 0", request.Message);
        Assert.Contains("FirstFailure: Stage=Mapping", request.Message);
    }

    [Fact]
    public void CreateForSyncResult_DoesNotIncludeSecretsOrRawPayloads()
    {
        const string apiKey = "atera-api-key-should-not-appear";
        const string token = "snipe-token-should-not-appear";
        const string authorizationHeader = "Authorization: Bearer should-not-appear";
        const string rawJson = "{\"api_key\":\"atera-api-key-should-not-appear\",\"token\":\"snipe-token-should-not-appear\"}";
        var result = CreateResult(
            success: false,
            agentRawJson: rawJson,
            failures: [CreateFailure("AteraPull", "InvalidApiKey", $"{authorizationHeader} {apiKey} {token} {rawJson}")]);

        var request = NotificationRequestFactory.CreateForSyncResult(result, "scheduled");

        Assert.DoesNotContain(apiKey, request.Message);
        Assert.DoesNotContain(token, request.Message);
        Assert.DoesNotContain(authorizationHeader, request.Message);
        Assert.DoesNotContain(rawJson, request.Message);
        Assert.Contains("[redacted sensitive details]", request.Message);
    }

    [Fact]
    public void CreateForSyncResult_ThrowsArgumentNullException_WhenResultNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => NotificationRequestFactory.CreateForSyncResult(null!, "scheduled"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateForSyncResult_ThrowsArgumentException_WhenTriggeredByBlank(string triggeredBy)
    {
        Assert.Throws<ArgumentException>(
            () => NotificationRequestFactory.CreateForSyncResult(CreateResult(success: true), triggeredBy));
    }

    private static SyncRunResult CreateResult(
        bool success,
        int pullAgentCount = 1,
        int mappedAssetCount = 1,
        int createdAssets = 1,
        int updatedAssets = 0,
        int deletedAssets = 0,
        int skippedAssets = 0,
        int failedAssets = 0,
        IReadOnlyList<ModuleWarning>? warnings = null,
        IReadOnlyList<SyncFailure>? failures = null,
        string agentRawJson = "{}")
    {
        return new SyncRunResult
        {
            Success = success,
            DryRun = true,
            StartedAt = new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 6, 18, 10, 0, 5, TimeSpan.Zero),
            PullResult = new AteraPullResult
            {
                Agents =
                [
                    new AgentInfo
                    {
                        AgentId = "agent-1",
                        Name = "LAPTOP-01",
                        RawJson = agentRawJson
                    }
                ],
                Summary = new PullSummary
                {
                    AgentCount = pullAgentCount,
                    PulledAt = new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero)
                },
                Warnings = warnings ?? []
            },
            ImportBatch = new SnipeImportBatch
            {
                Assets = [],
                Summary = new MappingSummary
                {
                    SourceAgentCount = pullAgentCount,
                    MappedAssetCount = mappedAssetCount
                },
                Warnings = warnings ?? []
            },
            ImportResult = new SnipeImportResult
            {
                CreatedAssets = createdAssets,
                UpdatedAssets = updatedAssets,
                DeletedAssets = deletedAssets,
                SkippedAssets = skippedAssets,
                FailedAssets = failedAssets,
                CreatedCompanies = 0,
                CreatedCategories = 0,
                CreatedModels = 0,
                UpdatedModels = 0,
                DryRun = true,
                Actions = [],
                Failures = [],
                Warnings = warnings ?? []
            },
            Warnings = warnings ?? [],
            Failures = failures ?? []
        };
    }

    private static SyncFailure CreateFailure(
        string stage,
        string code,
        string message)
    {
        return new SyncFailure
        {
            Stage = stage,
            Code = code,
            Message = message
        };
    }
}
