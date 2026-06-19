using System.Net;
using System.Text;
using AteraSnipeSync.Core.Atera;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.Atera;

public sealed class AteraClientTests
{
    private const string ApiKey = "secret-api-key";

    [Fact]
    public async Task PullInventoryAsync_ThrowsArgumentException_WhenApiKeyMissing()
    {
        var handler = new StubHttpMessageHandler();
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PullInventoryAsync(new AteraPullRequest { ApiKey = " " }, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PullInventoryAsync_ReturnsAllAgentInfo_WhenSinglePageSucceeds()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Envelope(FullAgentJson, totalItemCount: 1, totalPages: 1));
        var client = CreateClient(handler);

        var result = await client.PullInventoryAsync(CreateRequest(), CancellationToken.None);

        var agent = Assert.Single(result.Agents);
        Assert.Equal("1001", agent.AgentId);
        Assert.Equal("LAPTOP-001", agent.Name);
        Assert.Equal("M-1001", agent.MachineId);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", agent.DeviceGuid);
        Assert.Equal("2001", agent.FolderId);
        Assert.Equal("Managed Workstations", agent.FolderName);
        Assert.Equal("3001", agent.CustomerId);
        Assert.Equal("Acme Support", agent.CustomerName);
        Assert.Equal("Atera Agent Name", agent.AgentName);
        Assert.Equal("WIN-LAPTOP-001", agent.SystemName);
        Assert.Equal("example.local", agent.DomainName);
        Assert.Equal("user1", agent.CurrentLoggedUsers);
        Assert.True(agent.Monitored);
        Assert.True(agent.Favorite);
        Assert.True(agent.Online);
        Assert.Equal("Dell", agent.Manufacturer);
        Assert.Equal("Latitude 7440", agent.Model);
        Assert.Equal("SN-1001", agent.SerialNumber);
        Assert.Equal(32768, agent.Memory);
        Assert.Equal(8, agent.ProcessorCoresCount);
        Assert.Equal(["00-11-22-33-44-55", "66-77-88-99-AA-BB"], agent.MacAddresses);
        Assert.Equal(["10.0.0.10", "192.168.1.10"], agent.IpAddresses);
        Assert.Contains("Disk0", agent.HardwareDisksJson);
        Assert.Contains("Battery", agent.BatteryInfoJson);
        Assert.Equal("Windows 11 Pro", agent.OS);
        Assert.Equal("Workstation", agent.DeviceType);
        Assert.Equal("last.user", agent.LastLoginUser);
        Assert.Contains("\"OfficeFullVersion\"", agent.RawJson);
        Assert.Equal(1, result.Summary.AgentCount);

        var capturedRequest = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, capturedRequest.Method);
        Assert.Equal("/api/v3/agents", capturedRequest.RequestUri.AbsolutePath);
        Assert.Contains("page=1", capturedRequest.RequestUri.Query);
        Assert.Contains("itemsInPage=500", capturedRequest.RequestUri.Query);
        Assert.True(capturedRequest.Headers.TryGetValue("X-API-KEY", out var headerValues));
        Assert.Equal(ApiKey, Assert.Single(headerValues));
    }

    [Fact]
    public async Task PullInventoryAsync_ReturnsAllAgents_WhenMultiplePagesSucceed()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Envelope(MinimalAgentJson("1001", "DEVICE-001"), totalItemCount: 2, page: 1, totalPages: 2));
        handler.QueueResponse(HttpStatusCode.OK, Envelope(MinimalAgentJson("1002", "DEVICE-002"), totalItemCount: 2, page: 2, totalPages: 2));
        var client = CreateClient(handler);

        var result = await client.PullInventoryAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(2, result.Agents.Count);
        Assert.Equal(["1001", "1002"], result.Agents.Select(agent => agent.AgentId).ToArray());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page=1", handler.Requests[0].RequestUri.Query);
        Assert.Contains("page=2", handler.Requests[1].RequestUri.Query);
    }

    [Fact]
    public async Task PullInventoryAsync_DoesNotCallCustomerEndpoint()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Envelope(MinimalAgentJson("1001", "DEVICE-001")));
        var client = CreateClient(handler);

        await client.PullInventoryAsync(CreateRequest(), CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, request => request.RequestUri.AbsolutePath.Contains("customer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PullInventoryAsync_ThrowsAuthenticationFailed_WhenAteraReturnsAuthFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.Unauthorized, "{}");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AteraPullException>(
            () => client.PullInventoryAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal(AteraPullFailureKind.AuthenticationFailed, exception.FailureKind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PullInventoryAsync_DoesNotRetryAuthenticationFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.Forbidden, "{}");
        handler.QueueResponse(HttpStatusCode.OK, Envelope(MinimalAgentJson("1001", "DEVICE-001")));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<AteraPullException>(
            () => client.PullInventoryAsync(CreateRequest(), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PullInventoryAsync_RetriesRetryableFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.InternalServerError, "{}");
        handler.QueueResponse(HttpStatusCode.OK, Envelope(MinimalAgentJson("1001", "DEVICE-001")));
        var client = CreateClient(handler, maxRetryAttempts: 1);

        var result = await client.PullInventoryAsync(CreateRequest(), CancellationToken.None);

        Assert.Single(result.Agents);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PullInventoryAsync_ThrowsRetryExhaustedAndReturnsNoPartialResult_WhenFinalPageKeepsFailing()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Envelope(MinimalAgentJson("1001", "DEVICE-001"), totalItemCount: 2, page: 1, totalPages: 2));
        handler.QueueResponse(HttpStatusCode.InternalServerError, "{}");
        handler.QueueResponse(HttpStatusCode.InternalServerError, "{}");
        var client = CreateClient(handler, maxRetryAttempts: 1);

        var exception = await Assert.ThrowsAsync<AteraPullException>(
            () => client.PullInventoryAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal(AteraPullFailureKind.RetryExhausted, exception.FailureKind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task PullInventoryAsync_ThrowsMalformedResponse_WhenEnvelopeCannotBeParsed()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, """{ "totalPages": 1 }""");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AteraPullException>(
            () => client.PullInventoryAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal(AteraPullFailureKind.MalformedResponse, exception.FailureKind);
    }

    [Fact]
    public async Task PullInventoryAsync_AddsWarningAndSkipsRecord_WhenSingleAgentMissingRequiredIdentity()
    {
        var handler = new StubHttpMessageHandler();
        var malformedAgent = """{ "MachineName": "NO-ID" }""";
        var validAgent = MinimalAgentJson("1001", "DEVICE-001");
        handler.QueueResponse(HttpStatusCode.OK, Envelope($"{malformedAgent},{validAgent}", totalItemCount: 2, totalPages: 1));
        var client = CreateClient(handler);

        var result = await client.PullInventoryAsync(CreateRequest(), CancellationToken.None);

        var agent = Assert.Single(result.Agents);
        Assert.Equal("1001", agent.AgentId);
        Assert.Contains(result.Warnings, warning => warning.Code == "AteraPull.MissingAgentIdentity");
    }

    [Fact]
    public async Task PullInventoryAsync_ThrowsPaginationStateUnknown_WhenNextPageCannotBeDetermined()
    {
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(
            HttpStatusCode.OK,
            """{ "items": [{ "AgentID": 1001, "MachineName": "DEVICE-001" }], "page": 1 }""");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AteraPullException>(
            () => client.PullInventoryAsync(CreateRequest(), CancellationToken.None));

        Assert.Equal(AteraPullFailureKind.PaginationStateUnknown, exception.FailureKind);
    }

    [Fact]
    public async Task PullInventoryAsync_UsesClockForPulledAt()
    {
        var pulledAt = new DateTimeOffset(2026, 6, 17, 12, 30, 0, TimeSpan.Zero);
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.OK, Envelope(MinimalAgentJson("1001", "DEVICE-001")));
        var client = CreateClient(handler, clock: new FixedAteraClock(pulledAt));

        var result = await client.PullInventoryAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(pulledAt, result.Summary.PulledAt);
    }

    [Fact]
    public async Task PullInventoryAsync_DoesNotLogApiKey_WhenFailureOccurs()
    {
        var logger = new CapturingLogger<AteraClient>();
        var handler = new StubHttpMessageHandler();
        handler.QueueResponse(HttpStatusCode.BadRequest, "bad request");
        var client = CreateClient(handler, logger: logger);

        await Assert.ThrowsAsync<AteraPullException>(
            () => client.PullInventoryAsync(new AteraPullRequest { ApiKey = "very-secret-api-key" }, CancellationToken.None));

        Assert.DoesNotContain(logger.Messages, message => message.Contains("very-secret-api-key", StringComparison.Ordinal));
    }

    private static AteraClient CreateClient(
        StubHttpMessageHandler handler,
        int maxRetryAttempts = 0,
        IAteraClock? clock = null,
        ILogger<AteraClient>? logger = null)
    {
        var httpClient = new HttpClient(handler);
        var options = new AteraPullOptions
        {
            BaseUri = new Uri("https://app.atera.com/api/v3"),
            MaxRetryAttempts = maxRetryAttempts,
            RetryDelay = TimeSpan.Zero
        };

        return new AteraClient(
            httpClient,
            options,
            clock ?? new FixedAteraClock(DateTimeOffset.UnixEpoch),
            logger ?? NullLogger<AteraClient>.Instance);
    }

    private static AteraPullRequest CreateRequest()
    {
        return new AteraPullRequest { ApiKey = ApiKey };
    }

    private static string Envelope(
        string items,
        int totalItemCount = 1,
        int page = 1,
        int itemsInPage = 500,
        int totalPages = 1)
    {
        return $$"""
        {
          "items": [{{items}}],
          "totalItemCount": {{totalItemCount}},
          "page": {{page}},
          "itemsInPage": {{itemsInPage}},
          "totalPages": {{totalPages}},
          "prevLink": null,
          "nextLink": null
        }
        """;
    }

    private static string MinimalAgentJson(string agentId, string machineName)
    {
        return $$"""{ "AgentID": {{agentId}}, "MachineName": "{{machineName}}" }""";
    }

    private const string FullAgentJson = """
    {
      "MachineID": "M-1001",
      "AgentID": 1001,
      "DeviceGuid": "0f8fad5b-d9cb-469f-a165-70867728950e",
      "FolderID": 2001,
      "FolderName": "Managed Workstations",
      "CustomerID": 3001,
      "CustomerName": "Acme Support",
      "AgentName": "Atera Agent Name",
      "SystemName": "WIN-LAPTOP-001",
      "MachineName": "LAPTOP-001",
      "DomainName": "example.local",
      "CurrentLoggedUsers": "user1",
      "ComputerDescription": "Primary laptop",
      "Monitored": true,
      "AgentVersion": "1.2.3",
      "Favorite": true,
      "ThresholdID": 4001,
      "MonitoredAgentID": 5001,
      "Created": "2026-06-01T10:00:00Z",
      "Modified": "2026-06-02T11:00:00Z",
      "Online": true,
      "LastSeen": "2026-06-17T12:00:00Z",
      "ReportedFromIP": "203.0.113.10",
      "AppViewUrl": "https://app.atera.com/device/1001",
      "Motherboard": "Dell Board",
      "Processor": "Intel Core i7",
      "Memory": 32768,
      "Display": "Internal Display",
      "Sound": "Realtek",
      "ProcessorCoresCount": 8,
      "SystemDrive": "C:",
      "ProcessorClock": "2.8GHz",
      "Vendor": "Dell",
      "VendorSerialNumber": "SN-1001",
      "VendorBrandModel": "Latitude 7440",
      "ProductName": "Latitude Product",
      "BiosManufacturer": "Dell Inc.",
      "BiosVersion": "1.0.0",
      "BiosReleaseDate": "2026-01-01T00:00:00Z",
      "MacAddresses": ["00-11-22-33-44-55", "66-77-88-99-AA-BB"],
      "IpAddresses": ["10.0.0.10", "192.168.1.10"],
      "HardwareDisks": { "Disk0": "512GB SSD" },
      "BatteryInfo": { "Battery": "Healthy" },
      "OS": "Windows 11 Pro",
      "OSType": "Windows",
      "WindowsSerialNumber": "WIN-SN",
      "Office": "Microsoft 365",
      "OfficeSP": "SP1",
      "OfficeOEM": false,
      "OfficeSerialNumber": "OFFICE-SN",
      "OSNum": 10.0,
      "LastRebootTime": "2026-06-16T08:00:00Z",
      "OSVersion": "23H2",
      "OSBuild": "22631",
      "OfficeFullVersion": "16.0.1",
      "DeviceType": "Workstation",
      "LastLoginUser": "last.user"
    }
    """;

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = [];

        public List<CapturedRequest> Requests { get; } = [];

        public void QueueResponse(HttpStatusCode statusCode, string content)
        {
            _responses.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            }));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI is required."),
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray())));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP response is available.");
            }

            return _responses.Dequeue()(request, cancellationToken);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri RequestUri,
        IReadOnlyDictionary<string, string[]> Headers);

    private sealed class FixedAteraClock(DateTimeOffset utcNow) : IAteraClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.Message);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
