using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Converts detailed manual-sync progress into the five stable UI log milestones while leaving per-record details for file logging.
/// </summary>
internal sealed class ManualSyncUiStageTracker
{
    private Stage _stage;

    /// <summary>
    /// Starts a new manual run and returns its single UI start message.
    /// </summary>
    public string Start()
    {
        _stage = Stage.Started;
        return "Starting sync.";
    }

    /// <summary>
    /// Observes one detailed progress callback and returns only a newly reached ordered UI milestone.
    /// </summary>
    public string? Observe(SyncProgressUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (_stage == Stage.Started && Contains(update.Message, "model"))
        {
            _stage = Stage.Models;
            return "Processing models.";
        }

        if (_stage == Stage.Models && Contains(update.Message, "categor"))
        {
            _stage = Stage.Categories;
            return "Processing categories.";
        }

        if (_stage == Stage.Categories && Contains(update.Message, "asset"))
        {
            _stage = Stage.Assets;
            return "Processing assets.";
        }

        return null;
    }

    /// <summary>
    /// Completes a successful run, filling any skipped empty-batch stages in their stable display order.
    /// </summary>
    public IReadOnlyList<string> Complete()
    {
        var messages = new List<string>();
        if (_stage < Stage.Models)
        {
            messages.Add("Processing models.");
        }

        if (_stage < Stage.Categories)
        {
            messages.Add("Processing categories.");
        }

        if (_stage < Stage.Assets)
        {
            messages.Add("Processing assets.");
        }

        if (_stage < Stage.Completed)
        {
            messages.Add("Completed.");
            _stage = Stage.Completed;
        }

        return messages;
    }

    private static bool Contains(string value, string fragment)
    {
        return value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
    }

    private enum Stage
    {
        NotStarted,
        Started,
        Models,
        Categories,
        Assets,
        Completed
    }
}
