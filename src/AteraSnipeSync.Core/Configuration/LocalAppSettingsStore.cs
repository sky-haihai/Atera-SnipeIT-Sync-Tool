using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.SnipeIt;

namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Reads and writes local machine configuration while preserving unrelated appsettings sections.
/// </summary>
public sealed class LocalAppSettingsStore : ILocalAppSettingsReader
{
    public const string DefaultFileName = "appsettings.local.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

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
        return ReadOptionalString(root["Atera"] as JsonObject, "ApiKey");
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
        var defaultCategoryName = ReadOptionalString(mappingSection, "DefaultCategoryName")
            ?? ReadOptionalString(mappingSection, "DefaultComputerCategoryName");
        var settings = new ManualSyncSettings
        {
            AteraBaseUrl = ReadOptionalString(ateraSection, "BaseUrl"),
            AteraApiKey = ReadOptionalString(ateraSection, "ApiKey"),
            SnipeItBaseUrl = ReadOptionalString(snipeItSection, "BaseUrl"),
            SnipeItApiToken = ReadOptionalString(snipeItSection, "ApiToken"),
            DefaultCompanyName = ReadOptionalString(mappingSection, "DefaultCompanyName"),
            DefaultManufacturerName = ReadOptionalString(mappingSection, "DefaultManufacturerName"),
            DefaultModelName = ReadOptionalString(mappingSection, "DefaultModelName"),
            DefaultCategoryName = defaultCategoryName,
            ModelCategoriesToNormalize = ReadStringArray(snipeItSection, "ModelCategoriesToNormalize"),
            DefaultStatusId = ReadOptionalInt(snipeItSection, "DefaultStatusId"),
            CompanyAliases = ReadStringDictionary(mappingSection, "CompanyAliases"),
            ManufacturerAliases = ReadStringDictionary(mappingSection, "ManufacturerAliases"),
            IgnoredDeviceTypes = ReadStringArray(mappingSection, "IgnoredDeviceTypes"),
            MacAddressCustomFieldDbColumnName = ReadOptionalString(snipeItSection, "MacAddressCustomFieldDbColumnName"),
            MacAddressFieldsetName = ReadOptionalString(snipeItSection, "MacAddressFieldsetName"),
            IgnoredMacAddresses = ReadStringArray(snipeItSection, "IgnoredMacAddresses"),
            NameMatchThreshold = ReadOptionalDouble(snipeItSection, "NameMatchThreshold"),
            CreateMissingCompanies = ReadOptionalBool(snipeItSection, "CreateMissingCompanies"),
            CreateMissingModels = ReadOptionalBool(snipeItSection, "CreateMissingModels")
        };

        return HasAnyManualSyncSetting(settings) ? settings : null;
    }

    /// <summary>
    /// Loads unattended settings without accepting or modifying legacy plaintext secrets; credentials must come from environment variables.
    /// </summary>
    public async Task<ManualSyncSettings?> LoadWorkerSyncSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
        var ateraSection = root["Atera"] as JsonObject;
        var snipeItSection = root["SnipeIt"] as JsonObject;
        if (ReadOptionalString(ateraSection, "ApiKey") is not null
            || ReadOptionalString(snipeItSection, "ApiToken") is not null)
        {
            throw new InvalidOperationException(
                $"WorkerService refuses plaintext API credentials in '{_filePath}'. Save the config from TrayApp to remove them, then set {SecretEnvironmentVariables.AteraApiKey} and {SecretEnvironmentVariables.SnipeItApiToken} for the WorkerService process.");
        }

        var mappingSection = root["Mapping"] as JsonObject;
        var defaultCategoryName = ReadOptionalString(mappingSection, "DefaultCategoryName")
            ?? ReadOptionalString(mappingSection, "DefaultComputerCategoryName");
        var settings = new ManualSyncSettings
        {
            AteraBaseUrl = ReadOptionalString(ateraSection, "BaseUrl"),
            AteraApiKey = ReadEnvironmentSecret(SecretEnvironmentVariables.AteraApiKey),
            SnipeItBaseUrl = ReadOptionalString(snipeItSection, "BaseUrl"),
            SnipeItApiToken = ReadEnvironmentSecret(SecretEnvironmentVariables.SnipeItApiToken),
            DefaultCompanyName = ReadOptionalString(mappingSection, "DefaultCompanyName"),
            DefaultManufacturerName = ReadOptionalString(mappingSection, "DefaultManufacturerName"),
            DefaultModelName = ReadOptionalString(mappingSection, "DefaultModelName"),
            DefaultCategoryName = defaultCategoryName,
            ModelCategoriesToNormalize = ReadStringArray(snipeItSection, "ModelCategoriesToNormalize"),
            DefaultStatusId = ReadOptionalInt(snipeItSection, "DefaultStatusId"),
            CompanyAliases = ReadStringDictionary(mappingSection, "CompanyAliases"),
            ManufacturerAliases = ReadStringDictionary(mappingSection, "ManufacturerAliases"),
            IgnoredDeviceTypes = ReadStringArray(mappingSection, "IgnoredDeviceTypes"),
            MacAddressCustomFieldDbColumnName = ReadOptionalString(snipeItSection, "MacAddressCustomFieldDbColumnName"),
            MacAddressFieldsetName = ReadOptionalString(snipeItSection, "MacAddressFieldsetName"),
            IgnoredMacAddresses = ReadStringArray(snipeItSection, "IgnoredMacAddresses"),
            NameMatchThreshold = ReadOptionalDouble(snipeItSection, "NameMatchThreshold"),
            CreateMissingCompanies = ReadOptionalBool(snipeItSection, "CreateMissingCompanies"),
            CreateMissingModels = ReadOptionalBool(snipeItSection, "CreateMissingModels")
        };

        return HasAnyManualSyncSetting(settings) ? settings : null;
    }

    /// <summary>
    /// Saves an Atera API key to the local Manual Sync test configuration while preserving unrelated sections.
    /// </summary>
    public async Task SaveAteraApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var trimmedApiKey = apiKey?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedApiKey))
        {
            throw new ArgumentException("Atera API key is required.", nameof(apiKey));
        }

        await UpdateRootAsync(root =>
        {
            var ateraSection = GetOrCreateObject(root, "Atera");
            ateraSection["ApiKey"] = trimmedApiKey;
        }, cancellationToken).ConfigureAwait(false);
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
        var macCustomFieldDbColumnName = settings.MacAddressCustomFieldDbColumnName?.Trim();
        var macAddressFieldsetName = settings.MacAddressFieldsetName?.Trim();
        if (string.IsNullOrWhiteSpace(macCustomFieldDbColumnName)
            != string.IsNullOrWhiteSpace(macAddressFieldsetName))
        {
            throw new ArgumentException(
                "MAC address custom field DB column and MAC Fieldset name must be configured together.",
                nameof(settings));
        }

        var ignoredMacAddresses = NormalizeIgnoredMacAddresses(settings.IgnoredMacAddresses);
        var modelCategoriesToNormalize = settings.ModelCategoriesToNormalize
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        if (modelCategoriesToNormalize.Count == 0)
        {
            throw new ArgumentException("At least one model category to normalize is required.", nameof(settings));
        }

        if (defaultStatusId <= 0)
        {
            throw new ArgumentException("Default status id must be greater than zero.", nameof(settings));
        }

        if (nameMatchThreshold <= 0 || nameMatchThreshold > 1)
        {
            throw new ArgumentException("Name match threshold must be between 0 and 1.", nameof(settings));
        }

        await UpdateRootAsync(root =>
        {
            var ateraSection = GetOrCreateObject(root, "Atera");
            ateraSection["BaseUrl"] = ateraBaseUrl;
            ateraSection["ApiKey"] = ateraApiKey;

            var snipeItSection = GetOrCreateObject(root, "SnipeIt");
            snipeItSection["BaseUrl"] = snipeItBaseUrl;
            snipeItSection["ApiToken"] = snipeItApiToken;
            snipeItSection["DefaultStatusId"] = defaultStatusId;
            snipeItSection["ModelCategoriesToNormalize"] = WriteStringArray(modelCategoriesToNormalize);
            WriteOptionalString(snipeItSection, "MacAddressCustomFieldDbColumnName", macCustomFieldDbColumnName);
            WriteOptionalString(snipeItSection, "MacAddressFieldsetName", macAddressFieldsetName);
            snipeItSection["IgnoredMacAddresses"] = WriteStringArray(ignoredMacAddresses);
            snipeItSection["NameMatchThreshold"] = nameMatchThreshold;
            snipeItSection["CreateMissingCompanies"] = settings.CreateMissingCompanies ?? false;
            snipeItSection["CreateMissingModels"] = settings.CreateMissingModels ?? false;

            var mappingSection = GetOrCreateObject(root, "Mapping");
            mappingSection["DefaultCompanyName"] = defaultCompanyName;
            mappingSection["DefaultManufacturerName"] = defaultManufacturerName;
            mappingSection["DefaultModelName"] = defaultModelName;
            mappingSection["DefaultCategoryName"] = defaultCategoryName;
            mappingSection.Remove("DefaultComputerCategoryName");
            mappingSection.Remove("DefaultServerCategoryName");
            mappingSection["CompanyAliases"] = WriteStringDictionary(settings.CompanyAliases);
            mappingSection["ManufacturerAliases"] = WriteStringDictionary(settings.ManufacturerAliases);
            mappingSection["IgnoredDeviceTypes"] = WriteStringArray(settings.IgnoredDeviceTypes);
        }, cancellationToken).ConfigureAwait(false);
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
    /// Loads whether unattended runs are dry-run, defaulting to the fail-safe value true.
    /// </summary>
    public async Task<bool> LoadSyncDryRunAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return true;
        }

        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
        return root["Sync"]?["DryRun"]?.GetValue<bool>() ?? true;
    }

    /// <summary>
    /// Loads notification enablement and subscriptions, defaulting to a disabled publisher configuration.
    /// </summary>
    public async Task<NotificationConfig> LoadNotificationConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return DisabledNotifications();
        }

        var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
        if (root["Notifications"] is not JsonObject section)
        {
            return DisabledNotifications();
        }

        return new NotificationConfig
        {
            Enabled = ReadOptionalBool(section, "Enabled") ?? false,
            OnEvents = ReadStringArray(section, "OnEvents"),
            EmailTo = ReadOptionalString(section, "EmailTo"),
            WebhookUrl = ReadOptionalString(section, "WebhookUrl")
        };
    }

    /// <summary>
    /// Saves a validated unattended sync schedule under Sync.Schedule while preserving unrelated settings.
    /// </summary>
    public async Task SaveSyncScheduleOptionsAsync(
        SyncScheduleOptions scheduleOptions,
        CancellationToken cancellationToken)
    {
        ScheduleCalculator.Validate(scheduleOptions);

        await UpdateRootAsync(root =>
        {
            var syncSection = GetOrCreateObject(root, "Sync");
            syncSection["Schedule"] = WriteScheduleOptions(scheduleOptions);
        }, cancellationToken).ConfigureAwait(false);
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
        await AtomicFileWriter.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateRootAsync(Action<JsonObject> update, CancellationToken cancellationToken)
    {
        var gate = WriteGates.GetOrAdd(Path.GetFullPath(_filePath), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var root = File.Exists(_filePath)
                ? await LoadRootAsync(cancellationToken).ConfigureAwait(false)
                : new JsonObject();
            update(root);
            await SaveRootAsync(root, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lockPath = _filePath + ".lock";
        var startedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow - startedAt < LockTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }

            if (DateTimeOffset.UtcNow - startedAt >= LockTimeout)
            {
                throw new TimeoutException($"Timed out waiting for local settings lock '{lockPath}'.");
            }
        }
    }

    private static string? ReadEnvironmentSecret(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName, EnvironmentVariableTarget.Process)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
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

    private static IReadOnlyList<string> ReadStringArray(JsonObject? section, string propertyName)
    {
        if (section?[propertyName] is not JsonArray array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            var value = item?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
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

    private static JsonArray WriteStringArray(IReadOnlyList<string> values)
    {
        return new JsonArray(values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => JsonValue.Create(value))
            .ToArray<JsonNode?>());
    }

    /// <summary>
    /// Validates persisted ignored MAC values and stores one deterministic display-form entry per address.
    /// </summary>
    private static IReadOnlyList<string> NormalizeIgnoredMacAddresses(IReadOnlyList<string> values)
    {
        var normalizedValues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var comparable = MacAddressNormalizer.NormalizeComparable(value);
            if (comparable is null)
            {
                throw new ArgumentException($"Ignored MAC address '{value}' is invalid.", nameof(values));
            }

            if (seen.Add(comparable))
            {
                normalizedValues.Add(MacAddressNormalizer.NormalizeDisplay(comparable)!);
            }
        }

        return normalizedValues;
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
            || settings.ModelCategoriesToNormalize.Count > 0
            || settings.DefaultStatusId.HasValue
            || settings.CompanyAliases.Count > 0
            || settings.ManufacturerAliases.Count > 0
            || settings.IgnoredDeviceTypes.Count > 0
            || !string.IsNullOrWhiteSpace(settings.MacAddressCustomFieldDbColumnName)
            || !string.IsNullOrWhiteSpace(settings.MacAddressFieldsetName)
            || settings.IgnoredMacAddresses.Count > 0
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

    private static NotificationConfig DisabledNotifications()
    {
        return new NotificationConfig { Enabled = false, OnEvents = [] };
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
