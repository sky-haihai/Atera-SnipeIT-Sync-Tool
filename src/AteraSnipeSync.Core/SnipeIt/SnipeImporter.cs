using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Mapping;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Imports mapped assets into Snipe-IT by resolving dependencies, planning writes, and issuing mutations only after preflight gates pass.
/// </summary>
public sealed class SnipeImporter : ISnipeImporter
{
    private const int HardwareSnapshotPageSize = 500;
    private const int ModelSnapshotPageSize = 500;
    private const int MaximumErrorDetailLength = 2000;
    private const string AssetCategoryType = "asset";
    private const string FirstAddedNotePrefix = "First added by Atera-SnipeIT Sync Tool at ";
    private const int MaximumSnapshotPages = 10000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex CustomFieldDbColumnPattern = new(
        "^_snipeit_[a-z0-9_]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly ILogger<SnipeImporter> _logger;
    private readonly TimeProvider _timeProvider;

    public SnipeImporter(HttpClient httpClient, ILogger<SnipeImporter> logger)
        : this(httpClient, logger, TimeProvider.System)
    {
    }

    public SnipeImporter(HttpClient httpClient, ILogger<SnipeImporter> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Processes a batch and returns structured counts, actions, failures, and warnings without exposing secrets.
    /// </summary>
    public async Task<SnipeImportResult> ImportAsync(
        SnipeImportBatch batch,
        SnipeImportOptions options,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var context = new ImportRunContext(options, []);
        var plannedRecords = new List<PlannedRecord>();
        var validRecords = new List<SnipeAssetImportRecord>();
        _logger.LogInformation("Starting Snipe-IT import for {AssetCount} asset(s).", batch.Assets.Count);
        ReportProgress(progress, $"Starting Snipe-IT planning for {batch.Assets.Count} asset(s).", current: 0, total: batch.Assets.Count);

        try
        {
            var identityFailures = FindBatchIdentityFailures(batch.Assets, context.IgnoredMacAddresses);
            for (var index = 0; index < batch.Assets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var record = batch.Assets[index];
                var current = index + 1;
                ReportProgress(progress, $"Validating Snipe-IT asset {current}/{batch.Assets.Count}: {record.Name}.", current - 1, batch.Assets.Count);

                try
                {
                    ValidateRecord(record);
                    if (identityFailures.TryGetValue(record, out var identityFailure))
                    {
                        context.AddFailure(
                            record,
                            "SnipeImport.DuplicateBatchIdentity",
                            identityFailure.Message,
                            identityFailure.ConflictingFields,
                            identityFailure.ConflictingValue,
                            identityFailure.ConflictingAssets);
                        ReportProgress(progress, $"Blocked Snipe-IT asset {current}/{batch.Assets.Count}: SnipeImport.DuplicateBatchIdentity.", current, batch.Assets.Count);
                        continue;
                    }

                    validRecords.Add(record);
                }
                catch (SnipeApiException exception)
                {
                    context.AddFailure(record, exception.Code, exception.Message);
                    ReportProgress(progress, $"Blocked Snipe-IT asset {current}/{batch.Assets.Count}: {exception.Code}.", current, batch.Assets.Count);
                }
                catch (JsonException exception)
                {
                    context.AddFailure(record, "SnipeImport.MalformedResponse", $"Snipe-IT response could not be parsed: {exception.Message}");
                    ReportProgress(progress, $"Blocked Snipe-IT asset {current}/{batch.Assets.Count}: malformed response.", current, batch.Assets.Count);
                }
            }

            var referencePlans = await PlanReferencesAsync(validRecords, context, progress, cancellationToken).ConfigureAwait(false);
        var matchableRecords = new List<(SnipeAssetImportRecord Record, int Current)>();
        for (var index = 0; index < validRecords.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = validRecords[index];
            var current = index + 1;
            if (TryGetReferenceFailure(record, referencePlans, out var failureCode, out var failureMessage))
            {
                context.AddFailure(record, failureCode, failureMessage);
                ReportProgress(progress, $"Blocked Snipe-IT asset {current}/{validRecords.Count}: {failureCode}.", current, validRecords.Count);
                continue;
            }

            matchableRecords.Add((record, current));
        }

        var hardwareLookup = SnipeHardwareLookup.Empty;
        if (matchableRecords.Count > 0)
        {
            try
            {
                hardwareLookup = await LoadHardwareLookupAsync(
                    context.Options,
                    context.IgnoredMacAddresses,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SnipeApiException exception)
            {
                foreach (var (record, current) in matchableRecords)
                {
                    context.AddFailure(record, exception.Code, exception.Message);
                    ReportProgress(progress, $"Blocked Snipe-IT asset {current}/{validRecords.Count}: {exception.Code}.", current, validRecords.Count);
                }

                matchableRecords.Clear();
            }
        }

        foreach (var (record, current) in matchableRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReportProgress(progress, $"Matching Snipe-IT asset {current}/{validRecords.Count}: {record.Name}.", current - 1, validRecords.Count);

            try
            {
                var existingAsset = FindExistingAsset(record, context, hardwareLookup);
                plannedRecords.Add(CreatePlannedRecord(record, referencePlans, existingAsset));
                ReportProgress(progress, $"Planned Snipe-IT asset {current}/{validRecords.Count}: {record.Name}.", current, validRecords.Count);
            }
            catch (SnipeApiException exception)
            {
                context.AddFailure(record, exception.Code, exception.Message);
                ReportProgress(progress, $"Blocked Snipe-IT asset {current}/{validRecords.Count}: {exception.Code}.", current, validRecords.Count);
            }
            catch (JsonException exception)
            {
                context.AddFailure(record, "SnipeImport.MalformedResponse", $"Snipe-IT response could not be parsed: {exception.Message}");
                ReportProgress(progress, $"Blocked Snipe-IT asset {current}/{validRecords.Count}: malformed response.", current, validRecords.Count);
            }
        }

            BlockDuplicateTargetReservations(plannedRecords, context);
            context.RetainReferencesFor(plannedRecords);

        if (options.ManualPreflightCsvEnabled)
        {
            ReportProgress(progress, "Writing manual preflight CSV files.", batch.Assets.Count, batch.Assets.Count);
            if (!await TryWritePreflightCsvAsync(plannedRecords, context, cancellationToken).ConfigureAwait(false))
            {
                ReportProgress(progress, "Manual preflight CSV write failed; import stopped before writes.", batch.Assets.Count, batch.Assets.Count);
                return context.ToResult();
            }

            ReportProgress(progress, "Manual preflight CSV files were written.", batch.Assets.Count, batch.Assets.Count);
        }

        if (options.DryRun)
        {
            ReportProgress(progress, "Applying dry-run plan without Snipe-IT writes.", batch.Assets.Count, batch.Assets.Count);
            ApplyDryRunPlan(plannedRecords, context);
            ReportProgress(progress, "Completed Snipe-IT dry-run planning.", batch.Assets.Count, batch.Assets.Count);
            return context.ToResult();
        }

        if (!await TryExecuteReferencePlanAsync(plannedRecords, context, progress, cancellationToken).ConfigureAwait(false))
        {
            ReportProgress(progress, "Reference creation failed; import stopped before asset writes.", batch.Assets.Count, batch.Assets.Count);
            return context.ToResult();
        }

        for (var index = 0; index < plannedRecords.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plannedRecord = plannedRecords[index];
            var current = index + 1;
            ReportProgress(progress, $"Executing Snipe-IT asset {current}/{plannedRecords.Count}: {plannedRecord.Record.Name}.", current - 1, plannedRecords.Count);

            try
            {
                await ExecutePlanAsync(plannedRecord, context, cancellationToken).ConfigureAwait(false);
                ReportProgress(progress, $"Executed Snipe-IT asset {current}/{plannedRecords.Count}: {plannedRecord.Record.Name}.", current, plannedRecords.Count);
            }
            catch (SnipeApiException exception)
            {
                context.AddFailure(plannedRecord.Record, exception.Code, exception.Message);
                ReportProgress(progress, $"Failed Snipe-IT asset {current}/{plannedRecords.Count}: {exception.Code}.", current, plannedRecords.Count);
            }
            catch (JsonException exception)
            {
                context.AddFailure(plannedRecord.Record, "SnipeImport.MalformedResponse", $"Snipe-IT response could not be parsed: {exception.Message}");
                ReportProgress(progress, $"Failed Snipe-IT asset {current}/{plannedRecords.Count}: malformed response.", current, plannedRecords.Count);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "Completed Snipe-IT import. Created={CreatedAssets}, Updated={UpdatedAssets}, Failed={FailedAssets}.",
            context.CreatedAssets,
            context.UpdatedAssets,
            context.FailedAssets);

        ReportProgress(progress, "Completed Snipe-IT import.", plannedRecords.Count, plannedRecords.Count);
            return context.ToResult();
        }
        catch (OperationCanceledException) when (context.HasExecutedWrites)
        {
            context.AddCancellationFailure();
            _logger.LogWarning(
                "Snipe-IT import was cancelled after {ExecutedWriteCount} successful write(s); returning a partial audit result.",
                context.ExecutedWriteCount);
            return context.ToResult(cancelled: true);
        }
    }

    /// <summary>
    /// Reads the current Snipe-IT hardware list once per run so asset matching can use local indexes instead of per-asset API lookups.
    /// </summary>
    private async Task<SnipeHardwareLookup> LoadHardwareLookupAsync(
        SnipeImportOptions options,
        IReadOnlySet<string> ignoredMacAddresses,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var assets = new List<SnipeAssetMatch>();
        int? total = null;
        var offset = 0;
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, $"Loading Snipe-IT hardware snapshot page {page}.", assets.Count, total);

            var relativePath = BuildPath(
                "hardware",
                new Dictionary<string, string>
                {
                    ["limit"] = HardwareSnapshotPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            var operation = $"Load hardware snapshot page {page}";

            using var document = await SendJsonAsync(
                HttpMethod.Get,
                relativePath,
                payload: null,
                operation,
                options,
                cancellationToken).ConfigureAwait(false);

            EnsureBusinessSuccess(document.RootElement, HttpMethod.Get, relativePath, operation);

            total ??= ReadTotal(document.RootElement);
            var pageRows = ParseRows(document.RootElement, operation);
            var pageAssets = pageRows.Select(row => ParseAsset(row)
                    ?? throw new SnipeApiException(
                        "SnipeImport.MalformedResponse",
                        $"{operation} returned a hardware row without a valid id."))
                .ToList();

            assets.AddRange(pageAssets);
            if (pageAssets.Count == 0)
            {
                break;
            }

            offset += pageRows.Count;
            if (total is { } expectedTotal && offset >= expectedTotal)
            {
                break;
            }

            page++;
            if (page > MaximumSnapshotPages)
            {
                throw new SnipeApiException("SnipeImport.MalformedResponse", "Hardware snapshot exceeded the maximum safe page count.");
            }
        }

        ReportProgress(progress, $"Loaded Snipe-IT hardware snapshot with {assets.Count} asset(s).", assets.Count, total ?? assets.Count);
        return SnipeHardwareLookup.Create(
            assets,
            options.MacAddressCustomFieldDbColumnName,
            ignoredMacAddresses);
    }

    /// <summary>
    /// Reads the current Snipe-IT model list once per run so model planning can match locally by name and category id.
    /// </summary>
    private async Task<SnipeModelLookup> LoadModelLookupAsync(
        SnipeImportOptions options,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var models = new List<SnipeModel>();
        int? total = null;
        var offset = 0;
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, $"Loading Snipe-IT model snapshot page {page}.", models.Count, total);

            var relativePath = BuildPath(
                "models",
                new Dictionary<string, string>
                {
                    ["limit"] = ModelSnapshotPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            var operation = $"Load model snapshot page {page}";

            using var document = await SendJsonAsync(
                HttpMethod.Get,
                relativePath,
                payload: null,
                operation,
                options,
                cancellationToken).ConfigureAwait(false);

            EnsureBusinessSuccess(document.RootElement, HttpMethod.Get, relativePath, operation);

            total ??= ReadTotal(document.RootElement);
            var pageRows = ParseRows(document.RootElement, operation);
            models.AddRange(pageRows.Select(row => ParseModel(row)
                ?? throw new SnipeApiException(
                    "SnipeImport.MalformedResponse",
                    $"{operation} returned a model row without required id, name, or category id.")));

            if (pageRows.Count == 0)
            {
                break;
            }

            offset += pageRows.Count;
            if (total is { } expectedTotal && offset >= expectedTotal)
            {
                break;
            }

            page++;
            if (page > MaximumSnapshotPages)
            {
                throw new SnipeApiException("SnipeImport.MalformedResponse", "Model snapshot exceeded the maximum safe page count.");
            }
        }

        ReportProgress(progress, $"Loaded Snipe-IT model snapshot with {models.Count} model(s).", models.Count, total ?? models.Count);
        return SnipeModelLookup.Create(models);
    }

    /// <summary>
    /// Resolves the operator-configured Fieldset once and verifies that it exposes the configured MAC DB column.
    /// </summary>
    private async Task<SnipeFieldset?> LoadMacFieldsetAsync(
        SnipeImportOptions options,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var fieldsetName = Normalize(options.MacAddressFieldsetName);
        var macFieldName = Normalize(options.MacAddressCustomFieldDbColumnName);
        if (fieldsetName is null || macFieldName is null)
        {
            return null;
        }

        const string relativePath = "fieldsets";
        const string operation = "Load custom Fieldset snapshot";
        ReportProgress(progress, $"Resolving Snipe-IT MAC Fieldset '{fieldsetName}'.", 0, null);

        using var document = await SendJsonAsync(
            HttpMethod.Get,
            relativePath,
            payload: null,
            operation,
            options,
            cancellationToken).ConfigureAwait(false);
        EnsureBusinessSuccess(document.RootElement, HttpMethod.Get, relativePath, operation);

        var rows = ParseRows(document.RootElement, operation);
        var total = ReadTotal(document.RootElement);
        if (total is not null && total.Value != rows.Count)
        {
            throw new SnipeApiException(
                "SnipeImport.IncompleteFieldsetSnapshot",
                $"{operation} returned {rows.Count} row(s) but reported total {total.Value}.");
        }

        var fieldsets = rows.Select(ParseFieldset).ToList();
        var matches = fieldsets
            .Where(fieldset => NamesEqual(fieldsetName, fieldset.Name))
            .GroupBy(fieldset => fieldset.Id)
            .Select(group => group.First())
            .ToList();
        if (matches.Count == 0)
        {
            throw new SnipeApiException(
                "SnipeImport.MacFieldsetMissing",
                $"MAC Fieldset '{fieldsetName}' does not exist in Snipe-IT.");
        }

        if (matches.Count > 1)
        {
            throw new SnipeApiException(
                "SnipeImport.AmbiguousMacFieldset",
                $"MAC Fieldset name '{fieldsetName}' matched multiple Snipe-IT Fieldset ids.");
        }

        var match = matches[0];
        if (!match.FieldDbColumns.Contains(macFieldName))
        {
            throw new SnipeApiException(
                "SnipeImport.MacFieldsetDoesNotContainField",
                $"MAC Fieldset '{match.Name}' (id {match.Id}) does not contain custom field DB column '{macFieldName}'.");
        }

        ReportProgress(progress, $"Resolved Snipe-IT MAC Fieldset '{match.Name}' (id {match.Id}).", 1, 1);
        return match;
    }

    /// <summary>
    /// Loads the complete Snipe-IT company list once for the current run and builds an in-memory name index.
    /// </summary>
    private async Task<SnipeCompanyLookup> LoadCompanyLookupAsync(
        SnipeImportOptions options,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        const string relativePath = "companies";
        const string operation = "Load company snapshot";
        ReportProgress(progress, "Loading Snipe-IT company snapshot.", current: 0, total: null);

        using var document = await SendJsonAsync(
            HttpMethod.Get,
            relativePath,
            payload: null,
            operation,
            options,
            cancellationToken).ConfigureAwait(false);

        EnsureBusinessSuccess(document.RootElement, HttpMethod.Get, relativePath, operation);
        var rows = ParseRows(document.RootElement, operation);
        var total = ReadTotal(document.RootElement)
            ?? throw new SnipeApiException(
                "SnipeImport.MalformedResponse",
                $"{operation} returned JSON without the required total count.");
        if (total != rows.Count)
        {
            throw new SnipeApiException(
                "SnipeImport.IncompleteCompanySnapshot",
                $"{operation} returned {rows.Count} company row(s) but reported total {total}; the complete company list was not loaded.");
        }

        var companies = rows
            .Select(row => ParseEntity(row)
                ?? throw new SnipeApiException(
                    "SnipeImport.MalformedResponse",
                    $"{operation} returned a company row without a valid id or name."))
            .ToList();

        ReportProgress(progress, $"Loaded Snipe-IT company snapshot with {companies.Count} company(s).", companies.Count, total);
        return SnipeCompanyLookup.Create(companies);
    }

    /// <summary>
    /// Plans shared reference dependencies before asset matching so missing references are discovered once per unique name.
    /// </summary>
    private async Task<ReferencePlans> PlanReferencesAsync(
        IList<SnipeAssetImportRecord> records,
        ImportRunContext context,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var referencePlans = new ReferencePlans();
        if (records.Count == 0)
        {
            return referencePlans;
        }

        var companyLookupResult = await PlanReferenceAsync(() =>
            LoadCompanyLookupAsync(context.Options, progress, cancellationToken)).ConfigureAwait(false);
        if (companyLookupResult.Success)
        {
            ApplyCompanyDirectMatchPrecedence(records, companyLookupResult.Value!);
        }

        var companyGroups = GroupRecordsByReference(records, record => record.CompanyName);
        var plannedCompanyCount = 0;
        foreach (var group in companyGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            plannedCompanyCount++;
            var companyName = group.Value[0].CompanyName;
            ReportProgress(progress, $"Planning Snipe-IT company references {plannedCompanyCount}/{companyGroups.Count}: {companyName}.", plannedCompanyCount - 1, companyGroups.Count);
            referencePlans.Companies[group.Key] = companyLookupResult.Success
                ? await PlanReferenceAsync(() => Task.FromResult(PlanCompany(companyName, companyLookupResult.Value!, context))).ConfigureAwait(false)
                : ReferencePlanResult<CompanyPlan>.FromFailure(
                    companyLookupResult.FailureCode!,
                    companyLookupResult.FailureMessage!);
        }

        var companyReadyRecords = records
            .Where(record => referencePlans.Companies[BuildReferenceKey(record.CompanyName)].Success)
            .ToList();
        var categoryGroups = GroupRecordsByReference(companyReadyRecords, record => record.CategoryName);
        var plannedCategoryCount = 0;
        foreach (var group in categoryGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            plannedCategoryCount++;
            var categoryName = group.Value[0].CategoryName;
            ReportProgress(progress, $"Planning Snipe-IT category references {plannedCategoryCount}/{categoryGroups.Count}: {categoryName}.", plannedCategoryCount - 1, categoryGroups.Count);
            referencePlans.Categories[group.Key] = await PlanReferenceAsync(() =>
                PlanCategoryAsync(categoryName, context, cancellationToken)).ConfigureAwait(false);
        }

        ReferencePlanResult<CategoryPlan>? normalizationTargetCategoryResult = null;
        var categoryReadyRecords = companyReadyRecords
            .Where(record => referencePlans.Categories[BuildReferenceKey(record.CategoryName)].Success)
            .ToList();
        var manufacturerGroups = GroupRecordsByReference(categoryReadyRecords, record => record.ManufacturerName);
        var manufacturerPlansBySource = new Dictionary<string, ReferencePlanResult<ManufacturerPlan>>();
        var plannedManufacturerCount = 0;
        foreach (var group in manufacturerGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            plannedManufacturerCount++;
            var manufacturerName = group.Value[0].ManufacturerName;
            ReportProgress(progress, $"Planning Snipe-IT manufacturer references {plannedManufacturerCount}/{manufacturerGroups.Count}: {manufacturerName}.", plannedManufacturerCount - 1, manufacturerGroups.Count);
            manufacturerPlansBySource[group.Key] = await PlanReferenceAsync(() =>
                PlanManufacturerAsync(group.Value[0], context, cancellationToken)).ConfigureAwait(false);
        }

        ApplyManufacturerDirectMatchPrecedence(records, manufacturerPlansBySource, referencePlans.Manufacturers);
        categoryReadyRecords = records
            .Where(record => referencePlans.Companies[BuildReferenceKey(record.CompanyName)].Success)
            .Where(record => referencePlans.Categories[BuildReferenceKey(record.CategoryName)].Success)
            .ToList();
        var manufacturerReadyRecords = categoryReadyRecords
            .Where(record => referencePlans.Manufacturers.TryGetValue(BuildReferenceKey(record.ManufacturerName), out var plan)
                && plan.Success)
            .ToList();
        var modelRecords = new Dictionary<string, SnipeAssetImportRecord>();
        foreach (var record in manufacturerReadyRecords)
        {
            var categoryPlan = referencePlans.Categories[BuildReferenceKey(record.CategoryName)].Value!;
            var manufacturerPlan = referencePlans.Manufacturers[BuildReferenceKey(record.ManufacturerName)].Value!;
            var modelKey = BuildModelReferenceKey(record.ModelName, categoryPlan, manufacturerPlan.ManufacturerId);
            modelRecords.TryAdd(modelKey, record);
        }

        var modelLookupResult = ReferencePlanResult<SnipeModelLookup>.FromValue(SnipeModelLookup.Empty);
        if (modelRecords.Count > 0)
        {
            modelLookupResult = await PlanReferenceAsync(() =>
                LoadModelLookupAsync(context.Options, progress, cancellationToken)).ConfigureAwait(false);
        }

        var normalizationTargetCategoryName = Normalize(context.Options.ModelCategoryNormalizationTargetName);
        if (modelLookupResult.Success
            && normalizationTargetCategoryName is not null
            && RequiresNormalizationTargetCategoryResolution(
                modelLookupResult.Value ?? SnipeModelLookup.Empty,
                context.Options))
        {
            var targetCategoryKey = BuildReferenceKey(normalizationTargetCategoryName);
            if (!referencePlans.Categories.TryGetValue(targetCategoryKey, out normalizationTargetCategoryResult))
            {
                normalizationTargetCategoryResult = await PlanReferenceAsync(() =>
                    PlanCategoryAsync(normalizationTargetCategoryName, context, cancellationToken)).ConfigureAwait(false);
                referencePlans.Categories[targetCategoryKey] = normalizationTargetCategoryResult;
            }
        }

        var targetFieldsetResult = ReferencePlanResult<SnipeFieldset?>.FromValue(null);
        if (modelLookupResult.Success
            && RequiresMacFieldsetResolution(
                modelLookupResult.Value ?? SnipeModelLookup.Empty,
                modelRecords.Values,
                context.Options))
        {
            targetFieldsetResult = await PlanReferenceAsync(() =>
                LoadMacFieldsetAsync(context.Options, progress, cancellationToken)).ConfigureAwait(false);
        }

        if (modelLookupResult.Success
            && targetFieldsetResult.Success
            && (normalizationTargetCategoryResult is null || normalizationTargetCategoryResult.Success))
        {
            PlanModelMaintenance(
                modelLookupResult.Value ?? SnipeModelLookup.Empty,
                normalizationTargetCategoryResult?.Value,
                targetFieldsetResult.Value,
                context);
        }

        var plannedModelCount = 0;
        foreach (var modelRecord in modelRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();

            plannedModelCount++;
            var record = modelRecord.Value;
            var categoryPlan = referencePlans.Categories[BuildReferenceKey(record.CategoryName)].Value!;
            var manufacturerPlan = referencePlans.Manufacturers[BuildReferenceKey(record.ManufacturerName)].Value!;
            ReportProgress(progress, $"Planning Snipe-IT model references {plannedModelCount}/{modelRecords.Count}: {record.ModelName}.", plannedModelCount - 1, modelRecords.Count);

            if (!modelLookupResult.Success)
            {
                referencePlans.Models[modelRecord.Key] = ReferencePlanResult<ModelPlan>.FromFailure(
                    modelLookupResult.FailureCode!,
                    modelLookupResult.FailureMessage!);
                continue;
            }

            if (!targetFieldsetResult.Success)
            {
                referencePlans.Models[modelRecord.Key] = ReferencePlanResult<ModelPlan>.FromFailure(
                    targetFieldsetResult.FailureCode!,
                    targetFieldsetResult.FailureMessage!);
                continue;
            }

            if (normalizationTargetCategoryResult is { Success: false })
            {
                referencePlans.Models[modelRecord.Key] = ReferencePlanResult<ModelPlan>.FromFailure(
                    normalizationTargetCategoryResult.FailureCode!,
                    normalizationTargetCategoryResult.FailureMessage!);
                continue;
            }

            referencePlans.Models[modelRecord.Key] = await PlanReferenceAsync(() =>
                Task.FromResult(PlanModel(
                    record,
                    categoryPlan,
                    manufacturerPlan.ManufacturerId,
                    modelLookupResult.Value ?? SnipeModelLookup.Empty,
                    targetFieldsetResult.Value,
                    context))).ConfigureAwait(false);
        }

        BlockConflictingPlannedModelCreates(modelRecords, referencePlans, context);

        return referencePlans;
    }

    /// <summary>
    /// Keeps an existing direct source-company match and applies the configured alias candidate only when the source name is absent.
    /// </summary>
    private static void ApplyCompanyDirectMatchPrecedence(
        IList<SnipeAssetImportRecord> records,
        SnipeCompanyLookup companyLookup)
    {
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (companyLookup.Find(record.CompanyName) is not null
                || Normalize(record.CompanyAliasName) is not { } aliasName)
            {
                continue;
            }

            records[index] = CopyWithCompanyName(record, aliasName);
        }
    }

    /// <summary>
    /// Creates the effective-company record used by all downstream reference, asset, CSV, and result planning.
    /// </summary>
    private static SnipeAssetImportRecord CopyWithCompanyName(
        SnipeAssetImportRecord record,
        string companyName)
    {
        return new SnipeAssetImportRecord
        {
            AssetTag = record.AssetTag,
            Name = record.Name,
            Serial = record.Serial,
            SerialIsReliableIdentity = record.SerialIsReliableIdentity,
            MacAddresses = record.MacAddresses,
            CompanyName = companyName,
            CompanyAliasName = record.CompanyAliasName,
            ManufacturerName = record.ManufacturerName,
            ManufacturerAliasName = record.ManufacturerAliasName,
            ModelName = record.ModelName,
            CategoryName = record.CategoryName,
            DeviceType = record.DeviceType,
            StatusId = record.StatusId,
            Notes = record.Notes,
            SourceSystem = record.SourceSystem,
            SourceId = record.SourceId
        };
    }

    /// <summary>
    /// Applies each source manufacturer's resolved direct-or-alias name once, then indexes its plan by the effective downstream name.
    /// </summary>
    private static void ApplyManufacturerDirectMatchPrecedence(
        IList<SnipeAssetImportRecord> records,
        IReadOnlyDictionary<string, ReferencePlanResult<ManufacturerPlan>> plansBySource,
        IDictionary<string, ReferencePlanResult<ManufacturerPlan>> plansByEffectiveName)
    {
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var sourceKey = BuildReferenceKey(record.ManufacturerName);
            if (!plansBySource.TryGetValue(sourceKey, out var result))
            {
                continue;
            }

            if (!result.Success)
            {
                plansByEffectiveName[sourceKey] = result;
                continue;
            }

            var effectiveName = result.Value!.ManufacturerName;
            records[index] = CopyWithManufacturerName(record, effectiveName);
            plansByEffectiveName[BuildReferenceKey(effectiveName)] = result;
        }
    }

    /// <summary>
    /// Creates the effective-manufacturer record used by downstream model, asset, CSV, and result planning.
    /// </summary>
    private static SnipeAssetImportRecord CopyWithManufacturerName(
        SnipeAssetImportRecord record,
        string manufacturerName)
    {
        return new SnipeAssetImportRecord
        {
            AssetTag = record.AssetTag,
            Name = record.Name,
            Serial = record.Serial,
            SerialIsReliableIdentity = record.SerialIsReliableIdentity,
            MacAddresses = record.MacAddresses,
            CompanyName = record.CompanyName,
            CompanyAliasName = record.CompanyAliasName,
            ManufacturerName = manufacturerName,
            ManufacturerAliasName = record.ManufacturerAliasName,
            ModelName = record.ModelName,
            CategoryName = record.CategoryName,
            DeviceType = record.DeviceType,
            StatusId = record.StatusId,
            Notes = record.Notes,
            SourceSystem = record.SourceSystem,
            SourceId = record.SourceId
        };
    }

    /// <summary>
    /// Resolves the normalization target category only when the complete Model snapshot contains an authorized source-category Model.
    /// </summary>
    private static bool RequiresNormalizationTargetCategoryResolution(
        SnipeModelLookup modelLookup,
        SnipeImportOptions options)
    {
        if (NormalizeApiText(options.ModelCategoryNormalizationTargetName) is not { } targetCategoryName)
        {
            return false;
        }

        var sourceCategories = options.ModelCategoriesToNormalize
            .Select(NormalizeApiText)
            .Where(name => name is not null)
            .Select(name => NormalizeKey(name!))
            .ToHashSet(StringComparer.Ordinal);
        return sourceCategories.Count > 0
            && modelLookup.Models.Any(model =>
                model.CategoryName is { } categoryName
                && !NamesEqual(targetCategoryName, categoryName)
                && sourceCategories.Contains(NormalizeKey(categoryName)));
    }

    /// <summary>
    /// Limits MAC Fieldset resolution to Models already in, moving into, or being created in the configured default category.
    /// </summary>
    private static bool RequiresMacFieldsetResolution(
        SnipeModelLookup modelLookup,
        IEnumerable<SnipeAssetImportRecord> modelRecords,
        SnipeImportOptions options)
    {
        if (Normalize(options.MacAddressCustomFieldDbColumnName) is null
            || Normalize(options.MacAddressFieldsetName) is null
            || NormalizeApiText(options.ModelCategoryNormalizationTargetName) is not { } targetCategoryName)
        {
            return false;
        }

        if (modelRecords.Any(record => NamesEqual(targetCategoryName, record.CategoryName)))
        {
            return true;
        }

        var sourceCategories = options.ModelCategoriesToNormalize
            .Select(NormalizeApiText)
            .Where(name => name is not null)
            .Select(name => NormalizeKey(name!))
            .ToHashSet(StringComparer.Ordinal);
        return modelLookup.Models.Any(model =>
            model.CategoryName is { } categoryName
            && (NamesEqual(targetCategoryName, categoryName)
                || sourceCategories.Contains(NormalizeKey(categoryName))));
    }

    /// <summary>
    /// Blocks same-run model creates that would violate Snipe-IT's global model-name uniqueness.
    /// </summary>
    private static void BlockConflictingPlannedModelCreates(
        IReadOnlyDictionary<string, SnipeAssetImportRecord> modelRecords,
        ReferencePlans referencePlans,
        ImportRunContext context)
    {
        var conflictingNames = context.PlannedModels
            .GroupBy(model => NormalizeKey(model.Name), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var conflictingName in conflictingNames)
        {
            var requests = modelRecords
                .Where(item => NormalizeKey(item.Value.ModelName) == conflictingName)
                .ToList();
            var categories = string.Join(", ", requests
                .Select(item => item.Value.CategoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase));
            var manufacturers = string.Join(", ", requests
                .Select(item => item.Value.ManufacturerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase));
            var modelName = requests[0].Value.ModelName;
            var message = $"Model '{modelName}' was planned more than once in the same import batch with conflicting category/manufacturer references (categories: {categories}; manufacturers: {manufacturers}). Snipe-IT permits only one model with this name.";
            foreach (var request in requests)
            {
                referencePlans.Models[request.Key] = ReferencePlanResult<ModelPlan>.FromFailure(
                    "SnipeImport.ModelBatchConflict",
                    message);
            }
        }
    }

    /// <summary>
    /// Converts a reference planning exception into a stored result so every affected asset can report the same block reason.
    /// </summary>
    private static async Task<ReferencePlanResult<T>> PlanReferenceAsync<T>(Func<Task<T>> planFactory)
    {
        try
        {
            return ReferencePlanResult<T>.FromValue(await planFactory().ConfigureAwait(false));
        }
        catch (SnipeApiException exception)
        {
            return ReferencePlanResult<T>.FromFailure(exception.Code, exception.Message);
        }
        catch (JsonException exception)
        {
            return ReferencePlanResult<T>.FromFailure("SnipeImport.MalformedResponse", $"Snipe-IT response could not be parsed: {exception.Message}");
        }
    }

    /// <summary>
    /// Checks whether a record is blocked by reference planning before asset lookup begins.
    /// </summary>
    private static bool TryGetReferenceFailure(
        SnipeAssetImportRecord record,
        ReferencePlans referencePlans,
        out string code,
        out string message)
    {
        var companyKey = BuildReferenceKey(record.CompanyName);
        if (!TryGetSuccessfulReference(referencePlans.Companies, companyKey, "Company", record.CompanyName, out var companyPlan, out code, out message))
        {
            return true;
        }

        var categoryKey = BuildReferenceKey(record.CategoryName);
        if (!TryGetSuccessfulReference(referencePlans.Categories, categoryKey, "Category", record.CategoryName, out var categoryPlan, out code, out message))
        {
            return true;
        }

        var manufacturerKey = BuildReferenceKey(record.ManufacturerName);
        if (!TryGetSuccessfulReference(referencePlans.Manufacturers, manufacturerKey, "Manufacturer", record.ManufacturerName, out var manufacturerPlan, out code, out message))
        {
            return true;
        }

        var modelKey = BuildModelReferenceKey(record.ModelName, categoryPlan, manufacturerPlan.ManufacturerId);
        if (!TryGetSuccessfulReference(referencePlans.Models, modelKey, "Model", record.ModelName, out _, out code, out message))
        {
            return true;
        }

        code = string.Empty;
        message = string.Empty;
        return false;
    }

    private static bool TryGetSuccessfulReference<T>(
        IReadOnlyDictionary<string, ReferencePlanResult<T>> references,
        string key,
        string referenceType,
        string referenceName,
        out T value,
        out string code,
        out string message)
    {
        if (!references.TryGetValue(key, out var result))
        {
            value = default!;
            code = $"SnipeImport.{referenceType}NotPlanned";
            message = $"{referenceType} '{referenceName}' was not planned before asset matching.";
            return false;
        }

        if (!result.Success)
        {
            value = default!;
            code = result.FailureCode ?? $"SnipeImport.{referenceType}Missing";
            message = result.FailureMessage ?? $"{referenceType} '{referenceName}' could not be resolved.";
            return false;
        }

        value = result.Value!;
        code = string.Empty;
        message = string.Empty;
        return true;
    }

    private static PlannedRecord CreatePlannedRecord(
        SnipeAssetImportRecord record,
        ReferencePlans referencePlans,
        SnipeAssetMatch? existingAsset)
    {
        var companyPlan = referencePlans.Companies[BuildReferenceKey(record.CompanyName)].Value!;
        var categoryPlan = referencePlans.Categories[BuildReferenceKey(record.CategoryName)].Value!;
        var manufacturerPlan = referencePlans.Manufacturers[BuildReferenceKey(record.ManufacturerName)].Value!;
        var modelKey = BuildModelReferenceKey(record.ModelName, categoryPlan, manufacturerPlan.ManufacturerId);
        var modelPlan = referencePlans.Models[modelKey].Value!;

        return new PlannedRecord(
            record,
            companyPlan.CompanyId,
            companyPlan.CompanyCreate,
            manufacturerPlan.ManufacturerId,
            modelPlan.ModelId,
            modelPlan.ModelCreate,
            existingAsset);
    }

    private static Dictionary<string, List<SnipeAssetImportRecord>> GroupRecordsByReference(
        IEnumerable<SnipeAssetImportRecord> records,
        Func<SnipeAssetImportRecord, string> referenceSelector)
    {
        var groups = new Dictionary<string, List<SnipeAssetImportRecord>>();
        foreach (var record in records)
        {
            var key = BuildReferenceKey(referenceSelector(record));
            if (!groups.TryGetValue(key, out var groupRecords))
            {
                groupRecords = [];
                groups[key] = groupRecords;
            }

            groupRecords.Add(record);
        }

        return groups;
    }

    private static void ReportProgress(
        IProgress<SyncProgressUpdate>? progress,
        string message,
        int? current,
        int? total)
    {
        progress?.Report(new SyncProgressUpdate
        {
            Stage = "SnipeImport",
            Message = message,
            Current = current,
            Total = total
        });
    }

    private static CompanyPlan PlanCompany(
        string companyName,
        SnipeCompanyLookup companyLookup,
        ImportRunContext context)
    {
        if (context.TryGetPlannedCompany(companyName, out var existingPlan))
        {
            return new CompanyPlan(null, existingPlan);
        }

        var company = companyLookup.Find(companyName);

        if (company is not null)
        {
            return new CompanyPlan(company.Id, null);
        }

        if (!context.Options.CreateMissingCompanies)
        {
            throw new SnipeApiException("SnipeImport.CompanyMissing", $"Company '{companyName}' does not exist and creation is disabled.");
        }

        return new CompanyPlan(null, context.GetOrAddPlannedCompany(companyName));
    }

    private async Task<CategoryPlan> PlanCategoryAsync(
        string categoryName,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        if (context.TryGetPlannedCategory(categoryName, out var existingPlan))
        {
            return new CategoryPlan(null, existingPlan);
        }

        var category = await FindEntityByNameAsync(
            "categories",
            "Category",
            categoryName,
            new Dictionary<string, string>
            {
                ["search"] = categoryName,
                ["category_type"] = AssetCategoryType
            },
            context,
            cancellationToken).ConfigureAwait(false);

        return category is not null
            ? new CategoryPlan(category.Id, null)
            : new CategoryPlan(null, context.GetOrAddPlannedCategory(categoryName));
    }

    /// <summary>
    /// Resolves a manufacturer by its source name first and tries its configured alias only when that direct name is absent.
    /// Missing source and alias names produce one warning and retain a null manufacturer binding.
    /// </summary>
    private async Task<ManufacturerPlan> PlanManufacturerAsync(
        SnipeAssetImportRecord record,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var manufacturer = await FindManufacturerEntityAsync(
            record.ManufacturerName,
            context,
            cancellationToken).ConfigureAwait(false);
        if (manufacturer is not null)
        {
            return new ManufacturerPlan(record.ManufacturerName, manufacturer.Id);
        }

        var effectiveName = record.ManufacturerName;
        if (Normalize(record.ManufacturerAliasName) is { } aliasName)
        {
            effectiveName = aliasName;
            manufacturer = await FindManufacturerEntityAsync(
                aliasName,
                context,
                cancellationToken).ConfigureAwait(false);
            if (manufacturer is not null)
            {
                return new ManufacturerPlan(aliasName, manufacturer.Id);
            }
        }

        context.AddWarning("SnipeImport.ManufacturerMissing", $"Manufacturer '{effectiveName}' does not exist; model will be imported without manufacturer binding.");
        return new ManufacturerPlan(effectiveName, null);
    }

    /// <summary>
    /// Looks up one exact manufacturer name without deciding alias fallback or emitting missing-reference warnings.
    /// </summary>
    private Task<SnipeEntity?> FindManufacturerEntityAsync(
        string manufacturerName,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        return FindEntityByNameAsync(
            "manufacturers",
            "Manufacturer",
            manufacturerName,
            new Dictionary<string, string> { ["name"] = manufacturerName },
            context,
            cancellationToken);
    }

    /// <summary>
    /// Scans the complete model snapshot once and combines category and Fieldset repairs before any asset processing begins.
    /// </summary>
    private static void PlanModelMaintenance(
        SnipeModelLookup modelLookup,
        CategoryPlan? normalizationTargetCategory,
        SnipeFieldset? targetFieldset,
        ImportRunContext context)
    {
        var sourceCategories = context.Options.ModelCategoriesToNormalize
            .Select(NormalizeApiText)
            .Where(name => name is not null)
            .Select(name => NormalizeKey(name!))
            .ToHashSet(StringComparer.Ordinal);
        var targetCategoryName = NormalizeApiText(context.Options.ModelCategoryNormalizationTargetName);

        foreach (var model in modelLookup.Models)
        {
            var changeCategory = normalizationTargetCategory is not null
                && targetCategoryName is not null
                && model.CategoryName is { } currentCategoryName
                && sourceCategories.Contains(NormalizeKey(currentCategoryName))
                && (normalizationTargetCategory.CategoryId != model.CategoryId
                    || normalizationTargetCategory.CategoryCreate is not null);
            var isAlreadyInTargetCategory = targetCategoryName is not null
                && model.CategoryName is { } existingCategoryName
                && string.Equals(
                    NormalizeKey(existingCategoryName),
                    NormalizeKey(targetCategoryName),
                    StringComparison.Ordinal);
            var changeFieldset = targetFieldset is not null
                && (isAlreadyInTargetCategory || changeCategory)
                && model.FieldsetId != targetFieldset.Id;
            if (!changeFieldset && !changeCategory)
            {
                continue;
            }

            context.AddPlannedModelUpdate(new PlannedModelUpdate(
                model,
                changeCategory ? normalizationTargetCategory!.CategoryId : model.CategoryId,
                changeCategory ? targetCategoryName! : model.CategoryName ?? $"Category {model.CategoryId}",
                changeCategory ? normalizationTargetCategory!.CategoryCreate : null,
                changeFieldset ? targetFieldset!.Id : model.FieldsetId,
                changeFieldset ? targetFieldset!.Name : model.FieldsetName,
                changeCategory,
                changeFieldset));
        }
    }

    private static ModelPlan PlanModel(
        SnipeAssetImportRecord record,
        CategoryPlan categoryPlan,
        int? manufacturerId,
        SnipeModelLookup modelLookup,
        SnipeFieldset? targetFieldset,
        ImportRunContext context)
    {
        var modelKey = BuildModelReferenceKey(record.ModelName, categoryPlan, manufacturerId);
        if (context.TryGetPlannedModel(modelKey, out var existingPlan))
        {
            return new ModelPlan(null, existingPlan);
        }

        if (modelLookup.Find(record, categoryPlan, manufacturerId, context) is { } model)
        {
            return new ModelPlan(model.Id, null);
        }

        if (!context.Options.CreateMissingModels)
        {
            throw new SnipeApiException("SnipeImport.ModelMissing", $"Model '{record.ModelName}' does not exist and creation is disabled.");
        }

        var targetCategoryName = NormalizeApiText(context.Options.ModelCategoryNormalizationTargetName);
        var modelFieldset = targetFieldset is not null
            && targetCategoryName is not null
            && string.Equals(
                NormalizeKey(record.CategoryName),
                NormalizeKey(targetCategoryName),
                StringComparison.Ordinal)
                ? targetFieldset
                : null;

        return new ModelPlan(null, context.GetOrAddPlannedModel(
            modelKey,
            record.ModelName,
            record.CategoryName,
            categoryPlan.CategoryId,
            categoryPlan.CategoryCreate,
            record.ManufacturerName,
            manufacturerId,
            modelFieldset?.Id,
            modelFieldset?.Name));
    }

    private static SnipeAssetMatch? FindExistingAsset(
        SnipeAssetImportRecord record,
        ImportRunContext context,
        SnipeHardwareLookup hardwareLookup)
    {
        var assetTagMatch = FindAssetByAssetTag(record.AssetTag, hardwareLookup);
        var macMatch = FindAssetByMac(record, context, hardwareLookup);
        var serial = record.SerialIsReliableIdentity
            ? HardwareIdentityNormalizer.NormalizeSerial(record.Serial)
            : null;
        var serialMatch = serial is null ? null : FindAssetBySerial(serial, hardwareLookup);

        var strongMatches = new List<(string Field, SnipeAssetMatch Match)>();
        if (assetTagMatch is not null)
        {
            strongMatches.Add(("asset tag", assetTagMatch));
        }

        if (macMatch is not null)
        {
            strongMatches.Add(("MAC", macMatch));
        }

        if (serialMatch is not null)
        {
            strongMatches.Add(("serial", serialMatch));
        }

        if (strongMatches.Select(item => item.Match.Id).Distinct().Count() > 1)
        {
            var evidence = string.Join(", ", strongMatches.Select(item => $"{item.Field} -> asset {item.Match.Id}"));
            throw new SnipeApiException(
                "SnipeImport.ConflictingStrongIdentityMatch",
                $"Asset '{record.Name}' matched different Snipe-IT assets by strong identity: {evidence}.");
        }

        var strongMatch = assetTagMatch ?? macMatch ?? serialMatch;
        if (strongMatch is not null)
        {
            if (serial is not null
                && HardwareIdentityNormalizer.NormalizeSerial(strongMatch.Serial) is { } targetSerial
                && !string.Equals(serial, targetSerial, StringComparison.OrdinalIgnoreCase))
            {
                throw new SnipeApiException(
                    "SnipeImport.ConflictingStrongIdentityMatch",
                    $"Asset '{record.Name}' matched Snipe-IT asset {strongMatch.Id}, but its serial does not match the source serial.");
            }

            return strongMatch;
        }

        var nameCandidates = hardwareLookup.FindByReferenceContext(record);
        if (AssetNameMatcher.HasAmbiguousHighConfidenceMatches(record.Name, nameCandidates, context.Options.NameMatchThreshold))
        {
            throw new SnipeApiException("SnipeImport.AmbiguousNameMatch", $"Asset name '{record.Name}' matched multiple high-confidence Snipe-IT assets.");
        }

        return AssetNameMatcher.ChooseHighConfidenceMatch(record.Name, nameCandidates, context.Options.NameMatchThreshold);
    }

    /// <summary>
    /// Restricts weak name fallback so a Snipe-IT asset is only reused when its reference context matches the mapped record.
    /// </summary>
    private static bool HasMatchingFallbackReferences(SnipeAssetImportRecord record, SnipeAssetMatch candidate)
    {
        return NamesEqual(record.CompanyName, candidate.CompanyName)
            && NamesEqual(record.CategoryName, candidate.CategoryName)
            && NamesEqual(record.ModelName, candidate.ModelName);
    }

    private static SnipeAssetMatch? FindAssetByMac(
        SnipeAssetImportRecord record,
        ImportRunContext context,
        SnipeHardwareLookup hardwareLookup)
    {
        var customFieldName = Normalize(context.Options.MacAddressCustomFieldDbColumnName);
        if (customFieldName is null)
        {
            context.AddMacMatchingDisabledWarning();
            return null;
        }

        var matches = new Dictionary<int, SnipeAssetMatch>();
        foreach (var macAddress in record.MacAddresses)
        {
            var comparableMac = MacAddressNormalizer.NormalizeComparable(macAddress);
            if (comparableMac is null)
            {
                context.AddWarning("SnipeImport.InvalidMacAddress", $"Asset '{record.Name}' has invalid MAC address '{macAddress}'.");
                continue;
            }

            if (context.IgnoredMacAddresses.Contains(comparableMac))
            {
                continue;
            }

            foreach (var match in hardwareLookup.FindByMac(comparableMac))
            {
                matches.TryAdd(match.Id, match);
            }
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches.Values.Single(),
            _ => throw new SnipeApiException("SnipeImport.AmbiguousMacMatch", $"Asset '{record.Name}' matched multiple Snipe-IT assets by MAC address.")
        };
    }

    private static SnipeAssetMatch? FindAssetByAssetTag(
        string assetTag,
        SnipeHardwareLookup hardwareLookup)
    {
        var matches = hardwareLookup.FindByAssetTag(assetTag);
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new SnipeApiException(
                "SnipeImport.AmbiguousAssetTagMatch",
                $"Asset tag '{assetTag}' matched multiple Snipe-IT assets.")
        };
    }

    private static SnipeAssetMatch? FindAssetBySerial(
        string serial,
        SnipeHardwareLookup hardwareLookup)
    {
        var matches = hardwareLookup.FindBySerial(serial);
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new SnipeApiException("SnipeImport.AmbiguousSerialMatch", $"Serial '{serial}' matched multiple Snipe-IT assets.")
        };
    }

    private static void ApplyDryRunPlan(
        IReadOnlyList<PlannedRecord> plannedRecords,
        ImportRunContext context)
    {
        foreach (var company in context.PlannedCompanies)
        {
            context.AddPlannedAction("Create", "Company", company.Name, $"Company '{company.Name}' is missing.");
            context.CreatedCompanies++;
        }

        foreach (var category in context.PlannedCategories)
        {
            context.AddPlannedAction("Create", "Category", category.Name, $"Asset category '{category.Name}' is missing.");
            context.CreatedCategories++;
        }

        foreach (var model in context.PlannedModels)
        {
            context.AddPlannedAction("Create", "Model", model.Name, $"Model '{model.Name}' is missing.");
            context.CreatedModels++;
        }

        foreach (var model in context.PlannedModelUpdates)
        {
            context.AddPlannedAction(
                "Update",
                "Model",
                model.ModelName,
                $"Model '{model.ModelName}' requires {BuildModelChangeReasons(model)}.");
            context.UpdatedModels++;
        }

        foreach (var plannedRecord in plannedRecords)
        {
            if (plannedRecord.ExistingAsset is null)
            {
                context.AddPlannedAction("Create", "Asset", plannedRecord.Record.AssetTag, $"Asset '{plannedRecord.Record.Name}' does not exist.");
                context.CreatedAssets++;
                continue;
            }

            context.AddPlannedAction(
                "Update",
                "Asset",
                plannedRecord.ExistingAsset.AssetTag ?? plannedRecord.ExistingAsset.Name,
                $"Asset '{plannedRecord.Record.Name}' matched Snipe-IT asset id {plannedRecord.ExistingAsset.Id}.");
            context.UpdatedAssets++;
        }
    }

    private async Task ExecutePlanAsync(
        PlannedRecord plannedRecord,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var companyId = plannedRecord.CompanyId ?? plannedRecord.CompanyCreate?.CreatedId;
        var modelId = plannedRecord.ModelId ?? plannedRecord.ModelCreate?.CreatedId;

        if (modelId is null)
        {
            throw new SnipeApiException("SnipeImport.ModelMissing", $"Model '{plannedRecord.Record.ModelName}' was not created before asset import.");
        }

        if (plannedRecord.ExistingAsset is null)
        {
            await CreateAssetAsync(plannedRecord.Record, modelId.Value, companyId, context, cancellationToken).ConfigureAwait(false);
            return;
        }

        await UpdateAssetAsync(plannedRecord.Record, plannedRecord.ExistingAsset, modelId.Value, companyId, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryExecuteReferencePlanAsync(
        IReadOnlyList<PlannedRecord> plannedRecords,
        ImportRunContext context,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var companies = context.PlannedCompanies.ToList();
        var categories = context.PlannedCategories.ToList();
        var models = context.PlannedModels.ToList();
        var modelUpdates = context.PlannedModelUpdates.ToList();
        var referenceTotal = companies.Count + categories.Count + models.Count + modelUpdates.Count;
        if (referenceTotal == 0)
        {
            ReportProgress(
                progress,
                "No missing Snipe-IT reference records need to be created.",
                current: 0,
                total: 0);
            return true;
        }

        var currentReference = 0;
        var overallStartedAt = Stopwatch.GetTimestamp();
        try
        {
            ReportProgress(
                progress,
                $"Preparing Snipe-IT reference records before asset writes: total={referenceTotal}; companies={companies.Count}; categories={categories.Count}; models added={models.Count}; models updated={modelUpdates.Count}.",
                0,
                referenceTotal);

            foreach (var company in companies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteReferenceWithProgressAsync(
                    "Company",
                    company.Name,
                    ++currentReference,
                    referenceTotal,
                    () => ExecuteCompanyCreateAsync(company, context, cancellationToken),
                    progress).ConfigureAwait(false);
            }

            foreach (var category in categories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteReferenceWithProgressAsync(
                    "Category",
                    category.Name,
                    ++currentReference,
                    referenceTotal,
                    () => ExecuteCategoryCreateAsync(category, context, cancellationToken),
                    progress).ConfigureAwait(false);
            }

            foreach (var model in models)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteReferenceWithProgressAsync(
                    "Model",
                    model.Name,
                    ++currentReference,
                    referenceTotal,
                    () => ExecuteModelCreateAsync(model, context, cancellationToken),
                    progress).ConfigureAwait(false);
            }

            foreach (var model in modelUpdates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteReferenceWithProgressAsync(
                    "Model update",
                    model.ModelName,
                    ++currentReference,
                    referenceTotal,
                    async () =>
                    {
                        await ExecuteModelUpdateAsync(model, context, cancellationToken).ConfigureAwait(false);
                        return model.ModelId;
                    },
                    progress).ConfigureAwait(false);
            }

            ReportProgress(
                progress,
                $"Prepared all Snipe-IT reference records: total={referenceTotal}; elapsed={FormatElapsed(Stopwatch.GetElapsedTime(overallStartedAt))}s.",
                referenceTotal,
                referenceTotal);
            return true;
        }
        catch (SnipeApiException exception)
        {
            AddReferenceCreationFailure(plannedRecords, context, exception.Code, exception.Message);
            return false;
        }
        catch (JsonException exception)
        {
            AddReferenceCreationFailure(plannedRecords, context, "SnipeImport.MalformedResponse", $"Snipe-IT response could not be parsed: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Executes one planned reference mutation while reporting its exact position, safe target name, duration, and failure code.
    /// </summary>
    private async Task<T> ExecuteReferenceWithProgressAsync<T>(
        string referenceType,
        string referenceName,
        int current,
        int total,
        Func<Task<T>> executeAsync,
        IProgress<SyncProgressUpdate>? progress)
    {
        var safeName = SanitizeProgressValue(referenceName);
        ReportProgress(
            progress,
            $"Creating Snipe-IT {referenceType} reference '{safeName}'.",
            current,
            total);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var result = await executeAsync().ConfigureAwait(false);
            ReportProgress(
                progress,
                $"Created Snipe-IT {referenceType} reference '{safeName}' in {FormatElapsed(Stopwatch.GetElapsedTime(startedAt))}s.",
                current,
                total);
            return result;
        }
        catch (SnipeApiException exception)
        {
            ReportProgress(
                progress,
                $"Failed Snipe-IT {referenceType} reference '{safeName}' after {FormatElapsed(Stopwatch.GetElapsedTime(startedAt))}s ({exception.Code}).",
                current,
                total);
            throw;
        }
        catch (JsonException)
        {
            ReportProgress(
                progress,
                $"Failed Snipe-IT {referenceType} reference '{safeName}' after {FormatElapsed(Stopwatch.GetElapsedTime(startedAt))}s (SnipeImport.MalformedResponse).",
                current,
                total);
            throw;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string SanitizeProgressValue(string value)
    {
        return value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static void AddReferenceCreationFailure(
        IReadOnlyList<PlannedRecord> plannedRecords,
        ImportRunContext context,
        string code,
        string message)
    {
        var assetMessage = $"Reference creation failed before asset import: {message}";
        foreach (var plannedRecord in plannedRecords)
        {
            context.AddFailure(plannedRecord.Record, code, assetMessage);
        }
    }

    private async Task<int> ExecuteCompanyCreateAsync(
        PlannedCompanyCreate company,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        if (company.CreatedId is not null)
        {
            return company.CreatedId.Value;
        }

        company.CreatedId = await CreateEntityAsync(
            "companies",
            "Company",
            company.Name,
            new Dictionary<string, object?>
            {
                ["name"] = company.Name,
                ["notes"] = BuildFirstAddedNote(_timeProvider.GetUtcNow())
            },
            context.Options,
            cancellationToken).ConfigureAwait(false);
        context.AddExecutedAction("Create", "Company", company.Name, $"Company '{company.Name}' was created.");
        context.CreatedCompanies++;
        return company.CreatedId.Value;
    }

    private async Task<int> ExecuteModelCreateAsync(
        PlannedModelCreate model,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        if (model.CreatedId is not null)
        {
            return model.CreatedId.Value;
        }

        var categoryId = model.CategoryId;
        if (categoryId is null && model.CategoryCreate is not null)
        {
            categoryId = await ExecuteCategoryCreateAsync(model.CategoryCreate, context, cancellationToken).ConfigureAwait(false);
        }

        if (categoryId is null)
        {
            throw new SnipeApiException("SnipeImport.CategoryMissing", $"Category '{model.CategoryName}' was not created before model import.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["name"] = model.Name,
            ["category_id"] = categoryId.Value,
            ["notes"] = BuildFirstAddedNote(_timeProvider.GetUtcNow())
        };

        if (model.ManufacturerId is not null)
        {
            payload["manufacturer_id"] = model.ManufacturerId.Value;
        }

        if (model.FieldsetId is not null)
        {
            payload["fieldset_id"] = model.FieldsetId.Value;
        }

        model.CreatedId = await CreateEntityAsync(
            "models",
            "Model",
            model.Name,
            payload,
            context.Options,
            cancellationToken).ConfigureAwait(false);
        context.AddExecutedAction("Create", "Model", model.Name, $"Model '{model.Name}' was created.");
        context.CreatedModels++;
        return model.CreatedId.Value;
    }

    /// <summary>
    /// Applies the combined category and Fieldset repair for one existing model before dependent assets are written.
    /// </summary>
    private async Task ExecuteModelUpdateAsync(
        PlannedModelUpdate model,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>();
        if (model.ChangeCategory)
        {
            var categoryId = model.TargetCategoryId;
            if (categoryId is null && model.TargetCategoryCreate is not null)
            {
                categoryId = await ExecuteCategoryCreateAsync(
                    model.TargetCategoryCreate,
                    context,
                    cancellationToken).ConfigureAwait(false);
            }

            if (categoryId is null)
            {
                throw new SnipeApiException(
                    "SnipeImport.CategoryMissing",
                    $"Target category '{model.TargetCategoryName}' was not created before model update.");
            }

            payload["category_id"] = categoryId.Value;
        }

        if (model.ChangeFieldset)
        {
            if (model.TargetFieldsetId is null)
            {
                throw new SnipeApiException(
                    "SnipeImport.MacFieldsetMissing",
                    $"Target Fieldset '{model.TargetFieldsetName}' was not resolved before model update.");
            }

            payload["fieldset_id"] = model.TargetFieldsetId.Value;
        }

        if (payload.Count == 0)
        {
            return;
        }

        var relativePath = $"models/{model.ModelId}";
        var operation = $"Update Model '{model.ModelName}'";
        using var document = await SendJsonAsync(
            HttpMethod.Patch,
            relativePath,
            payload,
            operation,
            context.Options,
            cancellationToken).ConfigureAwait(false);
        EnsureBusinessSuccess(document.RootElement, HttpMethod.Patch, relativePath, operation);
        context.AddExecutedAction(
            "Update",
            "Model",
            model.ModelName,
            $"Model '{model.ModelName}' was updated ({BuildModelChangeReasons(model)}).");
        context.UpdatedModels++;
    }

    private static string BuildModelChangeReasons(PlannedModelUpdate model)
    {
        var reasons = new List<string>(2);
        if (model.ChangeCategory)
        {
            reasons.Add("category normalization");
        }

        if (model.ChangeFieldset)
        {
            reasons.Add("MAC Fieldset assignment");
        }

        return string.Join(" and ", reasons);
    }

    private async Task<int> ExecuteCategoryCreateAsync(
        PlannedCategoryCreate category,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        if (category.CreatedId is not null)
        {
            return category.CreatedId.Value;
        }

        category.CreatedId = await CreateEntityAsync(
            "categories",
            "Category",
            category.Name,
            new Dictionary<string, object?>
            {
                ["name"] = category.Name,
                ["category_type"] = AssetCategoryType,
                ["notes"] = BuildFirstAddedNote(_timeProvider.GetUtcNow())
            },
            context.Options,
            cancellationToken).ConfigureAwait(false);
        context.AddExecutedAction("Create", "Category", category.Name, $"Asset category '{category.Name}' was created.");
        context.CreatedCategories++;
        return category.CreatedId.Value;
    }

    private async Task CreateAssetAsync(
        SnipeAssetImportRecord record,
        int modelId,
        int? companyId,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        const string relativePath = "hardware";
        var operation = $"Create Asset '{record.AssetTag}'";

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            relativePath,
            BuildAssetPayload(
                record,
                modelId,
                companyId,
                context.Options,
                context.IgnoredMacAddresses,
                existingNotes: null,
                includeFirstAddedNote: true,
                _timeProvider.GetUtcNow()),
            operation,
            context.Options,
            cancellationToken).ConfigureAwait(false);
        EnsureBusinessSuccess(document.RootElement, HttpMethod.Post, relativePath, operation);
        _ = ReadEntityId(document.RootElement)
            ?? throw new SnipeApiException(
                "SnipeImport.MissingResponseId",
                $"{DescribeRequest(operation, HttpMethod.Post, relativePath)} failed: Snipe-IT reported success but did not include an id.");
        context.AddExecutedAction("Create", "Asset", record.AssetTag, $"Asset '{record.Name}' was created.");
        context.CreatedAssets++;
    }

    private async Task UpdateAssetAsync(
        SnipeAssetImportRecord record,
        SnipeAssetMatch existingAsset,
        int modelId,
        int? companyId,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var relativePath = $"hardware/{existingAsset.Id}";
        var operation = $"Update Asset '{existingAsset.AssetTag ?? record.AssetTag}'";

        using var document = await SendJsonAsync(
            HttpMethod.Patch,
            relativePath,
            BuildAssetPayload(
                record,
                modelId,
                companyId,
                context.Options,
                context.IgnoredMacAddresses,
                existingAsset.Notes,
                includeFirstAddedNote: false,
                _timeProvider.GetUtcNow()),
            operation,
            context.Options,
            cancellationToken).ConfigureAwait(false);
        EnsureBusinessSuccess(document.RootElement, HttpMethod.Patch, relativePath, operation);
        context.AddExecutedAction(
            "Update",
            "Asset",
            existingAsset.AssetTag ?? existingAsset.Name,
            $"Asset '{record.Name}' updated Snipe-IT asset id {existingAsset.Id}.");
        context.UpdatedAssets++;
    }

    private async Task<bool> TryWritePreflightCsvAsync(
        IReadOnlyList<PlannedRecord> plannedRecords,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await SnipeImportPreflightCsvWriter.WriteAsync(
                BuildPreflightPlan(plannedRecords, context),
                context.Options.ManualPreflightCsvDirectory!,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            context.AddPreflightFailure(
                "SnipeImport.PreflightCsvWriteFailed",
                $"Manual preflight CSV files could not be written: {exception.Message}");
            return false;
        }
    }

    private static SnipeImportPreflightPlan BuildPreflightPlan(
        IReadOnlyList<PlannedRecord> plannedRecords,
        ImportRunContext context)
    {
        return new SnipeImportPreflightPlan
        {
            Assets = plannedRecords.Select(plannedRecord => new SnipeAssetPreflightRow(
                plannedRecord.ExistingAsset is null ? "Add" : "Modify",
                plannedRecord.Record.AssetTag,
                plannedRecord.Record.Name,
                HardwareIdentityNormalizer.NormalizeSerial(plannedRecord.Record.Serial),
                FormatMacAddresses(plannedRecord.Record),
                plannedRecord.Record.CompanyName,
                plannedRecord.Record.ModelName,
                plannedRecord.Record.CategoryName,
                plannedRecord.Record.ManufacturerName,
                plannedRecord.ExistingAsset?.Id,
                plannedRecord.ExistingAsset?.AssetTag,
                ConflictingFields: null,
                ConflictingValue: null,
                ConflictingAssets: null,
                FailureCode: null,
                FailureMessage: null,
                DeviceType: plannedRecord.Record.DeviceType))
                .Concat(context.FailedPreflightAssets)
                .ToList(),
            Companies = context.PlannedCompanies
                .Select(company => new SnipeCompanyPreflightRow("Add", company.Name))
                .ToList(),
            Categories = context.PlannedCategories
                .Select(category => new SnipeCategoryPreflightRow("Add", category.Name, AssetCategoryType))
                .ToList(),
            Models = context.PlannedModels
                .Select(model => new SnipeModelPreflightRow(
                    "Add",
                    model.Name,
                    ExistingModelId: null,
                    CurrentCategoryName: null,
                    CurrentCategoryId: null,
                    TargetCategoryName: model.CategoryName,
                    TargetCategoryId: model.CategoryId,
                    model.ManufacturerName,
                    model.ManufacturerId,
                    CurrentFieldsetName: null,
                    CurrentFieldsetId: null,
                    TargetFieldsetName: model.FieldsetName,
                    TargetFieldsetId: model.FieldsetId,
                    ChangeReasons: "Create"))
                .Concat(context.PlannedModelUpdates.Select(model => new SnipeModelPreflightRow(
                    "Modify",
                    model.ModelName,
                    model.ModelId,
                    model.CurrentCategoryName,
                    model.CurrentCategoryId,
                    model.TargetCategoryName,
                    model.TargetCategoryId,
                    ManufacturerName: string.Empty,
                    ManufacturerId: null,
                    model.CurrentFieldsetName,
                    model.CurrentFieldsetId,
                    model.TargetFieldsetName,
                    model.TargetFieldsetId,
                    ChangeReasons: string.Join(
                        "; ",
                        new[]
                        {
                            model.ChangeCategory ? "Category" : null,
                            model.ChangeFieldset ? "Fieldset" : null
                        }.Where(reason => reason is not null)))))
                .ToList()
        };
    }

    private async Task<SnipeEntity?> FindEntityByNameAsync(
        string path,
        string targetType,
        string expectedName,
        IReadOnlyDictionary<string, string> query,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildEntityLookupCacheKey(path, expectedName, query);
        if (context.TryGetEntityLookup(cacheKey, out var cachedEntity))
        {
            return cachedEntity;
        }

        var relativePath = BuildPath(path, query);
        var operation = $"Lookup {targetType} '{expectedName}'";
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            relativePath,
            payload: null,
            operation,
            context.Options,
            cancellationToken).ConfigureAwait(false);

        EnsureBusinessSuccess(document.RootElement, HttpMethod.Get, relativePath, operation);

        var entity = ParseRows(document.RootElement, operation)
            .Select(row => ParseEntity(row)
                ?? throw new SnipeApiException(
                    "SnipeImport.MalformedResponse",
                    $"{operation} returned a row without a valid id or name."))
            .FirstOrDefault(entity => string.Equals(entity.Name, expectedName, StringComparison.OrdinalIgnoreCase));
        context.CacheEntityLookup(cacheKey, entity);
        return entity;
    }

    private async Task<int> CreateEntityAsync(
        string path,
        string targetType,
        string targetName,
        object payload,
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        var operation = $"Create {targetType} '{targetName}'";
        using var document = await SendJsonAsync(
            HttpMethod.Post,
            path,
            payload,
            operation,
            options,
            cancellationToken).ConfigureAwait(false);
        EnsureBusinessSuccess(document.RootElement, HttpMethod.Post, path, operation);
        return ReadEntityId(document.RootElement)
            ?? throw new SnipeApiException(
                "SnipeImport.MissingResponseId",
                $"{DescribeRequest(operation, HttpMethod.Post, path)} failed: Snipe-IT reported success but did not include an id.");
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        string operation,
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        var serializedPayload = payload is null ? null : JsonSerializer.Serialize(payload, JsonOptions);
        var maxAttempts = method == HttpMethod.Get ? options.MaxReadRetryAttempts + 1 : 1;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestCancellationToken = method == HttpMethod.Get ? cancellationToken : CancellationToken.None;
            using var request = CreateRequest(method, relativePath, serializedPayload, options);

            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellationToken).ConfigureAwait(false);

                if (attempt + 1 < maxAttempts && IsRetryableStatus(response.StatusCode))
                {
                    await DelayBeforeRetryAsync(response, options, attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(requestCancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var errorDetail = TryReadErrorDetails(content, out var hasValidationDetails);
                    var code = ClassifyHttpFailure(response.StatusCode, hasValidationDetails);
                    var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                        ? $"HTTP {(int)response.StatusCode}"
                        : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    var detailSuffix = errorDetail is null ? string.Empty : $" Detail: {errorDetail}";
                    throw new SnipeApiException(
                        code,
                        $"{DescribeRequest(operation, method, relativePath)} failed: Snipe-IT returned {reason}.{detailSuffix}");
                }

                try
                {
                    return JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
                }
                catch (JsonException exception)
                {
                    throw new SnipeApiException(
                        "SnipeImport.MalformedResponse",
                        $"{DescribeRequest(operation, method, relativePath)} failed: Snipe-IT returned malformed JSON.",
                        exception);
                }
            }
            catch (OperationCanceledException exception) when (!requestCancellationToken.IsCancellationRequested)
            {
                if (attempt + 1 < maxAttempts)
                {
                    await DelayBeforeRetryAsync(response: null, options, attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new SnipeApiException(
                    "SnipeImport.Timeout",
                    $"{DescribeRequest(operation, method, relativePath)} failed: the Snipe-IT request timed out.",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                if (attempt + 1 < maxAttempts)
                {
                    await DelayBeforeRetryAsync(response: null, options, attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new SnipeApiException(
                    "SnipeImport.NetworkFailure",
                    $"{DescribeRequest(operation, method, relativePath)} failed: network request error: {exception.Message}",
                    exception);
            }
        }

        throw new InvalidOperationException("Snipe-IT request retry loop ended unexpectedly.");
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        string? serializedPayload,
        SnipeImportOptions options)
    {
        var request = new HttpRequestMessage(method, BuildUri(relativePath, options));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
        if (serializedPayload is not null)
        {
            request.Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private async Task DelayBeforeRetryAsync(
        HttpResponseMessage? response,
        SnipeImportOptions options,
        int attempt,
        CancellationToken cancellationToken)
    {
        var retryAfter = ReadRetryAfter(response);
        var exponentialMilliseconds = options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var jitterMilliseconds = options.RetryBaseDelay.TotalMilliseconds <= 0
            ? 0
            : Random.Shared.NextDouble() * Math.Min(250, options.RetryBaseDelay.TotalMilliseconds);
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(exponentialMilliseconds + jitterMilliseconds);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode)
    {
        return statusCode == (HttpStatusCode)429
            || statusCode is HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - _timeProvider.GetUtcNow();
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static Uri BuildUri(string relativePath, SnipeImportOptions options)
    {
        return new Uri(ApiEndpointValidator.ValidateSnipeBaseUri(options.BaseUrl), relativePath.TrimStart('/'));
    }

    private static Dictionary<string, object?> BuildAssetPayload(
        SnipeAssetImportRecord record,
        int modelId,
        int? companyId,
        SnipeImportOptions options,
        IReadOnlySet<string> ignoredMacAddresses,
        string? existingNotes,
        bool includeFirstAddedNote,
        DateTimeOffset syncedAt)
    {
        var payload = new Dictionary<string, object?>
        {
            ["asset_tag"] = record.AssetTag,
            ["status_id"] = record.StatusId,
            ["model_id"] = modelId,
            ["name"] = record.Name,
            ["notes"] = BuildAssetNotes(record.Notes, existingNotes, includeFirstAddedNote, syncedAt)
        };

        if (record.SerialIsReliableIdentity
            && HardwareIdentityNormalizer.NormalizeSerial(record.Serial) is { } serial)
        {
            payload["serial"] = serial;
        }

        if (companyId is not null)
        {
            payload["company_id"] = companyId.Value;
        }

        if (Normalize(options.MacAddressCustomFieldDbColumnName) is { } macFieldName
            && record.MacAddresses
                .Select(value => (Comparable: MacAddressNormalizer.NormalizeComparable(value), Display: MacAddressNormalizer.NormalizeDisplay(value)))
                .FirstOrDefault(value => value.Comparable is not null
                    && value.Display is not null
                    && !ignoredMacAddresses.Contains(value.Comparable)).Display is { } macAddress)
        {
            payload[macFieldName] = macAddress;
        }

        return payload;
    }

    /// <summary>
    /// Builds hardware notes while preserving the immutable first-created audit line on later updates.
    /// </summary>
    private static string BuildAssetNotes(
        string notes,
        string? existingNotes,
        bool includeFirstAddedNote,
        DateTimeOffset syncedAt)
    {
        var lines = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(notes))
        {
            lines.Add(notes.TrimEnd());
        }

        var firstAddedNote = includeFirstAddedNote
            ? BuildFirstAddedNote(syncedAt)
            : FindFirstAddedNote(existingNotes);
        if (firstAddedNote is not null)
        {
            lines.Add(firstAddedNote);
        }

        lines.Add($"Auto Synced from Atera at {FormatUtcTimestamp(syncedAt)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFirstAddedNote(DateTimeOffset createdAt)
    {
        return $"{FirstAddedNotePrefix}{FormatUtcTimestamp(createdAt)}";
    }

    private static string FormatUtcTimestamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FindFirstAddedNote(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        foreach (var line in notes.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith(FirstAddedNotePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var timestamp = line[FirstAddedNotePrefix.Length..];
            if (DateTimeOffset.TryParseExact(
                    timestamp,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out _))
            {
                return line;
            }
        }

        return null;
    }

    private static string BuildPath(
        string path,
        IReadOnlyDictionary<string, string> query)
    {
        if (query.Count == 0)
        {
            return path;
        }

        var queryString = string.Join(
            "&",
            query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        return $"{path}?{queryString}";
    }

    private static string BuildEntityLookupCacheKey(
        string path,
        string expectedName,
        IReadOnlyDictionary<string, string> query)
    {
        var queryString = string.Join(
            "&",
            query
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{NormalizeKey(item.Key)}={NormalizeKey(item.Value)}"));
        return $"{NormalizeKey(path)}|{NormalizeKey(expectedName)}|{queryString}";
    }

    private static string BuildReferenceKey(string value)
    {
        return NormalizeKey(value);
    }

    private static string BuildModelReferenceKey(string modelName, int categoryId, int? manufacturerId)
    {
        return $"{NormalizeKey(modelName)}|{categoryId.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{manufacturerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}";
    }

    private static string BuildModelReferenceKey(string modelName, CategoryPlan categoryPlan, int? manufacturerId)
    {
        var categoryKey = categoryPlan.CategoryId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? $"CREATE:{NormalizeKey(categoryPlan.CategoryCreate?.Name ?? "unknown")}";
        return $"{NormalizeKey(modelName)}|{categoryKey}|{manufacturerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}";
    }

    private static IReadOnlyList<JsonElement> ParseRows(JsonElement root, string operation)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            return rows.EnumerateArray().ToList();
        }

        throw new SnipeApiException(
            "SnipeImport.MalformedResponse",
            $"{operation} returned JSON without the required rows array.");
    }

    private static int? ReadTotal(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object ? ReadInt(root, "total") : null;
    }

    private static SnipeEntity? ParseEntity(JsonElement element)
    {
        var id = ReadInt(element, "id");
        var name = ReadString(element, "name");
        return id is null || name is null ? null : new SnipeEntity(id.Value, name);
    }

    private static SnipeModel? ParseModel(JsonElement element)
    {
        var id = ReadInt(element, "id");
        var name = ReadString(element, "name");
        var categoryId = ReadEntityId(element, "category");
        return id is null || name is null || categoryId is null
            ? null
            : new SnipeModel(
                id.Value,
                name,
                ReadString(element, "model_number"),
                categoryId.Value,
                ReadEntityName(element, "category"),
                ReadEntityId(element, "manufacturer"),
                ReadEntityName(element, "manufacturer"),
                ReadEntityId(element, "fieldset"),
                ReadEntityName(element, "fieldset"));
    }

    private static SnipeFieldset ParseFieldset(JsonElement element)
    {
        var id = ReadInt(element, "id");
        var name = ReadString(element, "name");
        if (id is null || id <= 0 || name is null)
        {
            throw new SnipeApiException(
                "SnipeImport.MalformedResponse",
                "Custom Fieldset snapshot contained a row without a valid id or name.");
        }

        if (!element.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Object
            || !fields.TryGetProperty("rows", out var fieldRows)
            || fieldRows.ValueKind != JsonValueKind.Array)
        {
            throw new SnipeApiException(
                "SnipeImport.MalformedResponse",
                $"Custom Fieldset '{name}' did not contain the required fields.rows array.");
        }

        var dbColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fieldRow in fieldRows.EnumerateArray())
        {
            var dbColumn = ReadString(fieldRow, "db_column_name");
            if (dbColumn is null)
            {
                throw new SnipeApiException(
                    "SnipeImport.MalformedResponse",
                    $"Custom Fieldset '{name}' contained a field row without db_column_name.");
            }

            dbColumns.Add(dbColumn);
        }

        return new SnipeFieldset(id.Value, name, dbColumns);
    }

    private static SnipeAssetMatch? ParseAsset(JsonElement element)
    {
        var id = ReadInt(element, "id");
        if (id is null)
        {
            return null;
        }

        return new SnipeAssetMatch(
            id.Value,
            ReadString(element, "name") ?? ReadString(element, "asset_tag") ?? id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ReadString(element, "asset_tag"),
            ReadString(element, "serial"),
            ReadEntityName(element, "company"),
            ReadEntityName(element, "category") ?? ReadEntityName(element, "asset_category"),
            ReadEntityName(element, "model"),
            ReadString(element, "notes"),
            ReadCustomFields(element));
    }

    private static int? ReadEntityId(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadInt(value, "id");
    }

    private static string? ReadEntityName(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => ReadString(value, "name"),
            JsonValueKind.String => NormalizeApiText(value.GetString()),
            JsonValueKind.Number => Normalize(value.GetRawText()),
            _ => null
        };
    }

    private static IReadOnlyDictionary<string, string> ReadCustomFields(JsonElement element)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return fields;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.StartsWith("_snipeit_", StringComparison.OrdinalIgnoreCase)
                && ReadSimpleString(property.Value) is { } topLevelValue)
            {
                fields[property.Name] = topLevelValue;
            }
        }

        if (!element.TryGetProperty("custom_fields", out var customFields) || customFields.ValueKind != JsonValueKind.Object)
        {
            return fields;
        }

        foreach (var customField in customFields.EnumerateObject())
        {
            if (customField.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var fieldName = ReadString(customField.Value, "field");
            var fieldValue = ReadString(customField.Value, "value");
            if (fieldName is not null && fieldValue is not null)
            {
                fields[fieldName] = fieldValue;
            }
        }

        return fields;
    }

    private static string? ReadSimpleString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => NormalizeApiText(element.GetString()),
            JsonValueKind.Number => Normalize(element.GetRawText()),
            _ => null
        };
    }

    private static int? ReadEntityId(JsonElement root)
    {
        if (ReadInt(root, "id") is { } directId)
        {
            return directId;
        }

        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object
            ? ReadInt(payload, "id")
            : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeApiText(value.GetString()),
            JsonValueKind.Number => Normalize(value.GetRawText()),
            _ => null
        };
    }

    private static bool IsBusinessError(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts an HTTP 200 Snipe-IT error envelope into a classified exception while retaining safe field-level details.
    /// </summary>
    private static void EnsureBusinessSuccess(
        JsonElement root,
        HttpMethod method,
        string relativePath,
        string operation)
    {
        if (!IsBusinessError(root))
        {
            return;
        }

        var errorDetail = ReadErrorDetails(root, out var hasValidationDetails)
            ?? "No error detail was included in messages or message.";
        var code = hasValidationDetails
            ? "SnipeImport.ValidationError"
            : "SnipeImport.BusinessError";
        var failureKind = hasValidationDetails ? "validation error" : "business error";
        throw new SnipeApiException(
            code,
            $"{DescribeRequest(operation, method, relativePath)} failed: Snipe-IT returned a {failureKind}. Detail: {errorDetail}");
    }

    /// <summary>
    /// Maps transport status to stable operator-facing failure codes without relying on localized response text.
    /// </summary>
    private static string ClassifyHttpFailure(HttpStatusCode statusCode, bool hasValidationDetails)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "SnipeImport.AuthenticationFailed",
            HttpStatusCode.Forbidden => "SnipeImport.AuthorizationFailed",
            (HttpStatusCode)429 => "SnipeImport.RateLimited",
            _ when (int)statusCode >= 500 => "SnipeImport.ServerError",
            _ when hasValidationDetails => "SnipeImport.ValidationError",
            _ => "SnipeImport.HttpFailure"
        };
    }

    /// <summary>
    /// Parses only documented Snipe-IT error fields from a non-success response and never returns the raw response body.
    /// </summary>
    private static string? TryReadErrorDetails(string content, out bool hasValidationDetails)
    {
        hasValidationDetails = false;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadErrorDetails(document.RootElement, out hasValidationDetails);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads Snipe-IT messages/message content and identifies the documented object/array validation shape.
    /// </summary>
    private static string? ReadErrorDetails(JsonElement root, out bool hasValidationDetails)
    {
        hasValidationDetails = false;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("messages", out var messages))
        {
            hasValidationDetails = messages.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
            return FormatErrorDetails(messages);
        }

        return root.TryGetProperty("message", out var message)
            ? FormatErrorDetails(message)
            : null;
    }

    /// <summary>
    /// Flattens nested validation fields into concise field/reason pairs and caps output before it reaches logs or reports.
    /// </summary>
    private static string? FormatErrorDetails(JsonElement element)
    {
        var details = new List<string>();
        AppendErrorDetails(element, fieldPath: null, details);
        var combined = string.Join(
            "; ",
            details
                .Where(detail => !string.IsNullOrWhiteSpace(detail))
                .Distinct(StringComparer.Ordinal));
        if (combined.Length == 0)
        {
            return null;
        }

        return combined.Length <= MaximumErrorDetailLength
            ? combined
            : string.Concat(combined.AsSpan(0, MaximumErrorDetailLength), "...");
    }

    /// <summary>
    /// Walks an error value recursively so arrays and nested objects retain their owning validation field name.
    /// </summary>
    private static void AppendErrorDetails(JsonElement element, string? fieldPath, List<string> details)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = fieldPath is null
                        ? property.Name
                        : $"{fieldPath}.{property.Name}";
                    AppendErrorDetails(property.Value, childPath, details);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendErrorDetails(item, fieldPath, details);
                }

                break;
            case JsonValueKind.String:
                AddErrorDetail(WebUtility.HtmlDecode(element.GetString()), fieldPath, details);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                AddErrorDetail(element.GetRawText(), fieldPath, details);
                break;
        }
    }

    /// <summary>
    /// Adds one normalized error detail, prefixing field-level reasons with their response field path.
    /// </summary>
    private static void AddErrorDetail(string? value, string? fieldPath, List<string> details)
    {
        if (Normalize(value) is not { } normalized)
        {
            return;
        }

        details.Add(fieldPath is null ? normalized : $"{fieldPath}: {normalized}");
    }

    /// <summary>
    /// Produces a safe request label from caller-provided operation context and a relative API path.
    /// </summary>
    private static string DescribeRequest(string operation, HttpMethod method, string relativePath)
    {
        return $"{operation} via {method.Method} /{relativePath.TrimStart('/')}";
    }

    private static void ValidateRecord(SnipeAssetImportRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (Normalize(record.AssetTag) is null
            || Normalize(record.Name) is null
            || Normalize(record.CompanyName) is null
            || Normalize(record.ModelName) is null
            || Normalize(record.CategoryName) is null
            || record.StatusId <= 0)
        {
            throw new SnipeApiException("SnipeImport.InvalidRecord", $"Asset '{record.AssetTag}' is missing required import fields.");
        }
    }

    /// <summary>
    /// Finds identities shared by different source records before any Snipe-IT lookup or write is attempted.
    /// </summary>
    private static IReadOnlyDictionary<SnipeAssetImportRecord, BatchIdentityFailure> FindBatchIdentityFailures(
        IReadOnlyList<SnipeAssetImportRecord> records,
        IReadOnlySet<string> ignoredMacAddresses)
    {
        var conflicts = new Dictionary<SnipeAssetImportRecord, List<BatchIdentityConflict>>();
        AddDuplicateIdentityFailures(records, record => Normalize(record.SourceId), "source id", conflicts);
        AddDuplicateIdentityFailures(records, record => Normalize(record.AssetTag), "asset tag", conflicts);
        AddDuplicateIdentityFailures(
            records,
            record => record.SerialIsReliableIdentity ? HardwareIdentityNormalizer.NormalizeSerial(record.Serial) : null,
            "serial",
            conflicts);

        foreach (var group in records
            .SelectMany(record => record.MacAddresses
                .Select(MacAddressNormalizer.NormalizeComparable)
                .Where(value => value is not null)
                .Where(value => !ignoredMacAddresses.Contains(value!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => (Value: value!, Record: record)))
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Record).Distinct().Count() > 1))
        {
            var conflictingRecords = group
                .Select(item => item.Record)
                .Distinct()
                .ToList();
            var displayValue = MacAddressNormalizer.NormalizeDisplay(group.Key) ?? group.Key;
            foreach (var record in conflictingRecords)
            {
                AddIdentityConflict(
                    conflicts,
                    record,
                    "MAC address",
                    displayValue,
                    conflictingRecords.Where(candidate => !ReferenceEquals(candidate, record)));
            }
        }

        return conflicts.ToDictionary(
            item => item.Key,
            item => BuildBatchIdentityFailure(item.Key, item.Value));
    }

    private static void AddDuplicateIdentityFailures(
        IReadOnlyList<SnipeAssetImportRecord> records,
        Func<SnipeAssetImportRecord, string?> selector,
        string identityName,
        IDictionary<SnipeAssetImportRecord, List<BatchIdentityConflict>> conflicts)
    {
        foreach (var group in records
            .Select(record => (Value: selector(record), Record: record))
            .Where(item => item.Value is not null)
            .GroupBy(item => item.Value!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Record).Distinct().Count() > 1))
        {
            var conflictingRecords = group
                .Select(item => item.Record)
                .Distinct()
                .ToList();
            foreach (var record in conflictingRecords)
            {
                AddIdentityConflict(
                    conflicts,
                    record,
                    identityName,
                    group.Key,
                    conflictingRecords.Where(candidate => !ReferenceEquals(candidate, record)));
            }
        }
    }

    private static void AddIdentityConflict(
        IDictionary<SnipeAssetImportRecord, List<BatchIdentityConflict>> conflicts,
        SnipeAssetImportRecord record,
        string identityName,
        string identityValue,
        IEnumerable<SnipeAssetImportRecord> conflictingRecords)
    {
        if (!conflicts.TryGetValue(record, out var recordConflicts))
        {
            recordConflicts = [];
            conflicts[record] = recordConflicts;
        }

        recordConflicts.Add(new BatchIdentityConflict(
            identityName,
            identityValue,
            conflictingRecords.Distinct().ToList()));
    }

    private static BatchIdentityFailure BuildBatchIdentityFailure(
        SnipeAssetImportRecord record,
        IReadOnlyList<BatchIdentityConflict> conflicts)
    {
        var fields = conflicts
            .Select(conflict => conflict.Field)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var conflictingValue = string.Join(
            "; ",
            conflicts
                .Select(conflict => $"{conflict.Field}={conflict.Value}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase));
        var conflictingAssets = string.Join(
            "; ",
            conflicts
                .SelectMany(conflict => conflict.ConflictingRecords)
                .Distinct()
                .OrderBy(candidate => candidate.AssetTag, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.SourceId, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => $"AssetTag={candidate.AssetTag} | Name={candidate.Name} | SourceId={candidate.SourceId}"));

        return new BatchIdentityFailure(
            $"Asset '{record.AssetTag}' shares batch identity fields ({string.Join(", ", fields)}) with another source record.",
            string.Join("; ", fields),
            conflictingValue,
            conflictingAssets);
    }

    private static string FormatMacAddresses(SnipeAssetImportRecord record)
    {
        return string.Join(
            "; ",
            record.MacAddresses
                .Select(MacAddressNormalizer.NormalizeDisplay)
                .Where(macAddress => macAddress is not null)
                .Select(macAddress => macAddress!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reserves each existing target asset for exactly one source record and blocks every colliding plan.
    /// </summary>
    private static void BlockDuplicateTargetReservations(
        List<PlannedRecord> plannedRecords,
        ImportRunContext context)
    {
        var conflictingPlans = plannedRecords
            .Where(plan => plan.ExistingAsset is not null)
            .GroupBy(plan => plan.ExistingAsset!.Id)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();
        if (conflictingPlans.Count == 0)
        {
            return;
        }

        foreach (var plan in conflictingPlans)
        {
            context.AddFailure(
                plan.Record,
                "SnipeImport.DuplicateTargetReservation",
                $"Multiple source records matched Snipe-IT asset id {plan.ExistingAsset!.Id}; all conflicting writes were blocked.");
        }

        plannedRecords.RemoveAll(conflictingPlans.Contains);
    }

    private static void ValidateOptions(SnipeImportOptions options)
    {
        _ = ApiEndpointValidator.ValidateSnipeBaseUri(options.BaseUrl);

        if (string.IsNullOrWhiteSpace(options.ApiToken))
        {
            throw new ArgumentException("Snipe-IT API token is required.", nameof(options));
        }

        if (options.NameMatchThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Name match threshold must be greater than 0 and less than or equal to 1.");
        }

        if (options.MaxReadRetryAttempts < 0 || options.MaxReadRetryAttempts > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Read retry attempts must be between 0 and 10.");
        }

        if (options.RetryBaseDelay < TimeSpan.Zero || options.RetryBaseDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry base delay must be between zero and one minute.");
        }

        if (Normalize(options.MacAddressCustomFieldDbColumnName) is { } customField
            && !CustomFieldDbColumnPattern.IsMatch(customField))
        {
            throw new ArgumentException("MAC address custom field must be a Snipe-IT db column beginning with _snipeit_.", nameof(options));
        }

        if (Normalize(options.MacAddressCustomFieldDbColumnName) is not null
            && Normalize(options.MacAddressFieldsetName) is not null
            && Normalize(options.ModelCategoryNormalizationTargetName) is null)
        {
            throw new ArgumentException(
                "Model category normalization target name is required when MAC Fieldset reconciliation is enabled.",
                nameof(options));
        }

        foreach (var ignoredMacAddress in options.IgnoredMacAddresses)
        {
            if (MacAddressNormalizer.NormalizeComparable(ignoredMacAddress) is null)
            {
                throw new ArgumentException($"Ignored MAC address '{ignoredMacAddress}' is invalid.", nameof(options));
            }
        }

        if (options.ManualPreflightCsvEnabled && string.IsNullOrWhiteSpace(options.ManualPreflightCsvDirectory))
        {
            throw new ArgumentException("Manual preflight CSV directory is required when manual preflight CSV is enabled.", nameof(options));
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Decodes the single HTML-escaping layer documented for Snipe-IT API text before normalization and matching.
    /// </summary>
    private static string? NormalizeApiText(string? value)
    {
        return Normalize(WebUtility.HtmlDecode(value));
    }

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private static bool NamesEqual(string expected, string? actual)
    {
        return Normalize(actual) is { } normalizedActual
            && string.Equals(NormalizeKey(expected), NormalizeKey(normalizedActual), StringComparison.Ordinal);
    }

    private sealed record SnipeEntity(int Id, string Name);

    /// <summary>
    /// Describes one duplicated source identity value and the other batch records that share it.
    /// </summary>
    private sealed record BatchIdentityConflict(
        string Field,
        string Value,
        IReadOnlyList<SnipeAssetImportRecord> ConflictingRecords);

    /// <summary>
    /// Carries the operator-readable message and structured CSV evidence for one blocked source record.
    /// </summary>
    private sealed record BatchIdentityFailure(
        string Message,
        string ConflictingFields,
        string ConflictingValue,
        string ConflictingAssets);

    /// <summary>
    /// Represents the model identity fields required to build the run-local model lookup index.
    /// </summary>
    private sealed record SnipeModel(
        int Id,
        string Name,
        string? ModelNumber,
        int CategoryId,
        string? CategoryName,
        int? ManufacturerId,
        string? ManufacturerName,
        int? FieldsetId,
        string? FieldsetName);

    /// <summary>
    /// Represents one Snipe-IT custom Fieldset and the DB columns it makes available to associated models.
    /// </summary>
    private sealed record SnipeFieldset(
        int Id,
        string Name,
        IReadOnlySet<string> FieldDbColumns);

    /// <summary>
    /// Holds one run's resolved or blocked shared Snipe-IT references before asset matching begins.
    /// </summary>
    private sealed class ReferencePlans
    {
        public Dictionary<string, ReferencePlanResult<CompanyPlan>> Companies { get; } = [];
        public Dictionary<string, ReferencePlanResult<CategoryPlan>> Categories { get; } = [];
        public Dictionary<string, ReferencePlanResult<ManufacturerPlan>> Manufacturers { get; } = [];
        public Dictionary<string, ReferencePlanResult<ModelPlan>> Models { get; } = [];
    }

    /// <summary>
    /// Captures either a planned reference value or the failure that should be applied to dependent assets.
    /// </summary>
    private sealed class ReferencePlanResult<T>
    {
        private ReferencePlanResult(T? value, string? failureCode, string? failureMessage)
        {
            Value = value;
            FailureCode = failureCode;
            FailureMessage = failureMessage;
        }

        public T? Value { get; }
        public string? FailureCode { get; }
        public string? FailureMessage { get; }
        public bool Success => FailureCode is null;

        public static ReferencePlanResult<T> FromValue(T value)
        {
            return new ReferencePlanResult<T>(value, null, null);
        }

        public static ReferencePlanResult<T> FromFailure(string failureCode, string failureMessage)
        {
            return new ReferencePlanResult<T>(default, failureCode, failureMessage);
        }
    }

    private sealed record CompanyPlan(int? CompanyId, PlannedCompanyCreate? CompanyCreate);

    private sealed record CategoryPlan(int? CategoryId, PlannedCategoryCreate? CategoryCreate);

    private sealed record ManufacturerPlan(string ManufacturerName, int? ManufacturerId);

    private sealed record ModelPlan(int? ModelId, PlannedModelCreate? ModelCreate);

    private sealed record PlannedRecord(
        SnipeAssetImportRecord Record,
        int? CompanyId,
        PlannedCompanyCreate? CompanyCreate,
        int? ManufacturerId,
        int? ModelId,
        PlannedModelCreate? ModelCreate,
        SnipeAssetMatch? ExistingAsset);

    /// <summary>
    /// Holds the normalized company-name index built from the complete company snapshot for one import run.
    /// </summary>
    private sealed class SnipeCompanyLookup
    {
        private readonly IReadOnlyDictionary<string, SnipeEntity> _companiesByName;

        private SnipeCompanyLookup(IReadOnlyDictionary<string, SnipeEntity> companiesByName)
        {
            _companiesByName = companiesByName;
        }

        public static SnipeCompanyLookup Create(IEnumerable<SnipeEntity> companies)
        {
            var companiesByName = new Dictionary<string, SnipeEntity>(StringComparer.Ordinal);
            foreach (var company in companies)
            {
                var key = NormalizeKey(company.Name);
                if (companiesByName.TryGetValue(key, out var existing) && existing.Id != company.Id)
                {
                    throw new SnipeApiException(
                        "SnipeImport.AmbiguousCompanyMatch",
                        $"Multiple Snipe-IT companies normalize to the name '{company.Name}'.");
                }

                companiesByName[key] = company;
            }

            return new SnipeCompanyLookup(companiesByName);
        }

        public SnipeEntity? Find(string companyName)
        {
            return _companiesByName.TryGetValue(NormalizeKey(companyName), out var company)
                ? company
                : null;
        }
    }

    /// <summary>
    /// Resolves models against Snipe-IT's global name/model-number uniqueness while validating the requested category.
    /// </summary>
    private sealed class SnipeModelLookup
    {
        private readonly IReadOnlyList<SnipeModel> _models;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<SnipeModel>> _modelsByName;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<SnipeModel>> _modelsByModelNumber;

        private SnipeModelLookup(
            IReadOnlyList<SnipeModel> models,
            IReadOnlyDictionary<string, IReadOnlyList<SnipeModel>> modelsByName,
            IReadOnlyDictionary<string, IReadOnlyList<SnipeModel>> modelsByModelNumber)
        {
            _models = models;
            _modelsByName = modelsByName;
            _modelsByModelNumber = modelsByModelNumber;
        }

        public static SnipeModelLookup Empty { get; } = new(
            [],
            new Dictionary<string, IReadOnlyList<SnipeModel>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<SnipeModel>>(StringComparer.Ordinal));

        public IReadOnlyList<SnipeModel> Models => _models;

        public static SnipeModelLookup Create(IEnumerable<SnipeModel> models)
        {
            var distinctModels = models
                .GroupBy(model => model.Id)
                .Select(group => group.First())
                .ToList();
            return new SnipeModelLookup(
                distinctModels,
                BuildIndex(distinctModels, model => model.Name),
                BuildIndex(distinctModels, model => model.ModelNumber));
        }

        /// <summary>
        /// Resolves by exact name first, then reuses one exact model-number match only when manufacturer and category ids also match; ambiguous or incompatible matches fail closed.
        /// </summary>
        public SnipeEntity? Find(
            SnipeAssetImportRecord record,
            CategoryPlan categoryPlan,
            int? manufacturerId,
            ImportRunContext context)
        {
            var key = NormalizeKey(record.ModelName);
            if (_modelsByName.TryGetValue(key, out var nameMatches))
            {
                if (nameMatches.Count != 1)
                {
                    var ids = string.Join(", ", nameMatches.Select(model => model.Id).OrderBy(id => id));
                    throw new SnipeApiException(
                        "SnipeImport.AmbiguousModelMatch",
                        $"Multiple Snipe-IT models named '{record.ModelName}' exist with ids {ids}; no model was selected.");
                }

                var model = nameMatches[0];
                if (categoryPlan.CategoryId != model.CategoryId)
                {
                    if (context.TryGetPlannedModelUpdate(model.Id, out var update)
                        && update is not null
                        && update.ChangeCategory
                        && update.TargetCategoryMatches(categoryPlan))
                    {
                        return new SnipeEntity(model.Id, model.Name);
                    }

                    var existingCategory = model.CategoryName is null
                        ? $"category id {model.CategoryId}"
                        : $"category '{model.CategoryName}' (id {model.CategoryId})";
                    var requestedCategory = categoryPlan.CategoryId is { } requestedCategoryId
                        ? $"category '{record.CategoryName}' (id {requestedCategoryId})"
                        : $"category '{record.CategoryName}' (planned creation)";
                    var deviceType = Normalize(record.DeviceType) ?? "<missing>";
                    throw new SnipeApiException(
                        "SnipeImport.ModelCategoryMismatch",
                        $"Model '{record.ModelName}' exists as Snipe-IT model id {model.Id} in {existingCategory}, but source device type '{deviceType}' requires {requestedCategory}. Existing models are not recategorized automatically.");
                }

                return new SnipeEntity(model.Id, model.Name);
            }

            if (_modelsByModelNumber.TryGetValue(key, out var numberMatches))
            {
                var orderedMatches = numberMatches.OrderBy(model => model.Id).ToList();
                var conflict = orderedMatches[0];
                var additionalCount = orderedMatches.Count - 1;
                if (additionalCount > 0)
                {
                    throw new SnipeApiException(
                        "SnipeImport.ModelNameConflict",
                        $"Model name '{record.ModelName}' matches model number '{conflict.ModelNumber}' on multiple Snipe-IT models; first match is '{conflict.Name}' (id {conflict.Id}) with {additionalCount} additional model(s). No model was selected.");
                }

                var manufacturerMatches = manufacturerId is not null
                    && conflict.ManufacturerId == manufacturerId;
                var categoryMatches = categoryPlan.CategoryId is not null
                    && conflict.CategoryId == categoryPlan.CategoryId;
                if (!categoryMatches
                    && context.TryGetPlannedModelUpdate(conflict.Id, out var plannedUpdate)
                    && plannedUpdate is not null
                    && plannedUpdate.ChangeCategory
                    && plannedUpdate.TargetCategoryMatches(categoryPlan))
                {
                    categoryMatches = true;
                }

                if (manufacturerMatches && categoryMatches)
                {
                    return new SnipeEntity(conflict.Id, conflict.Name);
                }

                var requestedManufacturer = manufacturerId is { } requestedManufacturerId
                    ? $"manufacturer '{record.ManufacturerName}' (id {requestedManufacturerId})"
                    : $"unresolved manufacturer '{record.ManufacturerName}'";
                var existingManufacturer = conflict.ManufacturerId is { } existingManufacturerId
                    ? $"manufacturer '{conflict.ManufacturerName ?? "<missing>"}' (id {existingManufacturerId})"
                    : "no manufacturer";
                var requestedCategory = categoryPlan.CategoryId is { } requestedCategoryId
                    ? $"category '{record.CategoryName}' (id {requestedCategoryId})"
                    : $"category '{record.CategoryName}' (planned creation)";
                var existingCategory = conflict.CategoryName is { } existingCategoryName
                    ? $"category '{existingCategoryName}' (id {conflict.CategoryId})"
                    : $"category id {conflict.CategoryId}";
                throw new SnipeApiException(
                    "SnipeImport.ModelNameConflict",
                    $"Model name '{record.ModelName}' matches model number '{conflict.ModelNumber}' on Snipe-IT model '{conflict.Name}' (id {conflict.Id}), but requested {requestedManufacturer} and {requestedCategory} do not both match existing {existingManufacturer} and {existingCategory}. No model was selected.");
            }

            return null;
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<SnipeModel>> BuildIndex(
            IEnumerable<SnipeModel> models,
            Func<SnipeModel, string?> valueSelector)
        {
            return models
                .Select(model => (Model: model, Value: Normalize(valueSelector(model))))
                .Where(item => item.Value is not null)
                .GroupBy(item => NormalizeKey(item.Value!), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<SnipeModel>)group.Select(item => item.Model).ToList(),
                    StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Holds in-memory lookup indexes built from one paginated Snipe-IT hardware snapshot for the current import run.
    /// </summary>
    private sealed class SnipeHardwareLookup
    {
        private readonly Dictionary<string, List<SnipeAssetMatch>> _assetsByAssetTag;
        private readonly Dictionary<string, List<SnipeAssetMatch>> _assetsByMac;
        private readonly Dictionary<string, List<SnipeAssetMatch>> _assetsBySerial;
        private readonly Dictionary<string, List<SnipeAssetMatch>> _assetsByReferenceContext;

        private SnipeHardwareLookup(
            IReadOnlyList<SnipeAssetMatch> assets,
            Dictionary<string, List<SnipeAssetMatch>> assetsByAssetTag,
            Dictionary<string, List<SnipeAssetMatch>> assetsByMac,
            Dictionary<string, List<SnipeAssetMatch>> assetsBySerial,
            Dictionary<string, List<SnipeAssetMatch>> assetsByReferenceContext)
        {
            Assets = assets;
            _assetsByAssetTag = assetsByAssetTag;
            _assetsByMac = assetsByMac;
            _assetsBySerial = assetsBySerial;
            _assetsByReferenceContext = assetsByReferenceContext;
        }

        public static SnipeHardwareLookup Empty { get; } = new(
            [],
            new Dictionary<string, List<SnipeAssetMatch>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<SnipeAssetMatch>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<SnipeAssetMatch>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<SnipeAssetMatch>>(StringComparer.Ordinal));

        public IReadOnlyList<SnipeAssetMatch> Assets { get; }

        public static SnipeHardwareLookup Create(
            IReadOnlyList<SnipeAssetMatch> assets,
            string? macAddressCustomFieldDbColumnName,
            IReadOnlySet<string> ignoredMacAddresses)
        {
            var macFieldName = Normalize(macAddressCustomFieldDbColumnName);
            var assetsByAssetTag = new Dictionary<string, List<SnipeAssetMatch>>(StringComparer.OrdinalIgnoreCase);
            var assetsByMac = new Dictionary<string, List<SnipeAssetMatch>>(StringComparer.OrdinalIgnoreCase);
            var assetsBySerial = new Dictionary<string, List<SnipeAssetMatch>>(StringComparer.OrdinalIgnoreCase);
            var assetsByReferenceContext = new Dictionary<string, List<SnipeAssetMatch>>(StringComparer.Ordinal);

            foreach (var asset in assets)
            {
                if (Normalize(asset.AssetTag) is { } assetTag)
                {
                    AddLookup(assetsByAssetTag, NormalizeKey(assetTag), asset);
                }

                if (HardwareIdentityNormalizer.NormalizeSerial(asset.Serial) is { } serial)
                {
                    AddLookup(assetsBySerial, NormalizeKey(serial), asset);
                }

                if (macFieldName is not null
                    && asset.CustomFields.TryGetValue(macFieldName, out var macAddress)
                    && MacAddressNormalizer.NormalizeComparable(macAddress) is { } comparableMac
                    && !ignoredMacAddresses.Contains(comparableMac))
                {
                    AddLookup(assetsByMac, comparableMac, asset);
                }

                if (BuildReferenceContextKey(asset.CompanyName, asset.CategoryName, asset.ModelName) is { } referenceKey)
                {
                    AddLookup(assetsByReferenceContext, referenceKey, asset);
                }
            }

            return new SnipeHardwareLookup(assets, assetsByAssetTag, assetsByMac, assetsBySerial, assetsByReferenceContext);
        }

        public IReadOnlyList<SnipeAssetMatch> FindByAssetTag(string assetTag)
        {
            return Normalize(assetTag) is { } normalizedAssetTag
                && _assetsByAssetTag.TryGetValue(NormalizeKey(normalizedAssetTag), out var assets)
                    ? assets
                    : [];
        }

        public IReadOnlyList<SnipeAssetMatch> FindByMac(string macAddress)
        {
            return MacAddressNormalizer.NormalizeComparable(macAddress) is { } comparableMac
                && _assetsByMac.TryGetValue(comparableMac, out var assets)
                    ? assets
                    : [];
        }

        public IReadOnlyList<SnipeAssetMatch> FindBySerial(string serial)
        {
            return Normalize(serial) is { } normalizedSerial
                && _assetsBySerial.TryGetValue(NormalizeKey(normalizedSerial), out var assets)
                    ? assets
                    : [];
        }

        public IReadOnlyList<SnipeAssetMatch> FindByReferenceContext(SnipeAssetImportRecord record)
        {
            return BuildReferenceContextKey(record.CompanyName, record.CategoryName, record.ModelName) is { } key
                && _assetsByReferenceContext.TryGetValue(key, out var assets)
                    ? assets
                    : [];
        }

        private static string? BuildReferenceContextKey(string? company, string? category, string? model)
        {
            return Normalize(company) is { } normalizedCompany
                && Normalize(category) is { } normalizedCategory
                && Normalize(model) is { } normalizedModel
                    ? $"{NormalizeKey(normalizedCompany)}|{NormalizeKey(normalizedCategory)}|{NormalizeKey(normalizedModel)}"
                    : null;
        }

        private static void AddLookup(
            Dictionary<string, List<SnipeAssetMatch>> lookup,
            string key,
            SnipeAssetMatch asset)
        {
            if (!lookup.TryGetValue(key, out var assets))
            {
                assets = [];
                lookup[key] = assets;
            }

            assets.Add(asset);
        }
    }

    private sealed class PlannedCompanyCreate
    {
        public PlannedCompanyCreate(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public int? CreatedId { get; set; }
    }

    private sealed class PlannedCategoryCreate
    {
        public PlannedCategoryCreate(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public int? CreatedId { get; set; }
    }

    private sealed class PlannedModelCreate
    {
        public PlannedModelCreate(
            string name,
            string categoryName,
            int? categoryId,
            PlannedCategoryCreate? categoryCreate,
            string manufacturerName,
            int? manufacturerId,
            int? fieldsetId,
            string? fieldsetName)
        {
            Name = name;
            CategoryName = categoryName;
            CategoryId = categoryId;
            CategoryCreate = categoryCreate;
            ManufacturerName = manufacturerName;
            ManufacturerId = manufacturerId;
            FieldsetId = fieldsetId;
            FieldsetName = fieldsetName;
        }

        public string Name { get; }
        public string CategoryName { get; }
        public int? CategoryId { get; }
        public PlannedCategoryCreate? CategoryCreate { get; }
        public string ManufacturerName { get; }
        public int? ManufacturerId { get; }
        public int? FieldsetId { get; }
        public string? FieldsetName { get; }
        public int? CreatedId { get; set; }
    }

    /// <summary>
    /// Combines all category and Fieldset changes required for one existing Snipe-IT model into a single PATCH.
    /// </summary>
    private sealed class PlannedModelUpdate
    {
        public PlannedModelUpdate(
            SnipeModel model,
            int? targetCategoryId,
            string targetCategoryName,
            PlannedCategoryCreate? targetCategoryCreate,
            int? targetFieldsetId,
            string? targetFieldsetName,
            bool changeCategory,
            bool changeFieldset)
        {
            ModelId = model.Id;
            ModelName = model.Name;
            CurrentCategoryId = model.CategoryId;
            CurrentCategoryName = model.CategoryName;
            TargetCategoryId = targetCategoryId;
            TargetCategoryName = targetCategoryName;
            TargetCategoryCreate = targetCategoryCreate;
            CurrentFieldsetId = model.FieldsetId;
            CurrentFieldsetName = model.FieldsetName;
            TargetFieldsetId = targetFieldsetId;
            TargetFieldsetName = targetFieldsetName;
            ChangeCategory = changeCategory;
            ChangeFieldset = changeFieldset;
        }

        public int ModelId { get; }
        public string ModelName { get; }
        public int CurrentCategoryId { get; }
        public string? CurrentCategoryName { get; }
        public int? TargetCategoryId { get; }
        public string TargetCategoryName { get; }
        public PlannedCategoryCreate? TargetCategoryCreate { get; }
        public int? CurrentFieldsetId { get; }
        public string? CurrentFieldsetName { get; }
        public int? TargetFieldsetId { get; }
        public string? TargetFieldsetName { get; }
        public bool ChangeCategory { get; }
        public bool ChangeFieldset { get; }

        public bool TargetCategoryMatches(CategoryPlan categoryPlan)
        {
            return TargetCategoryCreate is not null
                ? ReferenceEquals(TargetCategoryCreate, categoryPlan.CategoryCreate)
                : TargetCategoryId == categoryPlan.CategoryId;
        }
    }

    private sealed class ImportRunContext
    {
        private readonly Dictionary<string, PlannedCompanyCreate> _plannedCompanies = [];
        private readonly Dictionary<string, PlannedCategoryCreate> _plannedCategories = [];
        private readonly Dictionary<string, PlannedModelCreate> _plannedModels = [];
        private readonly Dictionary<int, PlannedModelUpdate> _plannedModelUpdates = [];
        private readonly Dictionary<string, SnipeEntity?> _entityLookupCache = [];
        private bool _macMatchingDisabledWarningAdded;

        public ImportRunContext(SnipeImportOptions options, List<ModuleWarning> warnings)
        {
            Options = options;
            Warnings = warnings;
            IgnoredMacAddresses = options.IgnoredMacAddresses
                .Select(MacAddressNormalizer.NormalizeComparable)
                .Where(value => value is not null)
                .Select(value => value!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public SnipeImportOptions Options { get; }
        public IReadOnlySet<string> IgnoredMacAddresses { get; }
        public List<ImportAction> Actions { get; } = [];
        public List<ImportFailure> Failures { get; } = [];
        public List<SnipeAssetPreflightRow> FailedPreflightAssets { get; } = [];
        public List<ModuleWarning> Warnings { get; }
        public IEnumerable<PlannedCompanyCreate> PlannedCompanies => _plannedCompanies.Values;
        public IEnumerable<PlannedCategoryCreate> PlannedCategories => _plannedCategories.Values;
        public IEnumerable<PlannedModelCreate> PlannedModels => _plannedModels.Values;
        public IEnumerable<PlannedModelUpdate> PlannedModelUpdates => _plannedModelUpdates.Values;
        public int CreatedAssets { get; set; }
        public int UpdatedAssets { get; set; }
        public int SkippedAssets { get; set; }
        public int FailedAssets { get; set; }
        public int CreatedCompanies { get; set; }
        public int CreatedCategories { get; set; }
        public int CreatedModels { get; set; }
        public int UpdatedModels { get; set; }
        public int ExecutedWriteCount { get; private set; }
        public bool HasExecutedWrites => ExecutedWriteCount > 0;

        public bool TryGetEntityLookup(string cacheKey, out SnipeEntity? entity)
        {
            return _entityLookupCache.TryGetValue(cacheKey, out entity);
        }

        public void CacheEntityLookup(string cacheKey, SnipeEntity? entity)
        {
            _entityLookupCache[cacheKey] = entity;
        }

        public bool TryGetPlannedCompany(string companyName, out PlannedCompanyCreate? company)
        {
            return _plannedCompanies.TryGetValue(NormalizeKey(companyName), out company);
        }

        public PlannedCompanyCreate GetOrAddPlannedCompany(string companyName)
        {
            var key = NormalizeKey(companyName);
            if (_plannedCompanies.TryGetValue(key, out var plannedCompany))
            {
                return plannedCompany;
            }

            plannedCompany = new PlannedCompanyCreate(companyName);
            _plannedCompanies[key] = plannedCompany;
            return plannedCompany;
        }

        public bool TryGetPlannedCategory(string categoryName, out PlannedCategoryCreate? category)
        {
            return _plannedCategories.TryGetValue(NormalizeKey(categoryName), out category);
        }

        public PlannedCategoryCreate GetOrAddPlannedCategory(string categoryName)
        {
            var key = NormalizeKey(categoryName);
            if (_plannedCategories.TryGetValue(key, out var plannedCategory))
            {
                return plannedCategory;
            }

            plannedCategory = new PlannedCategoryCreate(categoryName);
            _plannedCategories[key] = plannedCategory;
            return plannedCategory;
        }

        public bool TryGetPlannedModel(string modelKey, out PlannedModelCreate? model)
        {
            return _plannedModels.TryGetValue(modelKey, out model);
        }

        public PlannedModelCreate GetOrAddPlannedModel(
            string modelKey,
            string modelName,
            string categoryName,
            int? categoryId,
            PlannedCategoryCreate? categoryCreate,
            string manufacturerName,
            int? manufacturerId,
            int? fieldsetId,
            string? fieldsetName)
        {
            if (_plannedModels.TryGetValue(modelKey, out var plannedModel))
            {
                return plannedModel;
            }

            plannedModel = new PlannedModelCreate(
                modelName,
                categoryName,
                categoryId,
                categoryCreate,
                manufacturerName,
                manufacturerId,
                fieldsetId,
                fieldsetName);
            _plannedModels[modelKey] = plannedModel;
            return plannedModel;
        }

        public bool TryGetPlannedModelUpdate(int modelId, out PlannedModelUpdate? update)
        {
            return _plannedModelUpdates.TryGetValue(modelId, out update);
        }

        public void AddPlannedModelUpdate(PlannedModelUpdate update)
        {
            _plannedModelUpdates.TryAdd(update.ModelId, update);
        }

        /// <summary>
        /// Removes reference creates that are no longer reachable after record-level planning failures or target-reservation blocks.
        /// </summary>
        public void RetainReferencesFor(IReadOnlyList<PlannedRecord> plannedRecords)
        {
            var retainedCompanies = plannedRecords
                .Select(record => record.CompanyCreate)
                .Where(company => company is not null)
                .ToHashSet();
            var retainedModels = plannedRecords
                .Select(record => record.ModelCreate)
                .Where(model => model is not null)
                .ToHashSet();
            var retainedCategories = retainedModels
                .Select(model => model!.CategoryCreate)
                .Concat(_plannedModelUpdates.Values.Select(update => update.TargetCategoryCreate))
                .Where(category => category is not null)
                .ToHashSet();

            RemoveUnretained(_plannedCompanies, retainedCompanies);
            RemoveUnretained(_plannedCategories, retainedCategories);
            RemoveUnretained(_plannedModels, retainedModels);
        }

        private static void RemoveUnretained<T>(Dictionary<string, T> references, IReadOnlySet<T?> retained)
            where T : class
        {
            foreach (var key in references
                         .Where(item => !retained.Contains(item.Value))
                         .Select(item => item.Key)
                         .ToList())
            {
                references.Remove(key);
            }
        }

        public void AddPlannedAction(string actionType, string targetType, string targetName, string message)
        {
            Actions.Add(new ImportAction
            {
                ActionType = actionType,
                TargetType = targetType,
                TargetName = targetName,
                WasExecuted = false,
                Message = message
            });
        }

        public void AddExecutedAction(string actionType, string targetType, string targetName, string message)
        {
            Actions.Add(new ImportAction
            {
                ActionType = actionType,
                TargetType = targetType,
                TargetName = targetName,
                WasExecuted = true,
                Message = message
            });
            ExecutedWriteCount++;
        }

        public void AddFailure(
            SnipeAssetImportRecord record,
            string code,
            string message,
            string? conflictingFields = null,
            string? conflictingValue = null,
            string? conflictingAssets = null)
        {
            FailedAssets++;
            FailedPreflightAssets.Add(new SnipeAssetPreflightRow(
                "Blocked",
                record.AssetTag,
                record.Name,
                HardwareIdentityNormalizer.NormalizeSerial(record.Serial),
                FormatMacAddresses(record),
                record.CompanyName,
                record.ModelName,
                record.CategoryName,
                record.ManufacturerName,
                ExistingAssetId: null,
                ExistingAssetTag: null,
                ConflictingFields: conflictingFields,
                ConflictingValue: conflictingValue,
                ConflictingAssets: conflictingAssets,
                FailureCode: code,
                FailureMessage: message,
                DeviceType: record.DeviceType));
            Failures.Add(new ImportFailure
            {
                TargetType = "Asset",
                TargetName = Normalize(record.AssetTag) ?? Normalize(record.Name) ?? "<unknown>",
                Code = code,
                Message = message
            });
        }

        public void AddPreflightFailure(string code, string message)
        {
            Failures.Add(new ImportFailure
            {
                TargetType = "PreflightCsv",
                TargetName = Options.ManualPreflightCsvDirectory ?? "<unknown>",
                Code = code,
                Message = message
            });
        }

        public void AddCancellationFailure()
        {
            Failures.Add(new ImportFailure
            {
                TargetType = "ImportRun",
                TargetName = "Snipe-IT",
                Code = "SnipeImport.CancelledAfterPartialExecution",
                Message = $"Import was cancelled after {ExecutedWriteCount} successful write(s); completed actions are retained for audit."
            });
        }

        public void AddWarning(string code, string message)
        {
            Warnings.Add(new ModuleWarning
            {
                Source = "SnipeImport",
                Code = code,
                Message = message
            });
        }

        public void AddMacMatchingDisabledWarning()
        {
            if (_macMatchingDisabledWarningAdded)
            {
                return;
            }

            _macMatchingDisabledWarningAdded = true;
            AddWarning(
                "SnipeImport.MacMatchingDisabled",
                "MAC matching is disabled because MacAddressCustomFieldDbColumnName is not configured.");
        }

        public SnipeImportResult ToResult(bool cancelled = false)
        {
            return new SnipeImportResult
            {
                CreatedAssets = CreatedAssets,
                UpdatedAssets = UpdatedAssets,
                SkippedAssets = SkippedAssets,
                FailedAssets = FailedAssets,
                CreatedCompanies = CreatedCompanies,
                CreatedCategories = CreatedCategories,
                CreatedModels = CreatedModels,
                UpdatedModels = UpdatedModels,
                DryRun = Options.DryRun,
                Cancelled = cancelled,
                Actions = Actions,
                Failures = Failures,
                Warnings = Warnings
            };
        }
    }
}
