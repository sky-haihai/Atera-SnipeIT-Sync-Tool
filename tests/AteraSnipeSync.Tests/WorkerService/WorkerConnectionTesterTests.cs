using System.Net;
using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Mapping;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.SnipeIt;
using AteraSnipeSync.Core.Sync;
using AteraSnipeSync.WorkerService;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.WorkerService;

/// <summary>
/// Verifies the combined read-only probes, documented Snipe-IT request shape, and independent endpoint results.
/// </summary>
public sealed class WorkerConnectionTesterTests
{
    [Fact]
    public async Task TestAllAsync_AteraFailure_StillTestsSnipeItWithDocumentedHeaders()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"total\":0,\"rows\":[]}")
            });
        var runtime = CreateRuntime(new FailingAteraClient(), new HttpClient(handler));
        var tester = new WorkerConnectionTester(NullLogger<WorkerConnectionTester>.Instance);

        var result = await tester.TestAllAsync(runtime, progress: null, CancellationToken.None);

        Assert.False(result.Atera.Succeeded);
        Assert.True(result.SnipeIt.Succeeded);
        Assert.Equal("https://snipe.example.com/api/v1/hardware?limit=1", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("snipe-token", handler.AuthorizationParameter);
        Assert.True(handler.AcceptsJson);
        Assert.True(handler.HasJsonContentType);
    }

    [Fact]
    public async Task TestAllAsync_SnipeItBusinessError_ReturnsFailedEndpointResult()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"error\",\"messages\":\"nope\"}")
            });
        var runtime = CreateRuntime(new SuccessfulAteraClient(), new HttpClient(handler));
        var tester = new WorkerConnectionTester(NullLogger<WorkerConnectionTester>.Instance);

        var result = await tester.TestAllAsync(runtime, progress: null, CancellationToken.None);

        Assert.True(result.Atera.Succeeded);
        Assert.False(result.SnipeIt.Succeeded);
        Assert.DoesNotContain("nope", result.SnipeIt.Message, StringComparison.Ordinal);
    }

    internal static WorkerSyncRuntime CreateRuntime(
        IAteraClient ateraClient,
        HttpClient snipeClient,
        ISyncOrchestrator? orchestrator = null,
        NotificationConfig? notificationConfig = null)
    {
        return new WorkerSyncRuntime
        {
            Orchestrator = orchestrator ?? new CompletedOrchestrator(),
            AteraConnectionClient = ateraClient,
            SnipeItHttpClient = snipeClient,
            NotificationConfig = notificationConfig
                ?? new NotificationConfig { Enabled = false, OnEvents = [] },
            BaseRequest = new SyncRunRequest
            {
                Atera = new AteraPullRequest { ApiKey = "atera-key" },
                Mapping = new MappingOptions
                {
                    DefaultCompanyName = "Acme",
                    DefaultManufacturerName = "Dell",
                    DefaultModelName = "Latitude",
                    DefaultCategoryName = "Computer",
                    DefaultStatusId = 2
                },
                SnipeIt = new SnipeImportOptions
                {
                    BaseUrl = "https://snipe.example.com/api/v1",
                    ApiToken = "snipe-token",
                    DryRun = false,
                    CreateMissingCompanies = true,
                    CreateMissingModels = true
                },
                Sync = new SyncRunOptions { DryRun = false, TriggeredBy = "scheduled" }
            }
        };
    }

    /// <summary>
    /// Returns an empty successful Atera snapshot for combined-probe success cases.
    /// </summary>
    private sealed class SuccessfulAteraClient : IAteraClient
    {
        public Task<AteraPullResult> PullInventoryAsync(
            AteraPullRequest request,
            CancellationToken cancellationToken,
            IProgress<AteraSnipeSync.Core.Common.SyncProgressUpdate>? progress = null)
        {
            return Task.FromResult(new AteraPullResult
            {
                Agents = [],
                Summary = new PullSummary { AgentCount = 0, PulledAt = DateTimeOffset.UtcNow },
                Warnings = []
            });
        }
    }

    /// <summary>
    /// Simulates an Atera authentication failure while allowing the Snipe-IT probe to continue.
    /// </summary>
    private sealed class FailingAteraClient : IAteraClient
    {
        public Task<AteraPullResult> PullInventoryAsync(
            AteraPullRequest request,
            CancellationToken cancellationToken,
            IProgress<AteraSnipeSync.Core.Common.SyncProgressUpdate>? progress = null)
        {
            return Task.FromException<AteraPullResult>(new AteraPullException(
                AteraPullFailureKind.AuthenticationFailed,
                "rejected"));
        }
    }

    /// <summary>
    /// Fulfills the runtime contract; connection tests never invoke orchestration.
    /// </summary>
    private sealed class CompletedOrchestrator : ISyncOrchestrator
    {
        public Task<SyncRunResult> RunOnceAsync(
            SyncRunRequest request,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SyncRunResult
            {
                Success = true,
                DryRun = request.Sync.DryRun,
                StartedAt = now,
                FinishedAt = now,
                PullResult = null,
                ImportBatch = null,
                ImportResult = null,
                Warnings = [],
                Failures = []
            });
        }
    }

    /// <summary>
    /// Records the read-only Snipe-IT request and returns a configured in-memory response.
    /// </summary>
    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public bool AcceptsJson { get; private set; }
        public bool HasJsonContentType { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            AcceptsJson = request.Headers.Accept.Any(value => value.MediaType == "application/json");
            HasJsonContentType = request.Content?.Headers.ContentType?.MediaType == "application/json";
            return Task.FromResult(response);
        }
    }
}
