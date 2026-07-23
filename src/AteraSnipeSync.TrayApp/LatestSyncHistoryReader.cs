using System.Text.Json;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Reads a compact sanitized summary from the newest real-sync history JSON when Worker status cannot provide one, without writing history or constructing API clients.
/// </summary>
internal static class LatestSyncHistoryReader
{
    /// <summary>
    /// Scans local history newest-first and returns the first valid non-dry-run summary, skipping Preview and malformed candidates.
    /// </summary>
    public static async Task<LatestSyncHistorySummary?> ReadSummaryAsync(
        string historyDirectory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> paths;
        try
        {
            if (!Directory.Exists(historyDirectory))
            {
                return null;
            }

            paths = Directory.EnumerateFiles(historyDirectory, "SyncResult_*.json")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ControlledPathValidator.IsUnderRoot(path, historyDirectory))
            {
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(path);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var root = document.RootElement;
                if (!root.TryGetProperty("run", out var run)
                    || !run.TryGetProperty("dryRun", out var dryRun)
                    || dryRun.ValueKind != JsonValueKind.False
                    || !root.TryGetProperty("summary", out var summary))
                {
                    continue;
                }

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
                or InvalidOperationException)
            {
                // A partial or malformed newest history file must not hide an older valid real sync.
            }
        }

        return null;
    }

    private static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var timestamp)
            ? timestamp
            : null;
}
