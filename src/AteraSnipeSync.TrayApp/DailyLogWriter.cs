using System.Threading.Channels;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Serializes every accepted daily manual-sync log entry on a background task so detailed progress never blocks the UI thread or drops on queue capacity.
/// </summary>
internal sealed class DailyLogWriter : IAsyncDisposable
{
    private readonly string _directoryPath;
    private readonly string _fileNamePrefix;
    private readonly TimeSpan _retentionAge;
    private readonly Channel<LogEntry> _channel;
    private readonly Task _writerTask;

    public DailyLogWriter(
        string directoryPath,
        string fileNamePrefix = "ManualSync",
        TimeSpan? retentionAge = null)
    {
        if (string.IsNullOrWhiteSpace(fileNamePrefix)
            || !string.Equals(Path.GetFileName(fileNamePrefix), fileNamePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Log file name prefix must be a non-empty file name.", nameof(fileNamePrefix));
        }

        _directoryPath = directoryPath;
        _fileNamePrefix = fileNamePrefix;
        _retentionAge = retentionAge ?? TimeSpan.FromDays(30);
        _channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = RunAsync();
    }

    public Exception? LastError { get; private set; }

    /// <summary>
    /// Returns the local daily log path that receives entries for the supplied timestamp.
    /// </summary>
    public string GetLogPath(DateTimeOffset timestamp)
    {
        return Path.Combine(
            _directoryPath,
            $"{_fileNamePrefix}_{timestamp:yyyyMMdd}.log");
    }

    /// <summary>
    /// Queues a complete formatted log line and returns false only when the writer is closing or has failed.
    /// </summary>
    public bool TryWrite(DateTimeOffset timestamp, string line)
    {
        return _channel.Writer.TryWrite(new LogEntry(timestamp, line));
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(_directoryPath);
            ApplyRetention();
            await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var path = GetLogPath(entry.Timestamp);
                await File.AppendAllTextAsync(path, entry.Line).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = exception;
            _channel.Writer.TryComplete(exception);
        }
    }

    private void ApplyRetention()
    {
        var cutoff = DateTimeOffset.UtcNow - _retentionAge;
        foreach (var path in Directory.EnumerateFiles(_directoryPath, $"{_fileNamePrefix}_*.log", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
            {
                File.Delete(path);
            }
        }
    }

    private sealed record LogEntry(DateTimeOffset Timestamp, string Line);
}
