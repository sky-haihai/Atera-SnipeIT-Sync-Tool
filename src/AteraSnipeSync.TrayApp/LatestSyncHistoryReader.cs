using System.Text.Json;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Reads a compact sanitized summary from the newest local history JSON when Worker IPC is offline, without writing history or constructing API clients.
/// </summary>
internal static class LatestSyncHistoryReader
{
    public static async Task<LatestSyncHistorySummary?> ReadSummaryAsync(
        string historyDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(historyDirectory))
            {
                return null;
            }

            var path = Directory.EnumerateFiles(historyDirectory, "SyncResult_*.json")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (path is null || !ControlledPathValidator.IsUnderRoot(path, historyDirectory))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var run = root.GetProperty("run");
            var summary = root.GetProperty("summary");
            return new LatestSyncHistorySummary
            {
                FinishedAtUtc = ReadDateTimeOffset(run, "finishedAtUtc"),
                Created = ReadInt(summary, "assetsCreated"),
                Updated = ReadInt(summary, "assetsUpdated"),
                NoChange = ReadInt(summary, "assetsSkipped"),
                Deleted = ReadInt(summary, "assetsDeleted")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var timestamp)
            ? timestamp
            : null;
}
