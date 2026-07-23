using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Runtime.Ipc;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Executes bounded versioned JSON-Line commands over the local Worker pipe and validates acknowledgement, progress, and terminal ordering.
/// </summary>
internal sealed class WorkerIpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public WorkerIpcClient(
        string pipeName = WorkerIpcProtocol.DefaultPipeName,
        TimeSpan? connectTimeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Starts one allowed command immediately and returns its request id before connection and response processing finish.
    /// </summary>
    public WorkerIpcOperation Start(
        string command,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (!WorkerIpcCommands.IsKnown(command))
        {
            throw new ArgumentException("Unknown Worker command.", nameof(command));
        }

        var requestId = Guid.NewGuid().ToString("N");
        return new WorkerIpcOperation
        {
            RequestId = requestId,
            Completion = ExecuteCoreAsync(command, requestId, targetRequestId: null, progress, cancellationToken)
        };
    }

    /// <summary>
    /// Executes a non-cancel command and returns its one validated terminal event.
    /// </summary>
    public Task<WorkerIpcEvent> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        return Start(command, progress: null, cancellationToken).Completion;
    }

    /// <summary>
    /// Sends a separate Cancel command for the exact active Tray request and returns whether Worker accepted the target.
    /// </summary>
    public async Task<bool> CancelAsync(string targetRequestId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetRequestId))
        {
            return false;
        }

        var result = await ExecuteCoreAsync(
                WorkerIpcCommands.Cancel,
                Guid.NewGuid().ToString("N"),
                targetRequestId,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Message?.StartsWith("Cancellation requested", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<WorkerIpcEvent> ExecuteCoreAsync(
        string command,
        string requestId,
        string? targetRequestId,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCancellation.CancelAfter(_connectTimeout);
            await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new WorkerUnavailableException("Worker IPC is unavailable.", exception);
        }

        var request = new WorkerIpcRequest
        {
            ProtocolVersion = WorkerIpcProtocol.Version,
            RequestId = requestId,
            Command = command,
            TargetRequestId = targetRequestId
        };
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
               { AutoFlush = true })
        {
            await writer.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        var accepted = false;
        WorkerIpcEvent? terminal = null;
        while (true)
        {
            var line = await ReadBoundedLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            WorkerIpcEvent @event;
            try
            {
                @event = JsonSerializer.Deserialize<WorkerIpcEvent>(line, JsonOptions)
                    ?? throw new WorkerProtocolException("Worker returned an empty response.");
            }
            catch (JsonException)
            {
                throw new WorkerProtocolException("Worker returned malformed JSON.");
            }

            ValidateEnvelope(@event, requestId);
            if (@event.EventType == WorkerIpcEventTypes.Accepted)
            {
                if (accepted || terminal is not null)
                {
                    throw new WorkerProtocolException("Worker acknowledgement order is invalid.");
                }

                accepted = true;
                continue;
            }

            if (@event.EventType == WorkerIpcEventTypes.Progress)
            {
                if (terminal is not null || @event.Progress is null)
                {
                    throw new WorkerProtocolException("Worker progress order or payload is invalid.");
                }

                progress?.Report(@event.Progress);
                continue;
            }

            if (!IsTerminal(@event.EventType) || terminal is not null)
            {
                throw new WorkerProtocolException("Worker terminal event order is invalid.");
            }

            terminal = @event;
        }

        if (terminal is null)
        {
            throw new WorkerProtocolException("Worker disconnected before a terminal response.");
        }

        ValidatePayload(command, terminal);
        return terminal.EventType switch
        {
            WorkerIpcEventTypes.Busy => throw new WorkerBusyException(terminal.ActiveOperation),
            WorkerIpcEventTypes.Error => throw new WorkerCommandException(
                string.IsNullOrWhiteSpace(terminal.Message) ? "Worker command failed." : terminal.Message),
            _ => terminal
        };
    }

    private static void ValidateEnvelope(WorkerIpcEvent @event, string requestId)
    {
        if (@event.ProtocolVersion != WorkerIpcProtocol.Version)
        {
            throw new WorkerProtocolException("Tray and Worker protocol versions do not match.");
        }

        if (!string.Equals(@event.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("Worker response request id does not match.");
        }
    }

    private static void ValidatePayload(string command, WorkerIpcEvent terminal)
    {
        if (terminal.EventType is not WorkerIpcEventTypes.Completed)
        {
            return;
        }

        var valid = command switch
        {
            WorkerIpcCommands.GetStatus => terminal.WorkerStatus is not null,
            WorkerIpcCommands.ReloadSchedule => terminal.ScheduleReload is not null,
            WorkerIpcCommands.TestConnections => terminal.ConnectionTest is not null,
            WorkerIpcCommands.TestNotifications => terminal.NotificationTest is not null,
            WorkerIpcCommands.PreviewChanges or WorkerIpcCommands.SyncNow => terminal.SyncResult is not null,
            _ => true
        };
        if (!valid)
        {
            throw new WorkerProtocolException("Worker terminal payload does not match the command.");
        }
    }

    private static bool IsTerminal(string eventType)
    {
        return eventType is WorkerIpcEventTypes.Completed
            or WorkerIpcEventTypes.Busy
            or WorkerIpcEventTypes.Cancelled
            or WorkerIpcEventTypes.Error;
    }

    private static async Task<string?> ReadBoundedLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (builder.Length == 0)
                {
                    return null;
                }

                throw new WorkerProtocolException("Worker response line was not terminated.");
            }

            if (character[0] == '\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            builder.Append(character[0]);
            if (builder.Length > WorkerIpcProtocol.MaxMessageCharacters)
            {
                throw new WorkerProtocolException("Worker response exceeded the message limit.");
            }
        }
    }
}
