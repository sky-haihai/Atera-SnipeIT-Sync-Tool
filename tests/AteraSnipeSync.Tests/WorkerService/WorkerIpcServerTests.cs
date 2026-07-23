using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Runtime.Ipc;
using AteraSnipeSync.WorkerService;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.WorkerService;

/// <summary>
/// Verifies the real local named-pipe transport orders accepted, progress, and terminal events and rejects malformed JSON before dispatch.
/// </summary>
public sealed class WorkerIpcServerTests
{
    [Fact]
    public async Task PipeFactory_AcceptsLocalClient()
    {
        var pipeName = $"AteraSnipeSync.Tests.{Guid.NewGuid():N}";
        var factory = new WindowsWorkerPipeFactory();
        using var pipe = factory.Create(pipeName);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        var waitForConnection = pipe.WaitForConnectionAsync();
        await client.ConnectAsync(2000);
        await waitForConnection;

        Assert.True(factory.IsLocalAuthorizedClient(pipe));
    }

    [Fact]
    public async Task LongCommand_WritesAcceptedProgressAndCompletedInOrder()
    {
        var pipeName = $"AteraSnipeSync.Tests.{Guid.NewGuid():N}";
        var handler = new ProgressHandler();
        using var server = CreateServer(pipeName, handler);
        await server.StartAsync(CancellationToken.None);

        var events = await SendAsync(pipeName, new WorkerIpcRequest
        {
            ProtocolVersion = WorkerIpcProtocol.Version,
            RequestId = "test-1",
            Command = WorkerIpcCommands.TestConnections
        }, expectedEvents: 3);

        await server.StopAsync(CancellationToken.None);
        Assert.Equal(
            [WorkerIpcEventTypes.Accepted, WorkerIpcEventTypes.Progress, WorkerIpcEventTypes.Completed],
            events.Select(value => value.EventType).ToArray());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task MalformedJson_ReturnsErrorWithoutDispatch()
    {
        var pipeName = $"AteraSnipeSync.Tests.{Guid.NewGuid():N}";
        var handler = new ProgressHandler();
        using var server = CreateServer(pipeName, handler);
        await server.StartAsync(CancellationToken.None);
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

        await writer.WriteLineAsync("{not-json}");
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var response = JsonSerializer.Deserialize<WorkerIpcEvent>(line!, JsonOptions());

        await server.StopAsync(CancellationToken.None);
        Assert.Equal(WorkerIpcEventTypes.Error, response?.EventType);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task IncompleteFirstLine_IsClosedAfterReadTimeoutWithoutDispatch()
    {
        var pipeName = $"AteraSnipeSync.Tests.{Guid.NewGuid():N}";
        var handler = new ProgressHandler();
        using var server = CreateServer(
            pipeName,
            handler,
            new WorkerIpcServerOptions
            {
                PipeName = pipeName,
                RequestReadTimeout = TimeSpan.FromMilliseconds(100)
            });
        await server.StartAsync(CancellationToken.None);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await server.StopAsync(CancellationToken.None);
        Assert.Null(line);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CompleteCommand_CanRunLongerThanRequestReadTimeout()
    {
        var pipeName = $"AteraSnipeSync.Tests.{Guid.NewGuid():N}";
        var handler = new DelayedHandler(TimeSpan.FromMilliseconds(750));
        using var server = CreateServer(
            pipeName,
            handler,
            new WorkerIpcServerOptions
            {
                PipeName = pipeName,
                RequestReadTimeout = TimeSpan.FromMilliseconds(500)
            });
        await server.StartAsync(CancellationToken.None);

        var events = await SendAsync(pipeName, new WorkerIpcRequest
        {
            ProtocolVersion = WorkerIpcProtocol.Version,
            RequestId = "slow-command",
            Command = WorkerIpcCommands.TestConnections
        }, expectedEvents: 2);

        await server.StopAsync(CancellationToken.None);
        Assert.Equal(
            [WorkerIpcEventTypes.Accepted, WorkerIpcEventTypes.Completed],
            events.Select(value => value.EventType).ToArray());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConnectionLimit_ReleasesSlotAfterTimedOutClient()
    {
        var pipeName = $"AteraSnipeSync.Tests.{Guid.NewGuid():N}";
        var handler = new ProgressHandler();
        using var server = CreateServer(
            pipeName,
            handler,
            new WorkerIpcServerOptions
            {
                PipeName = pipeName,
                MaxConcurrentConnections = 1,
                RequestReadTimeout = TimeSpan.FromMilliseconds(300)
            });
        await server.StartAsync(CancellationToken.None);
        using var occupyingClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await occupyingClient.ConnectAsync(2000);
        using var occupyingReader = new StreamReader(occupyingClient, Encoding.UTF8, leaveOpen: true);

        using (var blockedClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => blockedClient.ConnectAsync(100));
        }

        Assert.Null(await occupyingReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        var events = await SendAsync(pipeName, new WorkerIpcRequest
        {
            ProtocolVersion = WorkerIpcProtocol.Version,
            RequestId = "after-timeout",
            Command = WorkerIpcCommands.TestConnections
        }, expectedEvents: 3);

        await server.StopAsync(CancellationToken.None);
        Assert.Equal(WorkerIpcEventTypes.Completed, events[^1].EventType);
        Assert.Equal(1, handler.CallCount);
    }

    private static WorkerIpcServer CreateServer(
        string pipeName,
        IWorkerCommandHandler handler,
        WorkerIpcServerOptions? options = null)
    {
        return new WorkerIpcServer(
            new WindowsWorkerPipeFactory(),
            handler,
            NullLogger<WorkerIpcServer>.Instance,
            options ?? new WorkerIpcServerOptions { PipeName = pipeName },
            TimeProvider.System);
    }

    private static async Task<IReadOnlyList<WorkerIpcEvent>> SendAsync(
        string pipeName,
        WorkerIpcRequest request,
        int expectedEvents)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var requestLine = JsonSerializer.Serialize(request, JsonOptions()) + "\n";
        var requestBytes = new UTF8Encoding(false).GetBytes(requestLine);
        await client.WriteAsync(requestBytes);
        await client.FlushAsync();

        var events = new List<WorkerIpcEvent>();
        while (events.Count < expectedEvents)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(line);
            events.Add(JsonSerializer.Deserialize<WorkerIpcEvent>(line, JsonOptions())!);
        }

        return events;
    }

    private static JsonSerializerOptions JsonOptions()
        => new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Emits one synchronous progress update followed by one completed terminal result.
    /// </summary>
    private sealed class ProgressHandler : IWorkerCommandHandler
    {
        public int CallCount { get; private set; }

        public Task<WorkerCommandResult> ExecuteAsync(
            WorkerIpcRequest request,
            IProgress<SyncProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            progress?.Report(new SyncProgressUpdate
            {
                Stage = "Test",
                Message = "Testing.",
                Current = 1,
                Total = 1
            });
            return Task.FromResult(new WorkerCommandResult
            {
                EventType = WorkerIpcEventTypes.Completed,
                Message = "Done."
            });
        }

        public bool TryCancel(string targetRequestId) => false;
    }

    /// <summary>
    /// Holds a command open beyond the first-line timeout to prove that timeout is not applied after dispatch.
    /// </summary>
    private sealed class DelayedHandler(TimeSpan delay) : IWorkerCommandHandler
    {
        public int CallCount { get; private set; }

        public async Task<WorkerCommandResult> ExecuteAsync(
            WorkerIpcRequest request,
            IProgress<SyncProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(delay, cancellationToken);
            return new WorkerCommandResult
            {
                EventType = WorkerIpcEventTypes.Completed,
                Message = "Done."
            };
        }

        public bool TryCancel(string targetRequestId) => false;
    }
}
