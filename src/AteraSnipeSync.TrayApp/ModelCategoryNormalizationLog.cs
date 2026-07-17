namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Creates and appends the dedicated per-run audit log for operator-triggered model category normalization.
/// </summary>
internal sealed class ModelCategoryNormalizationLog
{
    private ModelCategoryNormalizationLog()
    {
    }

    /// <summary>
    /// Returns a collision-resistant log path under the supplied directory without touching the filesystem.
    /// </summary>
    public static string CreatePath(string directoryPath, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Log directory is required.", nameof(directoryPath));
        }

        return Path.Combine(
            Path.GetFullPath(directoryPath),
            $"ModelCategoryNormalization_{timestamp:yyyyMMdd_HHmmss_fffffff}.log");
    }

    /// <summary>
    /// Appends one sanitized timestamped line, creating the parent directory and file when needed.
    /// </summary>
    public static async Task AppendAsync(
        string path,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Log path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Log path must include a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var safeMessage = (message ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {safeMessage}{Environment.NewLine}";
        await File.AppendAllTextAsync(fullPath, line, cancellationToken).ConfigureAwait(false);
    }
}
