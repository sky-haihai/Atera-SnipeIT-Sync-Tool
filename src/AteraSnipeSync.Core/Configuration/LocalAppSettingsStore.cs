using System.Text.Json;
using System.Text.Json.Nodes;
using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Reads and writes local machine configuration while preserving unrelated appsettings sections.
/// </summary>
public sealed class LocalAppSettingsStore
{
    public const string DefaultFileName = "appsettings.local.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public LocalAppSettingsStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Local settings file path is required.", nameof(filePath));
        }

        _filePath = filePath;
    }

    /// <summary>
    /// Returns the shared local config path used by the tray app and worker service.
    /// </summary>
    public static string GetDefaultFilePath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "AteraSnipeSync", DefaultFileName);
    }

    /// <summary>
    /// Builds the standard manual sync preflight output folder for a generated run id.
    /// </summary>
    public static string GetDefaultManualPreflightDirectory(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "AteraSnipeSync", "Preflight", runId.Trim());
    }

    /// <summary>
    /// Loads the saved Atera API key, returning null when no local config or key exists.
    /// </summary>
    public async Task<string?> LoadAteraApiKeyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
        var apiKey = root["Atera"]?["ApiKey"]?.GetValue<string>();

        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    /// <summary>
    /// Saves a trimmed Atera API key to the local config file and keeps all unrelated sections intact.
    /// </summary>
    public async Task SaveAteraApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        var trimmedApiKey = apiKey?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedApiKey))
        {
            throw new ArgumentException("Atera API key is required.", nameof(apiKey));
        }

        var root = File.Exists(_filePath)
            ? await LoadRootAsync(cancellationToken).ConfigureAwait(false)
            : new JsonObject();

        if (root["Atera"] is not JsonObject ateraSection)
        {
            ateraSection = new JsonObject();
            root["Atera"] = ateraSection;
        }

        ateraSection["ApiKey"] = trimmedApiKey;

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = root.ToJsonString(WriteOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the unattended sync schedule from Sync.Schedule, returning null when it has not been configured.
    /// </summary>
    public async Task<SyncScheduleOptions?> LoadSyncScheduleOptionsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
        if (root["Sync"]?["Schedule"] is not JsonObject scheduleSection)
        {
            return null;
        }

        return ReadScheduleOptions(scheduleSection);
    }

    /// <summary>
    /// Saves a validated unattended sync schedule under Sync.Schedule while preserving unrelated settings.
    /// </summary>
    public async Task SaveSyncScheduleOptionsAsync(
        SyncScheduleOptions scheduleOptions,
        CancellationToken cancellationToken)
    {
        ScheduleCalculator.Validate(scheduleOptions);

        var root = File.Exists(_filePath)
            ? await LoadRootAsync(cancellationToken).ConfigureAwait(false)
            : new JsonObject();

        if (root["Sync"] is not JsonObject syncSection)
        {
            syncSection = new JsonObject();
            root["Sync"] = syncSection;
        }

        syncSection["Schedule"] = WriteScheduleOptions(scheduleOptions);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = root.ToJsonString(WriteOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject> LoadRootAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            return node as JsonObject
                ?? throw new InvalidOperationException("Local settings JSON root must be an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Local settings JSON is malformed.", exception);
        }
    }

    private static SyncScheduleOptions ReadScheduleOptions(JsonObject scheduleSection)
    {
        return new SyncScheduleOptions
        {
            Enabled = scheduleSection["Enabled"]?.GetValue<bool>() ?? false,
            Frequency = Enum.Parse<ScheduleFrequency>(RequireString(scheduleSection, "Frequency"), ignoreCase: true),
            TimeZoneId = RequireString(scheduleSection, "TimeZoneId"),
            RunTimes = ReadTimeOnlyArray(scheduleSection, "RunTimes"),
            DaysOfWeek = ReadEnumArray<DayOfWeek>(scheduleSection, "DaysOfWeek"),
            DaysOfMonth = ReadIntArray(scheduleSection, "DaysOfMonth"),
            RunOnLastDayOfMonth = scheduleSection["RunOnLastDayOfMonth"]?.GetValue<bool>() ?? false,
            PreventOverlappingRuns = scheduleSection["PreventOverlappingRuns"]?.GetValue<bool>() ?? true
        };
    }

    private static JsonObject WriteScheduleOptions(SyncScheduleOptions scheduleOptions)
    {
        return new JsonObject
        {
            ["Enabled"] = scheduleOptions.Enabled,
            ["Frequency"] = scheduleOptions.Frequency.ToString(),
            ["TimeZoneId"] = scheduleOptions.TimeZoneId,
            ["RunTimes"] = new JsonArray(scheduleOptions.RunTimes.Select(runTime => JsonValue.Create(runTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))).ToArray<JsonNode?>()),
            ["DaysOfWeek"] = new JsonArray(scheduleOptions.DaysOfWeek.Select(day => JsonValue.Create(day.ToString())).ToArray<JsonNode?>()),
            ["DaysOfMonth"] = new JsonArray(scheduleOptions.DaysOfMonth.Select(day => JsonValue.Create(day)).ToArray<JsonNode?>()),
            ["RunOnLastDayOfMonth"] = scheduleOptions.RunOnLastDayOfMonth,
            ["PreventOverlappingRuns"] = scheduleOptions.PreventOverlappingRuns
        };
    }

    private static string RequireString(JsonObject section, string propertyName)
    {
        var value = section[propertyName]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Schedule property '{propertyName}' is required.")
            : value;
    }

    private static IReadOnlyList<TimeOnly> ReadTimeOnlyArray(JsonObject section, string propertyName)
    {
        if (section[propertyName] is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(item => TimeOnly.ParseExact(item!.GetValue<string>(), "HH:mm", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    private static IReadOnlyList<TEnum> ReadEnumArray<TEnum>(JsonObject section, string propertyName)
        where TEnum : struct
    {
        if (section[propertyName] is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(item => Enum.Parse<TEnum>(item!.GetValue<string>(), ignoreCase: true))
            .ToList();
    }

    private static IReadOnlyList<int> ReadIntArray(JsonObject section, string propertyName)
    {
        if (section[propertyName] is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(item => item!.GetValue<int>())
            .ToList();
    }
}
