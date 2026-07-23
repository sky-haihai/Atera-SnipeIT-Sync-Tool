using System.Text.Json.Nodes;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Scheduling;

namespace AteraSnipeSync.Tests.Configuration;

/// <summary>
/// Verifies local settings persistence without touching machine-level config or real API keys.
/// </summary>
public sealed class LocalAppSettingsStoreTests
{
    [Fact]
    public async Task LoadAteraApiKeyAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var store = new LocalAppSettingsStore(CreateTempFilePath());

        var result = await store.LoadAteraApiKeyAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAteraApiKeyAsync_CreatesLocalConfigWithAteraApiKey()
    {
        var filePath = CreateTempFilePath();
        var store = new LocalAppSettingsStore(filePath);

        await store.SaveAteraApiKeyAsync(" test-api-key ", CancellationToken.None);

        var reloadedStore = new LocalAppSettingsStore(filePath);
        Assert.Equal("test-api-key", await reloadedStore.LoadAteraApiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAteraApiKeyAsync_PreservesExistingSections()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "SnipeIt": {
                "BaseUrl": "https://snipe.example.com/api/v1"
              }
            }
            """);
        var store = new LocalAppSettingsStore(filePath);

        await store.SaveAteraApiKeyAsync("test-api-key", CancellationToken.None);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsObject();
        Assert.Equal("test-api-key", root["Atera"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("https://snipe.example.com/api/v1", root["SnipeIt"]!["BaseUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAteraApiKeyAsync_ThrowsArgumentException_WhenApiKeyBlank()
    {
        var store = new LocalAppSettingsStore(CreateTempFilePath());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAteraApiKeyAsync(" ", CancellationToken.None));
    }

    [Fact]
    public async Task LoadSyncAppSettingsAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var store = new LocalAppSettingsStore(CreateTempFilePath());

        var result = await store.LoadSyncAppSettingsAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveSyncAppSettingsAsync_SavesCompleteConfig()
    {
        var filePath = CreateTempFilePath();
        var store = new LocalAppSettingsStore(filePath);

        await store.SaveSyncAppSettingsAsync(CreateSyncAppSettings(), CancellationToken.None);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsObject();
        Assert.Equal("https://app.atera.com/api/v3", root["Atera"]!["BaseUrl"]!.GetValue<string>());
        Assert.Equal("atera-key", root["Atera"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("https://snipe.example.com/api/v1", root["SnipeIt"]!["BaseUrl"]!.GetValue<string>());
        Assert.Equal("snipe-token", root["SnipeIt"]!["ApiToken"]!.GetValue<string>());
        Assert.Equal(2, root["SnipeIt"]!["DefaultStatusId"]!.GetValue<int>());
        Assert.Equal(
            ["Server", "Laptop", "Desktop"],
            root["SnipeIt"]!["ModelCategoriesToNormalize"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(
            "Assets with MAC Address",
            root["SnipeIt"]!["MacAddressFieldsetName"]!.GetValue<string>());
        Assert.Equal("_snipeit_mac_address_1", root["SnipeIt"]!["MacAddressCustomFieldDbColumnName"]!.GetValue<string>());
        Assert.Equal(
            ["00:09:0F:AA:00:01", "00:09:0F:FE:00:01"],
            root["SnipeIt"]!["IgnoredMacAddresses"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(0.92D, root["SnipeIt"]!["NameMatchThreshold"]!.GetValue<double>());
        Assert.True(root["SnipeIt"]!["CreateMissingCompanies"]!.GetValue<bool>());
        Assert.False(root["SnipeIt"]!["CreateMissingModels"]!.GetValue<bool>());
        Assert.Null(root["Mapping"]!["DefaultCompanyName"]);
        Assert.Null(root["Mapping"]!["DefaultManufacturerName"]);
        Assert.Null(root["Mapping"]!["DefaultModelName"]);
        Assert.Equal("Computer", root["Mapping"]!["DefaultCategoryName"]!.GetValue<string>());
        Assert.Null(root["Mapping"]!["DefaultComputerCategoryName"]);
        Assert.Null(root["Mapping"]!["DefaultServerCategoryName"]);
        Assert.Equal(
            "Moore Equine Veterinary Centre",
            root["Mapping"]!["CompanyAliases"]!["Moore Equine Veterinary Centre - AR"]!.GetValue<string>());
        Assert.Equal(
            "Dell",
            root["Mapping"]!["ManufacturerAliases"]!["Dell Inc."]!.GetValue<string>());
        Assert.Null(root["Sync"]!["DryRun"]);
        Assert.Equal("Weekly", root["Sync"]!["Schedule"]!["Frequency"]!.GetValue<string>());
        var notifications = root["Notifications"]!;
        Assert.True(notifications["Enabled"]!.GetValue<bool>());
        Assert.Equal("smtp.example.test", notifications["SmtpHost"]!.GetValue<string>());
        Assert.Equal(587, notifications["SmtpPort"]!.GetValue<int>());
        Assert.True(notifications["SmtpUseSsl"]!.GetValue<bool>());
        Assert.Equal("mailer", notifications["SmtpUsername"]!.GetValue<string>());
        Assert.Equal("smtp-secret", notifications["SmtpPassword"]!.GetValue<string>());
        Assert.Equal("sync@example.test", notifications["EmailFrom"]!.GetValue<string>());
        Assert.Equal("operator@example.test", notifications["EmailTo"]!.GetValue<string>());
        Assert.Equal("GenericJson", notifications["WebhookPayloadFormat"]!.GetValue<string>());
        Assert.Equal("https://hooks.example.test/notify", notifications["WebhookUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task LoadSyncAppSettingsAsync_ReturnsSavedCompleteConfig()
    {
        var filePath = CreateTempFilePath();
        var store = new LocalAppSettingsStore(filePath);
        await store.SaveSyncAppSettingsAsync(CreateSyncAppSettings(), CancellationToken.None);

        var reloadedStore = new LocalAppSettingsStore(filePath);
        var result = await reloadedStore.LoadSyncAppSettingsAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https://app.atera.com/api/v3", result.AteraBaseUrl);
        Assert.Equal("atera-key", result.AteraApiKey);
        Assert.Equal("https://snipe.example.com/api/v1", result.SnipeItBaseUrl);
        Assert.Equal("snipe-token", result.SnipeItApiToken);
        Assert.Equal(SyncApplicationDefaults.CompanyName, result.DefaultCompanyName);
        Assert.Equal(SyncApplicationDefaults.ManufacturerName, result.DefaultManufacturerName);
        Assert.Equal(SyncApplicationDefaults.ModelName, result.DefaultModelName);
        Assert.Equal("Computer", result.DefaultCategoryName);
        Assert.Equal(["Server", "Laptop", "Desktop"], result.ModelCategoriesToNormalize);
        Assert.Equal(2, result.DefaultStatusId);
        Assert.Equal("Assets with MAC Address", result.MacAddressFieldsetName);
        Assert.Equal("_snipeit_mac_address_1", result.MacAddressCustomFieldDbColumnName);
        Assert.Equal(["00:09:0F:AA:00:01", "00:09:0F:FE:00:01"], result.IgnoredMacAddresses);
        Assert.Equal(0.92D, result.NameMatchThreshold);
        Assert.True(result.CreateMissingCompanies);
        Assert.False(result.CreateMissingModels);
        Assert.Equal(
            "Moore Equine Veterinary Centre",
            result.CompanyAliases["Moore Equine Veterinary Centre - AR"]);
        Assert.Equal("Dell", result.ManufacturerAliases["Dell Inc."]);
        Assert.True(result.Notifications.Enabled);
        Assert.Equal("smtp.example.test", result.Notifications.SmtpHost);
        Assert.Equal(587, result.Notifications.SmtpPort);
        Assert.True(result.Notifications.SmtpUseSsl);
        Assert.Equal("mailer", result.Notifications.SmtpUsername);
        Assert.Equal("smtp-secret", result.Notifications.SmtpPassword);
        Assert.Equal("sync@example.test", result.Notifications.EmailFrom);
        Assert.Equal("operator@example.test", result.Notifications.EmailTo);
        Assert.Equal(WebhookPayloadFormat.GenericJson, result.Notifications.WebhookPayloadFormat);
        Assert.Equal("https://hooks.example.test/notify", result.Notifications.WebhookUrl);
    }

    [Fact]
    public async Task LoadNotificationConfigAsync_DefaultsLegacyWebhookFormatToTeamsAdaptiveCard()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "Notifications": {
                "WebhookUrl": "https://example.logic.azure.com/workflows/test"
              }
            }
            """);

        var result = await new LocalAppSettingsStore(filePath)
            .LoadNotificationConfigAsync(CancellationToken.None);

        Assert.Equal(WebhookPayloadFormat.TeamsAdaptiveCard, result.WebhookPayloadFormat);
    }

    [Fact]
    public async Task LoadSyncAppSettingsAsync_IgnoresLegacyCustomUnknownFallbacks()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "Mapping": {
                "DefaultCompanyName": "Legacy Company",
                "DefaultManufacturerName": "Legacy Manufacturer",
                "DefaultModelName": "Legacy Model",
                "DefaultCategoryName": "Computer"
              }
            }
            """);
        var store = new LocalAppSettingsStore(filePath);

        var result = await store.LoadSyncAppSettingsAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(SyncApplicationDefaults.CompanyName, result.DefaultCompanyName);
        Assert.Equal(SyncApplicationDefaults.ManufacturerName, result.DefaultManufacturerName);
        Assert.Equal(SyncApplicationDefaults.ModelName, result.DefaultModelName);
    }

    [Fact]
    public async Task LoadNotificationConfigAsync_RejectsUnknownWebhookFormat()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "Notifications": {
                "WebhookPayloadFormat": "UnknownFormat"
              }
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new LocalAppSettingsStore(filePath).LoadNotificationConfigAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadSyncAppSettingsAsync_UsesComputerFieldAsFallbackForSplitCategoryConfig()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "Mapping": {
                "DefaultComputerCategoryName": "Legacy Computer",
                "DefaultServerCategoryName": "Legacy Server"
              }
            }
            """);
        var store = new LocalAppSettingsStore(filePath);

        var result = await store.LoadSyncAppSettingsAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Legacy Computer", result.DefaultCategoryName);
    }

    [Fact]
    public async Task SaveSyncAppSettingsAsync_SavesIgnoredMacAddresses()
    {
        var filePath = CreateTempFilePath();
        var store = new LocalAppSettingsStore(filePath);

        await store.SaveSyncAppSettingsAsync(CreateSyncAppSettings(), CancellationToken.None);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsObject();
        Assert.Equal(
            ["00:09:0F:AA:00:01", "00:09:0F:FE:00:01"],
            root["SnipeIt"]!["IgnoredMacAddresses"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public async Task SaveSyncAppSettingsAsync_PreservesUneditedProperties()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "Notifications": {
                "Enabled": true,
                "Custom": "keep"
              },
              "Sync": {
                "DryRun": true,
                "IntervalMinutes": 30
              }
            }
            """);
        var store = new LocalAppSettingsStore(filePath);

        await store.SaveSyncAppSettingsAsync(CreateSyncAppSettings(), CancellationToken.None);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsObject();
        Assert.True(root["Notifications"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("keep", root["Notifications"]!["Custom"]!.GetValue<string>());
        Assert.Equal(30, root["Sync"]!["IntervalMinutes"]!.GetValue<int>());
        Assert.Null(root["Sync"]!["DryRun"]);
        Assert.Equal("atera-key", root["Atera"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("snipe-token", root["SnipeIt"]!["ApiToken"]!.GetValue<string>());

    }

    [Fact]
    public async Task LoadWorkerSyncSettingsAsync_LoadsPlaintextJsonCredentials_WithoutChangingFile()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        const string json =
            """
            {
              "Atera": { "ApiKey": "legacy-key", "BaseUrl": "https://app.atera.com/api/v3" },
              "SnipeIt": { "ApiToken": "legacy-token", "BaseUrl": "https://snipe.example.com/api/v1" }
            }
            """;
        await File.WriteAllTextAsync(filePath, json);
        var store = new LocalAppSettingsStore(filePath);

        var settings = await store.LoadWorkerSyncSettingsAsync(CancellationToken.None);

        Assert.NotNull(settings);
        Assert.Equal("legacy-key", settings.AteraApiKey);
        Assert.Equal("legacy-token", settings.SnipeItApiToken);
        Assert.Equal(json, await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task SaveSyncAppSettingsAsync_ThrowsArgumentException_WhenSnipeTokenBlank()
    {
        var store = new LocalAppSettingsStore(CreateTempFilePath());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveSyncAppSettingsAsync(
                CreateSyncAppSettings(snipeItApiToken: " "),
                CancellationToken.None));
    }

    [Fact]
    public async Task SaveSyncAppSettingsAsync_ThrowsArgumentException_WhenNormalizeCategoriesAreEmpty()
    {
        var store = new LocalAppSettingsStore(CreateTempFilePath());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveSyncAppSettingsAsync(
                CreateSyncAppSettings(modelCategoriesToNormalize: []),
                CancellationToken.None));
    }

    [Fact]
    public async Task SaveSyncAppSettingsAsync_ThrowsArgumentException_WhenIgnoredMacIsInvalid()
    {
        var store = new LocalAppSettingsStore(CreateTempFilePath());
        var settings = CreateSyncAppSettings();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveSyncAppSettingsAsync(
                new SyncAppSettings
                {
                    AteraBaseUrl = settings.AteraBaseUrl,
                    AteraApiKey = settings.AteraApiKey,
                    SnipeItBaseUrl = settings.SnipeItBaseUrl,
                    SnipeItApiToken = settings.SnipeItApiToken,
                    DefaultCompanyName = settings.DefaultCompanyName,
                    DefaultManufacturerName = settings.DefaultManufacturerName,
                    DefaultModelName = settings.DefaultModelName,
                    DefaultCategoryName = settings.DefaultCategoryName,
                    ModelCategoriesToNormalize = settings.ModelCategoriesToNormalize,
                    DefaultStatusId = settings.DefaultStatusId,
                    CompanyAliases = settings.CompanyAliases,
                    ManufacturerAliases = settings.ManufacturerAliases,
                    IgnoredDeviceTypes = settings.IgnoredDeviceTypes,
                    MacAddressFieldsetName = settings.MacAddressFieldsetName,
                    MacAddressCustomFieldDbColumnName = settings.MacAddressCustomFieldDbColumnName,
                    IgnoredMacAddresses = ["not-a-mac"],
                    NameMatchThreshold = settings.NameMatchThreshold,
                    CreateMissingCompanies = settings.CreateMissingCompanies,
                    CreateMissingModels = settings.CreateMissingModels,
                    Schedule = settings.Schedule,
                    Notifications = settings.Notifications
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task LoadAteraApiKeyAsync_ThrowsInvalidOperationException_WhenJsonMalformed()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "{ not-json");
        var store = new LocalAppSettingsStore(filePath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.LoadAteraApiKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveSyncScheduleOptionsAsync_PreservesExistingSections()
    {
        var filePath = CreateTempFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            """
            {
              "Atera": {
                "ApiKey": "test-api-key"
              },
              "SnipeIt": {
                "BaseUrl": "https://snipe.example.com"
              }
            }
            """);
        var store = new LocalAppSettingsStore(filePath);

        await store.SaveSyncScheduleOptionsAsync(CreateScheduleOptions(), CancellationToken.None);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsObject();
        Assert.Equal("test-api-key", root["Atera"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("https://snipe.example.com", root["SnipeIt"]!["BaseUrl"]!.GetValue<string>());
        Assert.Equal("Weekly", root["Sync"]!["Schedule"]!["Frequency"]!.GetValue<string>());
    }

    [Fact]
    public async Task LoadSyncScheduleOptionsAsync_ReturnsSavedSchedule()
    {
        var filePath = CreateTempFilePath();
        var store = new LocalAppSettingsStore(filePath);
        await store.SaveSyncScheduleOptionsAsync(CreateScheduleOptions(), CancellationToken.None);

        var result = await store.LoadSyncScheduleOptionsAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Enabled);
        Assert.Equal(ScheduleFrequency.Weekly, result.Frequency);
        Assert.Equal([new TimeOnly(8, 0), new TimeOnly(17, 30)], result.RunTimes);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], result.DaysOfWeek);
        Assert.Equal([1, 15], result.DaysOfMonth);
    }

    [Fact]
    public async Task SaveSyncScheduleOptionsAsync_RejectsInvalidWeeklySchedule()
    {
        var store = new LocalAppSettingsStore(CreateTempFilePath());
        var invalidSchedule = new SyncScheduleOptions
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            TimeZoneId = "UTC",
            RunTimes = [new TimeOnly(8, 0)],
            DaysOfWeek = [],
            PreventOverlappingRuns = true
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveSyncScheduleOptionsAsync(invalidSchedule, CancellationToken.None));
    }

    [Fact]
    public void GetDefaultManualPreflightDirectory_UsesProgramDataPreflightRunFolder()
    {
        var result = LocalAppSettingsStore.GetDefaultManualPreflightDirectory("run-123");

        Assert.EndsWith(
            Path.Combine("AteraSnipeSync", "Preflight", "run-123"),
            result,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefaultLogDirectory_UsesProgramDataLogsFolder()
    {
        var result = LocalAppSettingsStore.GetDefaultLogDirectory();

        Assert.EndsWith(
            Path.Combine("AteraSnipeSync", "Logs"),
            result,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempFilePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "AteraSnipeSync.Tests",
            Guid.NewGuid().ToString("N"),
            LocalAppSettingsStore.DefaultFileName);
    }

    private static SyncScheduleOptions CreateScheduleOptions()
    {
        return new SyncScheduleOptions
        {
            Enabled = true,
            Frequency = ScheduleFrequency.Weekly,
            TimeZoneId = "UTC",
            RunTimes = [new TimeOnly(8, 0), new TimeOnly(17, 30)],
            DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Friday],
            DaysOfMonth = [1, 15],
            PreventOverlappingRuns = true
        };
    }

    private static SyncAppSettings CreateSyncAppSettings(
        string? snipeItApiToken = "snipe-token",
        IReadOnlyList<string>? modelCategoriesToNormalize = null)
    {
        return new SyncAppSettings
        {
            AteraBaseUrl = " https://app.atera.com/api/v3 ",
            AteraApiKey = " atera-key ",
            SnipeItBaseUrl = " https://snipe.example.com/api/v1 ",
            SnipeItApiToken = snipeItApiToken,
            DefaultCompanyName = " Default Company ",
            DefaultManufacturerName = " Default Manufacturer ",
            DefaultModelName = " Default Model ",
            DefaultCategoryName = " Computer ",
            ModelCategoriesToNormalize = modelCategoriesToNormalize ?? [" Server ", " Laptop ", " Desktop "],
            DefaultStatusId = 2,
            CompanyAliases = new Dictionary<string, string>
            {
                ["Moore Equine Veterinary Centre - AR"] = "Moore Equine Veterinary Centre"
            },
            ManufacturerAliases = new Dictionary<string, string>
            {
                ["Dell Inc."] = "Dell"
            },
            MacAddressFieldsetName = " Assets with MAC Address ",
            MacAddressCustomFieldDbColumnName = " _snipeit_mac_address_1 ",
            IgnoredMacAddresses = [" 00-09-0f-aa-00-01 ", "0009.0faa.0001", "0009.0ffe.0001"],
            NameMatchThreshold = 0.92D,
            CreateMissingCompanies = true,
            CreateMissingModels = false,
            Schedule = CreateScheduleOptions(),
            Notifications = new NotificationConfig
            {
                Enabled = true,
                OnEvents = ["ScheduledSyncCompleted", "ScheduledSyncFailed"],
                SmtpHost = "smtp.example.test",
                SmtpPort = 587,
                SmtpUseSsl = true,
                SmtpUsername = "mailer",
                SmtpPassword = "smtp-secret",
                EmailFrom = "sync@example.test",
                EmailTo = "operator@example.test",
                WebhookPayloadFormat = WebhookPayloadFormat.GenericJson,
                WebhookUrl = "https://hooks.example.test/notify"
            }
        };
    }
}
