using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AteraSnipeSync.Core.Common;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.Scheduling;

/// <summary>
/// Atomically stores one schedule runtime state document using explicit UTC Z timestamps and no configuration secrets.
/// </summary>
public sealed class JsonFileScheduleRuntimeStateStore : IScheduleRuntimeStateStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly ILogger<JsonFileScheduleRuntimeStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileScheduleRuntimeStateStore(
        string filePath,
        ILogger<JsonFileScheduleRuntimeStateStore> logger)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Schedule state file path is required.", nameof(filePath));
        }

        ArgumentNullException.ThrowIfNull(logger);
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>
    /// Returns the standard machine-wide state path used by the Windows Worker.
    /// </summary>
    public static string GetDefaultFilePath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "AteraSnipeSync", "schedule-state.json");
    }

    /// <summary>
    /// Reads and validates the complete state; malformed content is reported as invalid data for the manager to rebuild.
    /// </summary>
    public async Task<ScheduleRuntimeState?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            await using var stream = File.OpenRead(_filePath);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                as JsonObject
                ?? throw new InvalidDataException("Schedule state JSON root must be an object.");
            var fingerprint = root["ruleFingerprint"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                throw new InvalidDataException("Schedule state rule fingerprint is missing.");
            }

            return new ScheduleRuntimeState
            {
                RuleFingerprint = fingerprint,
                NextRunUtc = ReadUtc(root, "nextRunUtc"),
                LastTriggeredUtc = ReadUtc(root, "lastTriggeredUtc")
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Schedule state JSON is malformed.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates UTC inputs and atomically replaces the state so a crash cannot publish a partial claim.
    /// </summary>
    public async Task SaveAsync(ScheduleRuntimeState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.RuleFingerprint))
        {
            throw new ArgumentException("Schedule state rule fingerprint is required.", nameof(state));
        }

        ValidateUtc(state.NextRunUtc, nameof(state.NextRunUtc));
        ValidateUtc(state.LastTriggeredUtc, nameof(state.LastTriggeredUtc));

        var root = new JsonObject
        {
            ["ruleFingerprint"] = state.RuleFingerprint.Trim(),
            ["nextRunUtc"] = WriteUtc(state.NextRunUtc),
            ["lastTriggeredUtc"] = WriteUtc(state.LastTriggeredUtc)
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicFileWriter.WriteAllTextAsync(
                    _filePath,
                    root.ToJsonString(WriteOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            _logger.LogDebug("Saved Worker schedule runtime state.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTimeOffset? ReadUtc(JsonObject root, string propertyName)
    {
        var text = root[propertyName]?.GetValue<string>();
        if (text is null)
        {
            return null;
        }

        if (!text.EndsWith('Z')
            || !DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value)
            || value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"Schedule state {propertyName} must be an ISO-8601 UTC Z timestamp.");
        }

        return value;
    }

    private static JsonNode? WriteUtc(DateTimeOffset? value)
    {
        return value is null
            ? null
            : JsonValue.Create(value.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void ValidateUtc(DateTimeOffset? value, string propertyName)
    {
        if (value is not null && value.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{propertyName} must use UTC offset zero.", propertyName);
        }
    }
}
