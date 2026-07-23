using AteraSnipeSync.Core.Common;
using AteraSnipeSync.TrayApp;

namespace AteraSnipeSync.Tests.TrayApp;

/// <summary>
/// Verifies lossless detailed file logging and the intentionally minimal sync Dashboard milestone policy.
/// </summary>
public sealed class SyncLoggingTests
{
    [Fact]
    public async Task DailyLogWriter_WritesEveryAcceptedEntry_WhenVolumeExceedsPreviousCapacity()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"atera-snipe-log-{Guid.NewGuid():N}");
        var timestamp = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        const int entryCount = 3_000;

        try
        {
            await using (var writer = new DailyLogWriter(directoryPath))
            {
                for (var index = 0; index < entryCount; index++)
                {
                    Assert.True(writer.TryWrite(timestamp, $"entry-{index}{Environment.NewLine}"));
                }
            }

            var lines = await File.ReadAllLinesAsync(
                Path.Combine(directoryPath, "ManualSync_20260716.log"));

            Assert.Equal(entryCount, lines.Length);
            Assert.Equal("entry-0", lines[0]);
            Assert.Equal($"entry-{entryCount - 1}", lines[^1]);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    [Fact]
    public void UiStageTracker_SuppressesRecordDetails_AndEmitsOrderedMilestonesOnce()
    {
        var tracker = new SyncUiStageTracker();
        var messages = new List<string> { tracker.Start() };

        Assert.Null(tracker.Observe(Update("AteraPull", "Requesting Atera agents page 1.")));
        Assert.Null(tracker.Observe(Update("Sync", "Mapping Atera agents into Snipe-IT asset records.")));
        Assert.Null(tracker.Observe(Update("SnipeImport", "Loading Snipe-IT hardware snapshot with DEVICE-001 asset.")));

        AddIfPresent(messages, tracker.Observe(Update("SnipeImport", "Loading Snipe-IT model snapshot page 1.")));
        Assert.Null(tracker.Observe(Update("SnipeImport", "Planning Snipe-IT model references 1/2: Latitude 7440.")));
        AddIfPresent(messages, tracker.Observe(Update("SnipeImport", "Planning Snipe-IT category references 1/1: Computer.")));
        Assert.Null(tracker.Observe(Update("SnipeImport", "Planning Snipe-IT category references 1/1: Computer.")));
        AddIfPresent(messages, tracker.Observe(Update("SnipeImport", "Validating Snipe-IT asset 1/2: DEVICE-001.")));
        Assert.Null(tracker.Observe(Update("SnipeImport", "Validating Snipe-IT asset 2/2: DEVICE-002.")));
        messages.AddRange(tracker.Complete());

        Assert.Equal(
            [
                "Starting sync.",
                "Processing models.",
                "Processing categories.",
                "Processing assets.",
                "Completed."
            ],
            messages);
        Assert.DoesNotContain(messages, message => message.Contains("DEVICE", StringComparison.Ordinal));
        Assert.Empty(tracker.Complete());
    }

    [Fact]
    public void UiStageTracker_Complete_FillsMissingSuccessfulStagesInOrder()
    {
        var tracker = new SyncUiStageTracker();

        Assert.Equal("Starting sync.", tracker.Start());
        Assert.Equal(
            ["Processing models.", "Processing categories.", "Processing assets.", "Completed."],
            tracker.Complete());
    }

    private static SyncProgressUpdate Update(string stage, string message)
    {
        return new SyncProgressUpdate
        {
            Stage = stage,
            Message = message
        };
    }

    private static void AddIfPresent(ICollection<string> messages, string? message)
    {
        if (message is not null)
        {
            messages.Add(message);
        }
    }
}
