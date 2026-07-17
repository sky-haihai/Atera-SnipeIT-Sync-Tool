using System.Net;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.SnipeIt;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.SnipeIt;

/// <summary>
/// Verifies all-model category normalization wire behavior entirely through deterministic mocked HTTP responses.
/// </summary>
public sealed class SnipeModelCategoryNormalizerTests
{
    [Fact]
    public async Task PlanAsync_ScansAllModelPages_IncludingUnusedAndServerModels()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Category(30, "Computer", "asset")));
        handler.QueueResponse(HttpStatusCode.OK, RowsWithTotal(
            5,
            Model(40, "PowerEdge R740", 21, "Server"),
            Model(41, "Latitude 5400", 22, "Laptop")));
        handler.QueueResponse(HttpStatusCode.OK, RowsWithTotal(
            5,
            Model(42, "OptiPlex 5070", 23, "Desktop"),
            Model(43, "Already Computer", 30, "Computer")));
        handler.QueueResponse(HttpStatusCode.OK, RowsWithTotal(
            5,
            Model(44, "Inventory Printer", 24, "Printer")));

        var plan = await CreateNormalizer(handler).PlanAsync(
            CreateOptions(pageSize: 2),
            CancellationToken.None);

        Assert.Equal(5, plan.ScannedModelCount);
        Assert.Equal(["Desktop", "Laptop", "Server"], plan.SourceCategoryNames);
        Assert.Equal(30, plan.TargetCategoryId);
        Assert.Collection(
            plan.Models,
            candidate =>
            {
                Assert.Equal(41, candidate.ModelId);
                Assert.Equal("Latitude 5400", candidate.ModelName);
                Assert.Equal("Laptop", candidate.SourceCategoryName);
            },
            candidate =>
            {
                Assert.Equal(42, candidate.ModelId);
                Assert.Equal("OptiPlex 5070", candidate.ModelName);
                Assert.Equal("Desktop", candidate.SourceCategoryName);
            },
            candidate =>
            {
                Assert.Equal(40, candidate.ModelId);
                Assert.Equal("PowerEdge R740", candidate.ModelName);
                Assert.Equal("Server", candidate.SourceCategoryName);
            });
        Assert.Equal("/api/v1/categories?limit=2&offset=0", handler.Requests[0].PathAndQuery);
        Assert.Equal("/api/v1/models?limit=2&offset=0", handler.Requests[1].PathAndQuery);
        Assert.Equal("/api/v1/models?limit=2&offset=2", handler.Requests[2].PathAndQuery);
        Assert.Equal("/api/v1/models?limit=2&offset=4", handler.Requests[3].PathAndQuery);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task PlanAsync_OnlyIncludesOperatorSelectedSourceCategories()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Category(30, "Computer", "asset")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(
            Model(40, "PowerEdge R740", 21, "SERVER"),
            Model(41, "Latitude 5400", 22, "Laptop"),
            Model(42, "Inventory Printer", 24, "Printer")));

        var plan = await CreateNormalizer(handler).PlanAsync(
            CreateOptions(sourceCategories: [" server "]),
            CancellationToken.None);

        var candidate = Assert.Single(plan.Models);
        Assert.Equal("PowerEdge R740", candidate.ModelName);
        Assert.Equal(["server"], plan.SourceCategoryNames);
    }

    [Fact]
    public async Task PlanAsync_RejectsEmptySourceCategoryListBeforeAnyRequest()
    {
        var handler = new StubHttpMessageHandler();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateNormalizer(handler).PlanAsync(
            CreateOptions(sourceCategories: []),
            CancellationToken.None));

        Assert.Contains("source category", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PlanAsync_DecodesHtmlAndMatchesTargetCategoryCaseInsensitively()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Category(30, "Research &amp; Development", "ASSET")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(Model(40, "R&amp;D Server", 21, "Server &amp; Appliance")));

        var plan = await CreateNormalizer(handler).PlanAsync(
            CreateOptions(
                targetCategory: "research & development",
                sourceCategories: ["Server & Appliance"]),
            CancellationToken.None);

        Assert.Equal(30, plan.TargetCategoryId);
        Assert.Equal("Research & Development", plan.TargetCategoryName);
        var candidate = Assert.Single(plan.Models);
        Assert.Equal("R&D Server", candidate.ModelName);
        Assert.Equal("Server & Appliance", candidate.SourceCategoryName);
    }

    [Fact]
    public async Task PlanAsync_FailsBeforeModelScan_WhenTargetCategoryIsAmbiguous()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(
            Category(30, "Computer", "asset"),
            Category(31, "COMPUTER", "asset")));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => CreateNormalizer(handler).PlanAsync(
            CreateOptions(),
            CancellationToken.None));

        Assert.Contains("multiple category ids", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task PlanAsync_FailsBeforeModelScan_WhenNamedTargetIsNotAssetCategory()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Category(30, "Computer", "accessory")));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => CreateNormalizer(handler).PlanAsync(
            CreateOptions(),
            CancellationToken.None));

        Assert.Contains("does not identify an asset category", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PlanAsync_FailsBeforeModelScan_WhenTargetCategoryDoesNotExist()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, EmptyRows());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => CreateNormalizer(handler).PlanAsync(
            CreateOptions(),
            CancellationToken.None));

        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PlanAsync_FailsBeforeWrites_WhenModelRowIsMalformed()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Category(30, "Computer", "asset")));
        handler.QueueResponse(
            HttpStatusCode.OK,
            """{ "total": 1, "rows": [{ "id": 40, "name": "Latitude" }] }""");

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => CreateNormalizer(handler).PlanAsync(
            CreateOptions(),
            CancellationToken.None));

        Assert.Contains("category object", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task PlanAsync_FailsBeforeWrites_WhenModelReferenceChangesWithinSnapshot()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Rows(Category(30, "Computer", "asset")));
        handler.QueueResponse(HttpStatusCode.OK, Rows(
            Model(40, "Latitude 5400", 22, "Laptop"),
            Model(40, "Different Name", 22, "Laptop")));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => CreateNormalizer(handler).PlanAsync(
            CreateOptions(),
            CancellationToken.None));

        Assert.Contains("conflicting name or category", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ExecuteAsync_PutsRequiredNameAndCategoryIdToEachModelEndpoint()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload());
        var plan = CreatePlan(new SnipeModelCategoryNormalizationCandidate(
            40,
            "PowerEdge R740",
            "Server"));

        var result = await CreateNormalizer(handler).ExecuteAsync(
            plan,
            CreateOptions(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.UpdatedModelCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v1/models/40", request.PathAndQuery);
        using var payload = JsonDocument.Parse(request.Content);
        Assert.Equal(2, payload.RootElement.EnumerateObject().Count());
        Assert.Equal("PowerEdge R740", payload.RootElement.GetProperty("name").GetString());
        Assert.Equal(30, payload.RootElement.GetProperty("category_id").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesAfterOneModelFails_AndReturnsDetailedOutcomes()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(
            HttpStatusCode.UnprocessableEntity,
            """{ "messages": { "category_id": ["Category cannot be used."] } }""");
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload());
        var plan = CreatePlan(
            new SnipeModelCategoryNormalizationCandidate(40, "PowerEdge R740", "Server"),
            new SnipeModelCategoryNormalizationCandidate(41, "OptiPlex 5070", "Desktop"));

        var result = await CreateNormalizer(handler).ExecuteAsync(
            plan,
            CreateOptions(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.UpdatedModelCount);
        Assert.Equal(1, result.FailedModelCount);
        Assert.Equal("SnipeModelCategoryNormalization.ValidationFailed", result.Outcomes[0].ErrorCode);
        Assert.Contains("category_id: Category cannot be used.", result.Outcomes[0].ErrorMessage);
        Assert.True(result.Outcomes[1].Success);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccessfulNoOp_WhenPlanHasNoCandidates()
    {
        var handler = new StubHttpMessageHandler();

        var result = await CreateNormalizer(handler).ExecuteAsync(
            CreatePlan(),
            CreateOptions(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Outcomes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsPartialCanceledResult_AfterCurrentPutCompletes()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler
        {
            OnRequest = request =>
            {
                if (request.Method == HttpMethod.Put)
                {
                    cancellation.Cancel();
                }
            }
        };
        handler.QueueResponse(HttpStatusCode.OK, SuccessPayload());
        var plan = CreatePlan(
            new SnipeModelCategoryNormalizationCandidate(40, "PowerEdge R740", "Server"),
            new SnipeModelCategoryNormalizationCandidate(41, "OptiPlex 5070", "Desktop"));

        var result = await CreateNormalizer(handler).ExecuteAsync(
            plan,
            CreateOptions(),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Equal(1, result.UpdatedModelCount);
        Assert.Single(result.Outcomes);
        Assert.Single(handler.Requests);
    }

    private static SnipeModelCategoryNormalizer CreateNormalizer(StubHttpMessageHandler handler)
    {
        return new SnipeModelCategoryNormalizer(
            new HttpClient(handler),
            NullLogger<SnipeModelCategoryNormalizer>.Instance);
    }

    private static SnipeModelCategoryNormalizationOptions CreateOptions(
        int pageSize = 500,
        string targetCategory = "Computer",
        IReadOnlyList<string>? sourceCategories = null)
    {
        return new SnipeModelCategoryNormalizationOptions
        {
            BaseUrl = "https://snipe.example.com/api/v1",
            ApiToken = "test-token",
            TargetCategoryName = targetCategory,
            SourceCategoryNames = sourceCategories ?? ["Server", "Laptop", "Desktop"],
            PageSize = pageSize
        };
    }

    private static SnipeModelCategoryNormalizationPlan CreatePlan(
        params SnipeModelCategoryNormalizationCandidate[] candidates)
    {
        return new SnipeModelCategoryNormalizationPlan
        {
            ScannedModelCount = candidates.Length,
            TargetCategoryId = 30,
            TargetCategoryName = "Computer",
            SourceCategoryNames = ["Server", "Laptop", "Desktop"],
            Models = candidates
        };
    }

    private static string EmptyRows()
    {
        return """{ "total": 0, "rows": [] }""";
    }

    private static string Rows(params string[] rows)
    {
        return RowsWithTotal(rows.Length, rows);
    }

    private static string RowsWithTotal(int total, params string[] rows)
    {
        return $$"""{ "total": {{total}}, "rows": [{{string.Join(",", rows)}}] }""";
    }

    private static string Model(int id, string name, int categoryId, string categoryName)
    {
        return $$"""{ "id": {{id}}, "name": "{{name}}", "category": { "id": {{categoryId}}, "name": "{{categoryName}}" } }""";
    }

    private static string Category(int id, string name, string categoryType)
    {
        return $$"""{ "id": {{id}}, "name": "{{name}}", "category_type": "{{categoryType}}" }""";
    }

    private static string SuccessPayload()
    {
        return """{ "status": "success", "payload": { "id": 40 } }""";
    }

    /// <summary>
    /// Captures outgoing requests and returns only explicitly queued local responses.
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = [];

        public List<CapturedRequest> Requests { get; } = [];

        public Action<CapturedRequest>? OnRequest { get; init; }

        public void QueueResponse(HttpStatusCode statusCode, string content)
        {
            _responses.Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new CapturedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                content);
            Requests.Add(captured);
            OnRequest?.Invoke(captured);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued response is available.");
            }

            return _responses.Dequeue();
        }
    }

    /// <summary>
    /// Stores the safe method, relative endpoint, and test payload needed for wire assertions.
    /// </summary>
    private sealed record CapturedRequest(HttpMethod Method, string PathAndQuery, string Content);
}
