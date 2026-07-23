using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Runtime.Ipc;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Hosts the local Worker command pipe, validates bounded JSON-line requests, and streams ordered sanitized events without tying runs to client lifetime.
/// </summary>
public sealed class WorkerIpcServer(
    WindowsWorkerPipeFactory pipeFactory,
    IWorkerCommandHandler commandHandler,
    ILogger<WorkerIpcServer> logger,
    WorkerIpcServerOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly ConcurrentDictionary<string, byte> _activeRequestIds = new(StringComparer.Ordinal);
    private readonly int _maximumConnections = ValidateMaximumConnections(options.MaxConcurrentConnections);
    private readonly TimeSpan _requestReadTimeout = ValidateRequestReadTimeout(options.RequestReadTimeout);
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly SemaphoreSlim _connectionSlots = new(
        ValidateMaximumConnections(options.MaxConcurrentConnections),
        ValidateMaximumConnections(options.MaxConcurrentConnections));
    private readonly string _pipeName = string.IsNullOrWhiteSpace(options.PipeName)
        ? throw new ArgumentException("Worker IPC pipe name is required.", nameof(options))
        : options.PipeName.Trim();
    private long _connectionId;

    /// <summary>
    /// Accepts concurrent local clients so status, reload, and cancel remain available while one long command is running.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var slotAcquired = false;
            try
            {
                await _connectionSlots.WaitAsync(stoppingToken).ConfigureAwait(false);
                slotAcquired = true;
                pipe = pipeFactory.Create(_pipeName, _maximumConnections);
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                if (!pipeFactory.IsLocalAuthorizedClient(pipe))
                {
                    pipe.Dispose();
                    pipe = null;
                    _connectionSlots.Release();
                    slotAcquired = false;
                    continue;
                }

                var connectionId = Interlocked.Increment(ref _connectionId);
                var connectionTask = HandleConnectionAsync(pipe, stoppingToken);
                pipe = null;
                _connections[connectionId] = connectionTask;
                _ = ObserveConnectionAsync(connectionId, connectionTask);
                slotAcquired = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                if (slotAcquired)
                {
                    _connectionSlots.Release();
                }
                break;
            }
            catch (Exception exception)
            {
                pipe?.Dispose();
                if (slotAcquired)
                {
                    _connectionSlots.Release();
                }
                logger.LogError(exception, "Worker IPC accept failed; the server will continue.");
            }
        }

        await Task.WhenAll(_connections.Values).ConfigureAwait(false);
    }

    private async Task ObserveConnectionAsync(long connectionId, Task connectionTask)
    {
        try
        {
            await connectionTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Worker IPC connection ended unexpectedly.");
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            _connectionSlots.Release();
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken hostCancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        using (var reader = new StreamReader(
                   pipe,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                   detectEncodingFromByteOrderMarks: false,
                   bufferSize: 4096,
                   leaveOpen: true))
        using (var writer = new StreamWriter(
                   pipe,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   bufferSize: 4096,
                   leaveOpen: true)
               { AutoFlush = true })
        {
            WorkerIpcRequest? request;
            try
            {
                using var timeout = new CancellationTokenSource(_requestReadTimeout, _timeProvider);
                using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    hostCancellationToken,
                    timeout.Token);
                var line = await ReadBoundedLineAsync(reader, readCancellation.Token).ConfigureAwait(false);
                request = line is null
                    ? null
                    : JsonSerializer.Deserialize<WorkerIpcRequest>(line, JsonOptions);
            }
            catch (OperationCanceledException) when (!hostCancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Worker IPC client did not send a complete request before the read timeout.");
                return;
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidDataException)
            {
                await TryWriteAsync(
                    writer,
                    CreateEvent("invalid", WorkerIpcEventTypes.Error, "Invalid Worker IPC request."),
                    hostCancellationToken).ConfigureAwait(false);
                return;
            }

            if (request is null)
            {
                return;
            }

            var requestId = request.RequestId ?? string.Empty;
            if (!_activeRequestIds.TryAdd(requestId, 0))
            {
                await TryWriteAsync(
                    writer,
                    CreateEvent(request.RequestId ?? "invalid", WorkerIpcEventTypes.Error, "Duplicate active request id."),
                    hostCancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                if (WorkerIpcCommands.IsLongRunning(request.Command))
                {
                    var accepted = CreateEvent(
                        requestId,
                        WorkerIpcEventTypes.Accepted,
                        "Worker command accepted.");
                    if (!await TryWriteAsync(writer, accepted, hostCancellationToken).ConfigureAwait(false))
                    {
                        // The command still executes because client lifetime is not a cancellation signal.
                    }
                }

                var progressChannel = Channel.CreateUnbounded<WorkerIpcEvent>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
                var progress = new InlineProgress<SyncProgressUpdate>(update =>
                {
                    progressChannel.Writer.TryWrite(new WorkerIpcEvent
                    {
                        ProtocolVersion = WorkerIpcProtocol.Version,
                        RequestId = requestId,
                        EventType = WorkerIpcEventTypes.Progress,
                        Progress = update
                    });
                });

                var commandTask = ExecuteAndCompleteProgressAsync(
                    request,
                    progress,
                    progressChannel.Writer,
                    hostCancellationToken);
                var clientConnected = true;
                await foreach (var progressEvent in progressChannel.Reader.ReadAllAsync(hostCancellationToken))
                {
                    if (clientConnected
                        && !await TryWriteAsync(writer, progressEvent, hostCancellationToken).ConfigureAwait(false))
                    {
                        clientConnected = false;
                    }
                }

                var result = await commandTask.ConfigureAwait(false);
                if (clientConnected)
                {
                    await TryWriteAsync(
                        writer,
                        ToEvent(requestId, result),
                        hostCancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _activeRequestIds.TryRemove(requestId, out _);
            }
        }
    }

    private async Task<WorkerCommandResult> ExecuteAndCompleteProgressAsync(
        WorkerIpcRequest request,
        IProgress<SyncProgressUpdate> progress,
        ChannelWriter<WorkerIpcEvent> progressWriter,
        CancellationToken hostCancellationToken)
    {
        try
        {
            return await commandHandler
                .ExecuteAsync(request, progress, hostCancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (hostCancellationToken.IsCancellationRequested)
        {
            return new WorkerCommandResult
            {
                EventType = WorkerIpcEventTypes.Cancelled,
                Message = "Worker service is stopping."
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Worker IPC command dispatch failed.");
            return new WorkerCommandResult
            {
                EventType = WorkerIpcEventTypes.Error,
                Message = "Worker command failed. See the Worker service log for details."
            };
        }
        finally
        {
            progressWriter.TryComplete();
        }
    }

    private static WorkerIpcEvent ToEvent(string requestId, WorkerCommandResult result)
    {
        return new WorkerIpcEvent
        {
            ProtocolVersion = WorkerIpcProtocol.Version,
            RequestId = requestId,
            EventType = result.EventType,
            Message = result.Message,
            SyncResult = result.SyncResult,
            ConnectionTest = result.ConnectionTest,
            NotificationTest = result.NotificationTest,
            WorkerStatus = result.WorkerStatus,
            ScheduleReload = result.ScheduleReload,
            ActiveOperation = result.ActiveOperation,
            PreflightDirectory = result.PreflightDirectory,
            ReportPath = result.ReportPath
        };
    }

    private static WorkerIpcEvent CreateEvent(string requestId, string eventType, string message)
    {
        return new WorkerIpcEvent
        {
            ProtocolVersion = WorkerIpcProtocol.Version,
            RequestId = requestId,
            EventType = eventType,
            Message = message
        };
    }

    private static async Task<bool> TryWriteAsync(
        StreamWriter writer,
        WorkerIpcEvent @event,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(@event, JsonOptions);
            if (json.Length > WorkerIpcProtocol.MaxMessageCharacters)
            {
                throw new InvalidDataException("Worker IPC response exceeded the message limit.");
            }

            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return false;
        }
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

                throw new InvalidDataException("Worker IPC request line was not terminated.");
            }

            if (character[0] == '\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            builder.Append(character[0]);
            if (builder.Length > WorkerIpcProtocol.MaxMessageCharacters)
            {
                throw new InvalidDataException("Worker IPC request exceeded the message limit.");
            }
        }
    }

    /// <summary>
    /// Forwards progress synchronously into the per-connection channel so event ordering remains deterministic.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value)
        {
            callback(value);
        }
    }

    private static int ValidateMaximumConnections(int value)
    {
        return value is >= 1 and <= 254
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(WorkerIpcServerOptions.MaxConcurrentConnections),
                "Worker IPC maximum connections must be between 1 and 254.");
    }

    private static TimeSpan ValidateRequestReadTimeout(TimeSpan value)
    {
        return value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(2)
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(WorkerIpcServerOptions.RequestReadTimeout),
                "Worker IPC request read timeout must be greater than zero and no more than two minutes.");
    }
}
