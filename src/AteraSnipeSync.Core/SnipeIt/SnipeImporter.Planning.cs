using System.Text.Json;
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Contains the planning responsibility segment of the SnipeImporter facade.
/// </summary>
public sealed partial class SnipeImporter
{
    /// <summary>
    /// Coordinates reference planning, one complete hardware snapshot, asset matching, and immutable asset-plan creation.
    /// It does not issue mutations, which keeps the preflight boundary explicit for dry-run and CSV workflows.
    /// </summary>
    private sealed class SnipeImportPlanner
    {
        private readonly SnipeImporter _owner;

        public SnipeImportPlanner(SnipeImporter owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Builds executable plans for all valid source records. Shared lookup failures are copied to each dependent
        /// asset, while an individual matching failure blocks only that record.
        /// </summary>
        public async Task<SnipeImportPlanningResult> PlanAssetsAsync(
            IReadOnlyList<SnipeAssetImportRecord> allRecords,
            IList<SnipeAssetImportRecord> validRecords,
            ImportRunContext context,
            IProgress<SyncProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            var plannedRecords = new List<PlannedRecord>();
            var referencePlans = await _owner.PlanReferencesAsync(
                validRecords,
                context,
                progress,
                cancellationToken).ConfigureAwait(false);
            var matchableRecords = new List<(SnipeAssetImportRecord Record, int Current)>();
            for (var index = 0; index < validRecords.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var record = validRecords[index];
                var current = index + 1;
                if (TryGetReferenceFailure(record, referencePlans, out var failureCode, out var failureMessage))
                {
                    context.AddFailure(record, failureCode, failureMessage);
                    ReportProgress(
                        progress,
                        $"Blocked Snipe-IT asset {current}/{validRecords.Count}: {failureCode}.",
                        current,
                        validRecords.Count);
                    continue;
                }

                matchableRecords.Add((record, current));
            }

            if (matchableRecords.Count == 0 && allRecords.Count > 0)
            {
                return new SnipeImportPlanningResult([], []);
            }

            SnipeHardwareLookup hardwareLookup;
            try
            {
                hardwareLookup = await _owner.LoadHardwareLookupAsync(
                    context.Options,
                    context.IgnoredMacAddresses,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SnipeApiException exception)
            {
                if (matchableRecords.Count == 0)
                {
                    context.AddHardwareSnapshotFailure(exception.Code, exception.Message);
                }
                else
                {
                    foreach (var (record, current) in matchableRecords)
                    {
                        context.AddFailure(record, exception.Code, exception.Message);
                        ReportProgress(
                            progress,
                            $"Blocked Snipe-IT asset {current}/{validRecords.Count}: {exception.Code}.",
                            current,
                            validRecords.Count);
                    }
                }

                return new SnipeImportPlanningResult([], []);
            }

            foreach (var (record, current) in matchableRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    progress,
                    $"Matching Snipe-IT asset {current}/{validRecords.Count}: {record.Name}.",
                    current - 1,
                    validRecords.Count);

                try
                {
                    var existingAsset = FindExistingAsset(record, context, hardwareLookup);
                    plannedRecords.Add(CreatePlannedRecord(record, referencePlans, existingAsset, context));
                    ReportProgress(
                        progress,
                        $"Planned Snipe-IT asset {current}/{validRecords.Count}: {record.Name}.",
                        current,
                        validRecords.Count);
                }
                catch (SnipeApiException exception)
                {
                    context.AddFailure(record, exception.Code, exception.Message);
                    ReportProgress(
                        progress,
                        $"Blocked Snipe-IT asset {current}/{validRecords.Count}: {exception.Code}.",
                        current,
                        validRecords.Count);
                }
                catch (JsonException exception)
                {
                    context.AddFailure(
                        record,
                        "SnipeImport.MalformedResponse",
                        $"Snipe-IT response could not be parsed: {exception.Message}");
                    ReportProgress(
                        progress,
                        $"Blocked Snipe-IT asset {current}/{validRecords.Count}: malformed response.",
                        current,
                        validRecords.Count);
                }
            }

            return new SnipeImportPlanningResult(
                plannedRecords,
                context.Failures.Count == 0
                    ? PlanStaleAssetDeletions(allRecords, hardwareLookup)
                    : []);
        }
    }
}
