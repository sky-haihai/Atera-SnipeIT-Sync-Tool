using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Runtime.Ipc;
using AteraSnipeSync.TrayApp;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies Tray-side request envelopes, event ordering, progress forwarding, and protocol rejection over isolated local pipes.
/// </summary>
public sealed class WorkerIpcClientTests
{
    [Fact]
    public async Task Start_ValidSequence_ForwardsProgressAndReturnsTerminal()
    {
        var pipeName = $"AteraSnipeSync.TrayTests.{Guid.NewGuid():N}";
        var server = RunServerAsync(pipeName, (request, writer) => WriteEventsAsync(writer,
            Event(request, WorkerIpcEventTypes.Accepted),
            new WorkerIpcEvent
            {
                ProtocolVersion = WorkerIpcProtocol.Version,
                RequestId = request.RequestId,
                EventType = WorkerIpcEventTypes.Progress,
                Progress = new SyncProgressUpdate { Stage = "Sync", Message = "Working", Percent = 50 }
            },
            new WorkerIpcEvent
            {
                ProtocolVersion = WorkerIpcProtocol.Version,
                RequestId = request.RequestId,
                EventType = WorkerIpcEventTypes.Completed,
                ConnectionTest = new ConnectionTestResult
                {
                    Atera = new ConnectionEndpointTestResult { Succeeded = true, Message = "OK" },
                    SnipeIt = new ConnectionEndpointTestResult { Succeeded = true, Message = "OK" }
                }
            }));
        var updates = new List<SyncProgressUpdate>();
        var client = new WorkerIpcClient(pipeName, TimeSpan.FromSeconds(2));

        var operation = client.Start(
            WorkerIpcCommands.TestConnections,
            new InlineProgress<SyncProgressUpdate>(updates.Add),
            CancellationToken.None);
        var terminal = await operation.Completion;
        await server;

        Assert.Equal(WorkerIpcEventTypes.Completed, terminal.EventType);
        Assert.Single(updates);
        Assert.Equal(50, updates[0].Percent);
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedRequestId_ThrowsProtocolException()
    {
        var pipeName = $"AteraSnipeSync.TrayTests.{Guid.NewGuid():N}";
        var server = RunServerAsync(pipeName, (_, writer) => WriteEventsAsync(writer,
            new WorkerIpcEvent
            {
                ProtocolVersion = WorkerIpcProtocol.Version,
                RequestId = "wrong-id",
                EventType = WorkerIpcEventTypes.Completed
            }));
        var client = new WorkerIpcClient(pipeName, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<WorkerProtocolException>(
            () => client.ExecuteAsync(WorkerIpcCommands.Ping, CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task CancelAsync_SendsExactTargetRequestId()
    {
        var pipeName = $"AteraSnipeSync.TrayTests.{Guid.NewGuid():N}";
        WorkerIpcRequest? captured = null;
        var server = RunServerAsync(pipeName, (request, writer) =>
        {
            captured = request;
            return WriteEventsAsync(writer, new WorkerIpcEvent
            {
                ProtocolVersion = WorkerIpcProtocol.Version,
                RequestId = request.RequestId,
                EventType = WorkerIpcEventTypes.Completed,
                Message = "Cancellation requested."
            });
        });
        var client = new WorkerIpcClient(pipeName, TimeSpan.FromSeconds(2));

        Assert.True(await client.CancelAsync("preview-request-1", CancellationToken.None));
        await server;
        Assert.Equal(WorkerIpcCommands.Cancel, captured?.Command);
        Assert.Equal("preview-request-1", captured?.TargetRequestId);
    }

    [Fact]
    public async Task Start_TestNotifications_UsesPayloadFreeRequestAndRequiresTerminalResult()
    {
        var pipeName = $"AteraSnipeSync.TrayTests.{Guid.NewGuid():N}";
        WorkerIpcRequest? captured = null;
        var server = RunServerAsync(pipeName, (request, writer) =>
        {
            captured = request;
            return WriteEventsAsync(
                writer,
                Event(request, WorkerIpcEventTypes.Accepted),
                new WorkerIpcEvent
                {
                    ProtocolVersion = WorkerIpcProtocol.Version,
                    RequestId = request.RequestId,
                    EventType = WorkerIpcEventTypes.Completed,
                    NotificationTest = new NotificationTestResult
                    {
                        Email = new NotificationChannelTestResult
                        {
                            Configured = true,
                            Succeeded = true,
                            Message = "Email test succeeded."
                        },
                        Webhook = new NotificationChannelTestResult
                        {
                            Configured = false,
                            Succeeded = false,
                            Message = "Webhook is not configured."
                        }
                    }
                });
        });
        var client = new WorkerIpcClient(pipeName, TimeSpan.FromSeconds(2));

        var terminal = await client
            .Start(WorkerIpcCommands.TestNotifications, progress: null, CancellationToken.None)
            .Completion;
        await server;

        Assert.Equal(WorkerIpcCommands.TestNotifications, captured?.Command);
        Assert.Null(captured?.TargetRequestId);
        Assert.True(terminal.NotificationTest?.Email.Succeeded);
        Assert.False(terminal.NotificationTest?.Webhook.Configured);
    }

    private static async Task RunServerAsync(
        string pipeName,
        Func<WorkerIpcRequest, StreamWriter, Task> respond)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync();
        var request = JsonSerializer.Deserialize<WorkerIpcRequest>(line!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await respond(request, writer);
    }

    private static async Task WriteEventsAsync(StreamWriter writer, params WorkerIpcEvent[] events)
    {
        foreach (var @event in events)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                @event,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    private static WorkerIpcEvent Event(WorkerIpcRequest request, string eventType)
    {
        return new WorkerIpcEvent
        {
            ProtocolVersion = WorkerIpcProtocol.Version,
            RequestId = request.RequestId,
            EventType = eventType
        };
    }

    /// <summary>
    /// Forwards progress synchronously so assertions do not depend on a captured synchronization context.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
