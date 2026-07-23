using System.Text.Json;
using AteraSnipeSync.Core.Common;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Contains the mutation-execution responsibility segment of the SnipeImporter facade.
/// </summary>
public sealed partial class SnipeImporter
{
    /// <summary>
    /// Executes already-approved asset plans and converts per-asset API or JSON failures into structured import failures.
    /// Reference writes remain a separate pre-asset gate so a partial reference failure cannot begin asset mutation.
    /// </summary>
    private sealed class SnipeImportExecutor
    {
        private readonly SnipeImporter _owner;

        public SnipeImportExecutor(SnipeImporter owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Executes the shared reference gate before any asset mutation and reports whether asset execution may continue.
        /// </summary>
        public Task<bool> ExecuteReferencesAsync(
            IReadOnlyList<PlannedRecord> plannedRecords,
            ImportRunContext context,
            IProgress<SyncProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            return _owner.TryExecuteReferencePlanAsync(plannedRecords, context, progress, cancellationToken);
        }

        /// <summary>
        /// Executes plans sequentially to preserve deterministic audit ordering and stops only for cancellation.
        /// Individual Snipe-IT failures are recorded on their source asset and do not abort unrelated assets.
        /// </summary>
        public async Task ExecuteAssetsAsync(
            IReadOnlyList<PlannedRecord> plannedRecords,
            ImportRunContext context,
            IProgress<SyncProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            for (var index = 0; index < plannedRecords.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var plannedRecord = plannedRecords[index];
                var current = index + 1;
                ReportProgress(
                    progress,
                    $"Executing Snipe-IT asset {current}/{plannedRecords.Count}: {plannedRecord.Record.Name}.",
                    current - 1,
                    plannedRecords.Count);

                try
                {
                    await _owner.ExecutePlanAsync(plannedRecord, context, cancellationToken).ConfigureAwait(false);
                    var successMessage = plannedRecord.ExistingAsset is not null
                        && plannedRecord.RequiresAssetWrite
                            ? $"Updated Snipe-IT asset {current}/{plannedRecords.Count}: {plannedRecord.Record.Name}. Changed fields: {string.Join(", ", plannedRecord.ChangeReasons)}."
                            : $"Executed Snipe-IT asset {current}/{plannedRecords.Count}: {plannedRecord.Record.Name}.";
                    ReportProgress(
                        progress,
                        successMessage,
                        current,
                        plannedRecords.Count);
                }
                catch (SnipeApiException exception)
                {
                    context.AddFailure(plannedRecord.Record, exception.Code, exception.Message);
                    ReportProgress(
                        progress,
                        $"Failed Snipe-IT asset {current}/{plannedRecords.Count}: {exception.Code}.",
                        current,
                        plannedRecords.Count);
                }
                catch (JsonException exception)
                {
                    context.AddFailure(
                        plannedRecord.Record,
                        "SnipeImport.MalformedResponse",
                        $"Snipe-IT response could not be parsed: {exception.Message}");
                    ReportProgress(
                        progress,
                        $"Failed Snipe-IT asset {current}/{plannedRecords.Count}: malformed response.",
                        current,
                        plannedRecords.Count);
                }
            }
        }

        /// <summary>
        /// Executes deterministic stale-asset deletes after normal asset writes. Each failure is audited against the
        /// exact Snipe-IT id and does not prevent unrelated deletion candidates from being attempted.
        /// </summary>
        public async Task ExecuteDeletionsAsync(
            IReadOnlyList<PlannedAssetDeletion> plannedDeletions,
            ImportRunContext context,
            IProgress<SyncProgressUpdate>? progress,
            CancellationToken cancellationToken)
        {
            for (var index = 0; index < plannedDeletions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var deletion = plannedDeletions[index];
                var current = index + 1;
                var safeAssetTag = SanitizeProgressValue(deletion.Asset.AssetTag ?? "<missing>");
                var safeAssetName = SanitizeProgressValue(deletion.Asset.Name);
                var safeAgentId = SanitizeProgressValue(deletion.AteraAgentId);
                ReportProgress(
                    progress,
                    $"Deleting stale Snipe-IT asset {current}/{plannedDeletions.Count}: {safeAssetTag}.",
                    current - 1,
                    plannedDeletions.Count);

                try
                {
                    await _owner.DeleteAssetAsync(deletion, context, cancellationToken).ConfigureAwait(false);
                    ReportProgress(
                        progress,
                        $"Deleted stale Snipe-IT asset {current}/{plannedDeletions.Count}: {safeAssetTag}.",
                        current,
                        plannedDeletions.Count);
                }
                catch (SnipeApiException exception)
                {
                    context.AddDeletionFailure(deletion, exception.Code, exception.Message);
                    _owner._logger.LogWarning(
                        "Failed to delete stale Atera-managed Snipe-IT asset {AssetId} ({AssetTag}, {AssetName}, Atera agent {AteraAgentId}). Reason={DeleteReason}; Result={DeleteResult}.",
                        deletion.Asset.Id,
                        safeAssetTag,
                        safeAssetName,
                        safeAgentId,
                        deletion.Reason,
                        exception.Code);
                    ReportProgress(
                        progress,
                        $"Failed to delete stale Snipe-IT asset {current}/{plannedDeletions.Count}: {exception.Code}.",
                        current,
                        plannedDeletions.Count);
                }
                catch (JsonException exception)
                {
                    const string code = "SnipeImport.MalformedResponse";
                    context.AddDeletionFailure(
                        deletion,
                        code,
                        $"Snipe-IT delete response could not be parsed: {exception.Message}");
                    _owner._logger.LogWarning(
                        "Failed to delete stale Atera-managed Snipe-IT asset {AssetId} ({AssetTag}, {AssetName}, Atera agent {AteraAgentId}). Reason={DeleteReason}; Result={DeleteResult}.",
                        deletion.Asset.Id,
                        safeAssetTag,
                        safeAssetName,
                        safeAgentId,
                        deletion.Reason,
                        code);
                    ReportProgress(
                        progress,
                        $"Failed to delete stale Snipe-IT asset {current}/{plannedDeletions.Count}: {code}.",
                        current,
                        plannedDeletions.Count);
                }
            }
        }
    }
}
