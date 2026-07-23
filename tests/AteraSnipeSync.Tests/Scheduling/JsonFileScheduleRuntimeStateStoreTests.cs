using AteraSnipeSync.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.Scheduling;

/// <summary>
/// Verifies schedule runtime state is persisted independently with strict UTC timestamps and corrupt input fails visibly.
/// </summary>
public sealed class JsonFileScheduleRuntimeStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "AteraSnipeSync.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsStrictUtcZState()
    {
        var path = Path.Combine(_directory, "schedule-state.json");
        var store = CreateStore(path);
        var state = new ScheduleRuntimeState
        {
            RuleFingerprint = "ABC123",
            NextRunUtc = DateTimeOffset.Parse("2026-08-01T15:00:00Z"),
            LastTriggeredUtc = DateTimeOffset.Parse("2026-07-31T15:00:00Z")
        };

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);
        var json = await File.ReadAllTextAsync(path);

        Assert.Equal(state.RuleFingerprint, loaded?.RuleFingerprint);
        Assert.Equal(state.NextRunUtc, loaded?.NextRunUtc);
        Assert.Equal(state.LastTriggeredUtc, loaded?.LastTriggeredUtc);
        Assert.Contains("2026-08-01T15:00:00.0000000Z", json, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RejectsMalformedOrNonUtcState()
    {
        Directory.CreateDirectory(_directory);
        var malformedPath = Path.Combine(_directory, "malformed.json");
        await File.WriteAllTextAsync(malformedPath, "{not-json}");
        var offsetPath = Path.Combine(_directory, "offset.json");
        await File.WriteAllTextAsync(
            offsetPath,
            """{ "ruleFingerprint": "ABC", "nextRunUtc": "2026-08-01T09:00:00.0000000-06:00", "lastTriggeredUtc": null }""");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateStore(malformedPath).LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateStore(offsetPath).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_RejectsNonUtcValues()
    {
        var store = CreateStore(Path.Combine(_directory, "schedule-state.json"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            new ScheduleRuntimeState
            {
                RuleFingerprint = "ABC",
                NextRunUtc = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.FromHours(-6)),
                LastTriggeredUtc = null
            },
            CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static JsonFileScheduleRuntimeStateStore CreateStore(string path)
    {
        return new JsonFileScheduleRuntimeStateStore(
            path,
            NullLogger<JsonFileScheduleRuntimeStateStore>.Instance);
    }
}
