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

        await store.SaveAteraApiKeyAsync("  test-api-key  ", CancellationToken.None);

        var result = await store.LoadAteraApiKeyAsync(CancellationToken.None);
        Assert.Equal("test-api-key", result);
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
                "BaseUrl": "https://snipe.example.com",
                "DefaultStatusId": 2
              },
              "Sync": {
                "IntervalMinutes": 30
              }
            }
            """);
        var store = new LocalAppSettingsStore(filePath);

        await store.SaveAteraApiKeyAsync("test-api-key", CancellationToken.None);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsObject();
        Assert.Equal("test-api-key", root["Atera"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("https://snipe.example.com", root["SnipeIt"]!["BaseUrl"]!.GetValue<string>());
        Assert.Equal(30, root["Sync"]!["IntervalMinutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task SaveAteraApiKeyAsync_ThrowsArgumentException_WhenApiKeyBlank()
    {
        var store = new LocalAppSettingsStore(CreateTempFilePath());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAteraApiKeyAsync(" ", CancellationToken.None));
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
}
