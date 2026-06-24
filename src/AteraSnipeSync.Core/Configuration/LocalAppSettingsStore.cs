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
    /// Returns the shared local log directory used for date-split manual run logs.
    /// </summary>
    public static string GetDefaultLogDirectory()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "AteraSnipeSync", "Logs");
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
    /// Loads reusable manual sync settings from local config, returning null when none are present.
    /// </summary>
    public async Task<ManualSyncSettings?> LoadManualSyncSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
        var ateraSection = root["Atera"] as JsonObject;
        var snipeItSection = root["SnipeIt"] as JsonObject;
        var mappingSection = root["Mapping"] as JsonObject;
        var settings = new ManualSyncSettings
        {
            AteraBaseUrl = ReadOptionalString(ateraSection, "BaseUrl"),
            AteraApiKey = ReadOptionalString(ateraSection, "ApiKey"),
            SnipeItBaseUrl = ReadOptionalString(snipeItSection, "BaseUrl"),
            SnipeItApiToken = ReadOptionalString(snipeItSection, "ApiToken"),
            DefaultCompanyName = ReadOptionalString(mappingSection, "DefaultCompanyName"),
            DefaultManufacturerName = ReadOptionalString(mappingSection, "DefaultManufacturerName"),
            DefaultModelName = ReadOptionalString(mappingSection, "DefaultModelName"),
            DefaultCategoryName = ReadOptionalString(mappingSection, "DefaultCategoryName"),
            DefaultStatusId = ReadOptionalInt(snipeItSection, "DefaultStatusId"),
            CompanyAliases = ReadStringDictionary(mappingSection, "CompanyAliases"),
            MacAddressCustomFieldDbColumnName = ReadOptionalString(snipeItSection, "MacAddressCustomFieldDbColumnName"),
            NameMatchThreshold = ReadOptionalDouble(snipeItSection, "NameMatchThreshold"),
            CreateMissingCompanies = ReadOptionalBool(snipeItSection, "CreateMissingCompanies"),
            CreateMissingModels = ReadOptionalBool(snipeItSection, "CreateMissingModels")
        };

        return HasAnyManualSyncSetting(settings) ? settings : null;
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

        await SaveRootAsync(root, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves reusable manual sync panel settings while preserving unrelated local config sections.
    /// </summary>
    public async Task SaveManualSyncSettingsAsync(
        ManualSyncSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var ateraBaseUrl = RequireSetting(settings.AteraBaseUrl, nameof(settings.AteraBaseUrl));
        var ateraApiKey = RequireSetting(settings.AteraApiKey, nameof(settings.AteraApiKey));
        var snipeItBaseUrl = RequireSetting(settings.SnipeItBaseUrl, nameof(settings.SnipeItBaseUrl));
        var snipeItApiToken = RequireSetting(settings.SnipeItApiToken, nameof(settings.SnipeItApiToken));
        var defaultCompanyName = RequireSetting(settings.DefaultCompanyName, nameof(settings.DefaultCompanyName));
        var defaultManufacturerName = RequireSetting(settings.DefaultManufacturerName, nameof(settings.DefaultManufacturerName));
        var defaultModelName = RequireSetting(settings.DefaultModelName, nameof(settings.DefaultModelName));
        var defaultCategoryName = RequireSetting(settings.DefaultCategoryName, nameof(settings.DefaultCategoryName));
        var defaultStatusId = settings.DefaultStatusId
            ?? throw new ArgumentException("Default status id is required.", nameof(settings));
        var nameMatchThreshold = settings.NameMatchThreshold
            ?? throw new ArgumentException("Name match threshold is required.", nameof(settings));
        if (defaultStatusId <= 0)
        {
            throw new ArgumentException("Default status id must be greater than zero.", nameof(settings));
        }

        if (nameMatchThreshold <= 0 || nameMatchThreshold > 1)
        {
            throw new ArgumentException("Name match threshold must be between 0 and 1.", nameof(settings));
        }

        var root = File.Exists(_filePath)
            ? await LoadRootAsync(cancellationToken).ConfigureAwait(false)
            : new JsonObject();

        var ateraSection = GetOrCreateObject(root, "Atera");
        ateraSection["BaseUrl"] = ateraBaseUrl;
        ateraSection["ApiKey"] = ateraApiKey;

        var snipeItSection = GetOrCreateObject(root, "SnipeIt");
        snipeItSection["BaseUrl"] = snipeItBaseUrl;
        snipeItSection["ApiToken"] = snipeItApiToken;
        snipeItSection["DefaultStatusId"] = defaultStatusId;
        WriteOptionalString(snipeItSection, "MacAddressCustomFieldDbColumnName", settings.MacAddressCustomFieldDbColumnName);
        snipeItSection["NameMatchThreshold"] = nameMatchThreshold;
        snipeItSection["CreateMissingCompanies"] = settings.CreateMissingCompanies ?? false;
        snipeItSection["CreateMissingModels"] = settings.CreateMissingModels ?? false;

        var mappingSection = GetOrCreateObject(root, "Mapping");
        mappingSection["DefaultCompanyName"] = defaultCompanyName;
        mappingSection["DefaultManufacturerName"] = defaultManufacturerName;
        mappingSection["DefaultModelName"] = defaultModelName;
        mappingSection["DefaultCategoryName"] = defaultCategoryName;
        mappingSection["CompanyAliases"] = WriteStringDictionary(settings.CompanyAliases);

        await SaveRootAsync(root, cancellationToken).ConfigureAwait(false);
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

        await SaveRootAsync(root, cancellationToken).ConfigureAwait(false);
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

    private async Task SaveRootAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = root.ToJsonString(WriteOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject section)
        {
            return section;
        }

        section = new JsonObject();
        root[propertyName] = section;
        return section;
    }

    private static string RequireSetting(string? value, string propertyName)
    {
        var trimmedValue = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmedValue)
            ? throw new ArgumentException($"{propertyName} is required.", propertyName)
            : trimmedValue;
    }

    private static string? ReadOptionalString(JsonObject? section, string propertyName)
    {
        var value = section?[propertyName]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? ReadOptionalInt(JsonObject? section, string propertyName)
    {
        return section?[propertyName]?.GetValue<int>();
    }

    private static double? ReadOptionalDouble(JsonObject? section, string propertyName)
    {
        return section?[propertyName]?.GetValue<double>();
    }

    private static bool? ReadOptionalBool(JsonObject? section, string propertyName)
    {
        return section?[propertyName]?.GetValue<bool>();
    }

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(JsonObject? section, string propertyName)
    {
        if (section?[propertyName] is not JsonObject dictionaryObject)
        {
            return new Dictionary<string, string>();
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dictionaryObject)
        {
            var key = item.Key.Trim();
            var value = item.Value?.GetValue<string>()?.Trim();
            if (key.Length > 0 && !string.IsNullOrWhiteSpace(value))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static void WriteOptionalString(JsonObject section, string propertyName, string? value)
    {
        var trimmedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            section.Remove(propertyName);
            return;
        }

        section[propertyName] = trimmedValue;
    }

    private static JsonObject WriteStringDictionary(IReadOnlyDictionary<string, string> values)
    {
        var dictionaryObject = new JsonObject();
        foreach (var item in values)
        {
            var key = RequireSetting(item.Key, nameof(values));
            var value = RequireSetting(item.Value, nameof(values));
            dictionaryObject[key] = value;
        }

        return dictionaryObject;
    }

    private static bool HasAnyManualSyncSetting(ManualSyncSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.AteraBaseUrl)
            || !string.IsNullOrWhiteSpace(settings.AteraApiKey)
            || !string.IsNullOrWhiteSpace(settings.SnipeItBaseUrl)
            || !string.IsNullOrWhiteSpace(settings.SnipeItApiToken)
            || !string.IsNullOrWhiteSpace(settings.DefaultCompanyName)
            || !string.IsNullOrWhiteSpace(settings.DefaultManufacturerName)
            || !string.IsNullOrWhiteSpace(settings.DefaultModelName)
            || !string.IsNullOrWhiteSpace(settings.DefaultCategoryName)
            || settings.DefaultStatusId.HasValue
            || settings.CompanyAliases.Count > 0
            || !string.IsNullOrWhiteSpace(settings.MacAddressCustomFieldDbColumnName)
            || settings.NameMatchThreshold.HasValue
            || settings.CreateMissingCompanies.HasValue
            || settings.CreateMissingModels.HasValue;
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
