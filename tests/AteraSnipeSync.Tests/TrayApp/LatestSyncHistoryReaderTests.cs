using AteraSnipeSync.TrayApp;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies the Worker-offline fallback reads only a compact latest-history summary from a temporary controlled directory.
/// </summary>
public sealed class LatestSyncHistoryReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "AteraSnipeSyncTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadSummaryAsync_ReturnsNewestSanitizedCounts()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "SyncResult_20260717_120000_0000000Z.json"),
            """
            {
              "run": {
                "result": "PartialSuccess",
                "finishedAtUtc": "2026-07-17T12:00:00Z"
              },
              "summary": {
                "pulled": 10,
                "mapped": 9,
                "assetsCreated": 2,
                "assetsUpdated": 3,
                "assetsSkipped": 4,
                "assetsDeleted": 1,
                "assetsFailed": 0
              }
            }
            """);

        var summary = await LatestSyncHistoryReader.ReadSummaryAsync(
            _directory,
            CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero), summary.FinishedAtUtc);
        Assert.Equal(2, summary.Created);
        Assert.Equal(3, summary.Updated);
        Assert.Equal(4, summary.NoChange);
        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task ReadSummaryAsync_DefaultsMissingDeletedCountToZero()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "SyncResult_20260717_120000_0000000Z.json"),
            """
            {
              "run": { "result": "Failed" },
              "summary": {
                "assetsCreated": 0,
                "assetsUpdated": 0,
                "assetsSkipped": 0
              }
            }
            """);

        var summary = await LatestSyncHistoryReader.ReadSummaryAsync(
            _directory,
            CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Deleted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
