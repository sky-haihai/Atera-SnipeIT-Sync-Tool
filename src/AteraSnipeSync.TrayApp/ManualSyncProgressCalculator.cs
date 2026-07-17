using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Maps detailed manual-sync callbacks into monotonic, workload-weighted progress so slow per-asset work owns most of the progress bar.
/// </summary>
internal sealed class ManualSyncProgressCalculator
{
    private readonly bool _previewOnly;
    private SnipePhase _snipePhase;
    private int _lastValue;

    public ManualSyncProgressCalculator(bool previewOnly)
    {
        _previewOnly = previewOnly;
    }

    /// <summary>
    /// Calculates the whole-run percentage for one callback without treating a completed child snapshot as a completed sync.
    /// </summary>
    public int Calculate(SyncProgressUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        var calculated = update.Stage switch
        {
            "Sync" => CalculateSync(update),
            "AteraPull" => CalculateAteraPull(update),
            "SnipeImport" => CalculateSnipeImport(update),
            _ => _lastValue
        };

        _lastValue = Math.Clamp(Math.Max(_lastValue, calculated), 0, 100);
        return _lastValue;
    }

    private static int CalculateSync(SyncProgressUpdate update)
    {
        var message = update.Message;
        if (Contains(message, "failed") || StartsWith(message, "Sync run completed"))
        {
            return 100;
        }

        if (StartsWith(message, "Snipe-IT import stage completed"))
        {
            return 99;
        }

        if (StartsWith(message, "Planning Snipe-IT import"))
        {
            return 5;
        }

        if (StartsWith(message, "Mapping completed"))
        {
            return 5;
        }

        if (StartsWith(message, "Mapping Atera agents"))
        {
            return 4;
        }

        if (StartsWith(message, "Atera pull completed"))
        {
            return 4;
        }

        return StartsWith(message, "Pulling Atera inventory") ? 1 : 0;
    }

    private static int CalculateAteraPull(SyncProgressUpdate update)
    {
        if (StartsWith(update.Message, "Completed Atera inventory pull"))
        {
            return 4;
        }

        return Scale(update.Current, update.Total, 1, 4);
    }

    private int CalculateSnipeImport(SyncProgressUpdate update)
    {
        var message = update.Message;
        if (StartsWith(message, "Completed Snipe-IT import"))
        {
            _snipePhase = SnipePhase.Completed;
            return 99;
        }

        if (StartsWith(message, "Completed Snipe-IT dry-run planning"))
        {
            _snipePhase = SnipePhase.Completed;
            return 99;
        }

        if (StartsWith(message, "Applying dry-run plan"))
        {
            _snipePhase = SnipePhase.Finalizing;
            return 98;
        }

        if (StartsWith(message, "Writing manual preflight CSV"))
        {
            _snipePhase = SnipePhase.Finalizing;
            return 96;
        }

        if (StartsWith(message, "Manual preflight CSV"))
        {
            _snipePhase = SnipePhase.Finalizing;
            return 97;
        }

        if (StartsWith(message, "Executing Snipe-IT asset"))
        {
            _snipePhase = SnipePhase.AssetExecution;
            return Scale(update.Current, update.Total, 35, 99);
        }

        if (StartsWith(message, "Executed Snipe-IT asset")
            || StartsWith(message, "Failed Snipe-IT asset"))
        {
            _snipePhase = SnipePhase.AssetExecution;
            return Scale(update.Current, update.Total, 35, 99);
        }

        if (StartsWith(message, "Reference creation failed"))
        {
            _snipePhase = SnipePhase.Completed;
            return 99;
        }

        if (StartsWith(message, "No missing Snipe-IT reference records")
            || StartsWith(message, "Prepared all Snipe-IT reference records"))
        {
            _snipePhase = SnipePhase.ReferenceExecution;
            return 35;
        }

        if (StartsWith(message, "Preparing Snipe-IT reference records"))
        {
            _snipePhase = SnipePhase.ReferenceExecution;
            return 30;
        }

        if (IsReferenceExecution(message))
        {
            _snipePhase = SnipePhase.ReferenceExecution;
            var current = StartsWith(message, "Creating Snipe-IT")
                ? Math.Max(0, (update.Current ?? 0) - 1)
                : update.Current;
            return Scale(current, update.Total, 30, 35);
        }

        if (StartsWith(message, "Matching Snipe-IT asset")
            || StartsWith(message, "Planned Snipe-IT asset"))
        {
            _snipePhase = SnipePhase.AssetPlanning;
            return ScaleAssetPlanning(update);
        }

        if (StartsWith(message, "Blocked Snipe-IT asset"))
        {
            return _snipePhase switch
            {
                SnipePhase.AssetPlanning => ScaleAssetPlanning(update),
                SnipePhase.Preparation => Scale(update.Current, update.Total, 10, 15),
                _ => Scale(update.Current, update.Total, 5, 10)
            };
        }

        if (StartsWith(message, "Validating Snipe-IT asset"))
        {
            _snipePhase = SnipePhase.Validation;
            return Scale(update.Current, update.Total, 5, 10);
        }

        if (StartsWith(message, "Loading Snipe-IT hardware snapshot"))
        {
            _snipePhase = SnipePhase.Preparation;
            return 14;
        }

        if (StartsWith(message, "Loaded Snipe-IT hardware snapshot"))
        {
            _snipePhase = SnipePhase.Preparation;
            return 15;
        }

        if (StartsWith(message, "Loading Snipe-IT model snapshot"))
        {
            _snipePhase = SnipePhase.Preparation;
            return 10;
        }

        if (StartsWith(message, "Loaded Snipe-IT model snapshot"))
        {
            _snipePhase = SnipePhase.Preparation;
            return 11;
        }

        if (Contains(message, "Fieldset") || Contains(message, "company snapshot"))
        {
            _snipePhase = SnipePhase.Preparation;
            return 12;
        }

        if (StartsWith(message, "Planning Snipe-IT company references"))
        {
            _snipePhase = SnipePhase.Preparation;
            return 12;
        }

        if (StartsWith(message, "Planning Snipe-IT category references"))
        {
            _snipePhase = SnipePhase.Preparation;
            return 13;
        }

        if (StartsWith(message, "Planning Snipe-IT manufacturer references"))
        {
            _snipePhase = SnipePhase.Preparation;
            return 14;
        }

        if (StartsWith(message, "Planning Snipe-IT model references"))
        {
            _snipePhase = SnipePhase.Preparation;
            return Scale(update.Current, update.Total, 14, 15);
        }

        return StartsWith(message, "Starting Snipe-IT planning") ? 5 : _lastValue;
    }

    private int ScaleAssetPlanning(SyncProgressUpdate update)
    {
        return Scale(update.Current, update.Total, 15, _previewOnly ? 95 : 30);
    }

    private static bool IsReferenceExecution(string message)
    {
        return (StartsWith(message, "Creating Snipe-IT")
                || StartsWith(message, "Created Snipe-IT")
                || StartsWith(message, "Failed Snipe-IT"))
            && Contains(message, " reference");
    }

    private static int Scale(int? current, int? total, int start, int end)
    {
        if (current is not { } currentValue || total is not { } totalValue || totalValue <= 0)
        {
            return start;
        }

        var ratio = Math.Clamp(currentValue / (double)totalValue, 0D, 1D);
        return start + (int)Math.Round(ratio * (end - start));
    }

    private static bool StartsWith(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string value, string fragment)
    {
        return value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
    }

    private enum SnipePhase
    {
        NotStarted,
        Validation,
        Preparation,
        AssetPlanning,
        Finalizing,
        ReferenceExecution,
        AssetExecution,
        Completed
    }
}
