using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.Common;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Imports mapped assets into Snipe-IT by resolving dependencies, planning writes, and issuing mutations only after preflight gates pass.
/// </summary>
public sealed class SnipeImporter : ISnipeImporter
{
    private const int LookupLimit = 50;
    private const string AssetCategoryType = "asset";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        var context = new ImportRunContext(options, batch.Warnings.ToList());
        var plannedRecords = new List<PlannedRecord>();
        var validRecords = new List<SnipeAssetImportRecord>();
        _logger.LogInformation("Starting Snipe-IT import for {AssetCount} asset(s).", batch.Assets.Count);
        ReportProgress(progress, $"Starting Snipe-IT planning for {batch.Assets.Count} asset(s).", current: 0, total: batch.Assets.Count);

        for (var index = 0; index < batch.Assets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = batch.Assets[index];
            var current = index + 1;
            ReportProgress(progress, $"Validating Snipe-IT asset {current}/{batch.Assets.Count}: {record.Name}.", current - 1, batch.Assets.Count);

            try
            {
                ValidateRecord(record);
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

            ReportProgress(progress, $"Matching Snipe-IT asset {current}/{validRecords.Count}: {record.Name}.", current - 1, validRecords.Count);

            try
            {
                var existingAsset = await FindExistingAssetAsync(record, context, cancellationToken).ConfigureAwait(false);
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

        _logger.LogInformation(
            "Completed Snipe-IT import. Created={CreatedAssets}, Updated={UpdatedAssets}, Failed={FailedAssets}.",
            context.CreatedAssets,
            context.UpdatedAssets,
            context.FailedAssets);

        ReportProgress(progress, "Completed Snipe-IT import.", plannedRecords.Count, plannedRecords.Count);
        return context.ToResult();
    }

    /// <summary>
    /// Plans shared reference dependencies before asset matching so missing references are discovered once per unique name.
    /// </summary>
    private async Task<ReferencePlans> PlanReferencesAsync(
        IReadOnlyList<SnipeAssetImportRecord> records,
        ImportRunContext context,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var referencePlans = new ReferencePlans();
        if (records.Count == 0)
        {
            return referencePlans;
        }

        var companyGroups = GroupRecordsByReference(records, record => record.CompanyName);
        var plannedCompanyCount = 0;
        foreach (var group in companyGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            plannedCompanyCount++;
            var companyName = group.Value[0].CompanyName;
            ReportProgress(progress, $"Planning Snipe-IT company references {plannedCompanyCount}/{companyGroups.Count}: {companyName}.", plannedCompanyCount - 1, companyGroups.Count);
            referencePlans.Companies[group.Key] = await PlanReferenceAsync(() => PlanCompanyAsync(companyName, context, cancellationToken)).ConfigureAwait(false);
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

        var categoryReadyRecords = companyReadyRecords
            .Where(record => referencePlans.Categories[BuildReferenceKey(record.CategoryName)].Success)
            .ToList();
        var manufacturerGroups = GroupRecordsByReference(categoryReadyRecords, record => record.ManufacturerName);
        var plannedManufacturerCount = 0;
        foreach (var group in manufacturerGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            plannedManufacturerCount++;
            var manufacturerName = group.Value[0].ManufacturerName;
            ReportProgress(progress, $"Planning Snipe-IT manufacturer references {plannedManufacturerCount}/{manufacturerGroups.Count}: {manufacturerName}.", plannedManufacturerCount - 1, manufacturerGroups.Count);
            referencePlans.Manufacturers[group.Key] = await PlanReferenceAsync(async () =>
                new ManufacturerPlan(await FindManufacturerAsync(manufacturerName, context, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        }

        var manufacturerReadyRecords = categoryReadyRecords
            .Where(record => referencePlans.Manufacturers[BuildReferenceKey(record.ManufacturerName)].Success)
            .ToList();
        var modelRecords = new Dictionary<string, SnipeAssetImportRecord>();
        foreach (var record in manufacturerReadyRecords)
        {
            var categoryPlan = referencePlans.Categories[BuildReferenceKey(record.CategoryName)].Value!;
            var manufacturerPlan = referencePlans.Manufacturers[BuildReferenceKey(record.ManufacturerName)].Value!;
            var modelKey = BuildModelReferenceKey(record.ModelName, categoryPlan, manufacturerPlan.ManufacturerId);
            modelRecords.TryAdd(modelKey, record);
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
            referencePlans.Models[modelRecord.Key] = await PlanReferenceAsync(() =>
                PlanModelAsync(record, categoryPlan, manufacturerPlan.ManufacturerId, context, cancellationToken)).ConfigureAwait(false);
        }

        return referencePlans;
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

    private async Task<CompanyPlan> PlanCompanyAsync(
        string companyName,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        if (context.TryGetPlannedCompany(companyName, out var existingPlan))
        {
            return new CompanyPlan(null, existingPlan);
        }

        var company = await FindEntityByNameAsync(
            "companies",
            companyName,
            new Dictionary<string, string> { ["name"] = companyName },
            context,
            cancellationToken).ConfigureAwait(false);

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

    private async Task<int?> FindManufacturerAsync(
        string manufacturerName,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var manufacturer = await FindEntityByNameAsync(
            "manufacturers",
            manufacturerName,
            new Dictionary<string, string> { ["name"] = manufacturerName },
            context,
            cancellationToken).ConfigureAwait(false);

        if (manufacturer is not null)
        {
            return manufacturer.Id;
        }

        context.AddWarning("SnipeImport.ManufacturerMissing", $"Manufacturer '{manufacturerName}' does not exist; model will be imported without manufacturer binding.");
        return null;
    }

    private async Task<ModelPlan> PlanModelAsync(
        SnipeAssetImportRecord record,
        CategoryPlan categoryPlan,
        int? manufacturerId,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var modelKey = BuildModelReferenceKey(record.ModelName, categoryPlan, manufacturerId);
        if (context.TryGetPlannedModel(modelKey, out var existingPlan))
        {
            return new ModelPlan(null, existingPlan);
        }

        if (categoryPlan.CategoryId is { } categoryId)
        {
            var model = await FindEntityByNameAsync(
                "models",
                record.ModelName,
                new Dictionary<string, string>
                {
                    ["search"] = record.ModelName,
                    ["category_id"] = categoryId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                context,
                cancellationToken).ConfigureAwait(false);

            if (model is not null)
            {
                return new ModelPlan(model.Id, null);
            }
        }

        if (!context.Options.CreateMissingModels)
        {
            throw new SnipeApiException("SnipeImport.ModelMissing", $"Model '{record.ModelName}' does not exist and creation is disabled.");
        }

        return new ModelPlan(null, context.GetOrAddPlannedModel(
            modelKey,
            record.ModelName,
            record.CategoryName,
            categoryPlan.CategoryId,
            categoryPlan.CategoryCreate,
            record.ManufacturerName,
            manufacturerId));
    }

    private async Task<SnipeAssetMatch?> FindExistingAssetAsync(
        SnipeAssetImportRecord record,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var macMatch = await FindAssetByMacAsync(record, context, cancellationToken).ConfigureAwait(false);
        if (macMatch is not null)
        {
            return macMatch;
        }

        var serial = Normalize(record.Serial);
        if (serial is not null)
        {
            var serialMatch = await FindAssetBySerialAsync(serial, context.Options, cancellationToken).ConfigureAwait(false);
            if (serialMatch is not null)
            {
                return serialMatch;
            }
        }

        var nameCandidates = await SearchAssetsByNameAsync(record.Name, context.Options, cancellationToken).ConfigureAwait(false);
        if (AssetNameMatcher.HasAmbiguousHighConfidenceMatches(record.Name, nameCandidates, context.Options.NameMatchThreshold))
        {
            throw new SnipeApiException("SnipeImport.AmbiguousNameMatch", $"Asset name '{record.Name}' matched multiple high-confidence Snipe-IT assets.");
        }

        return AssetNameMatcher.ChooseHighConfidenceMatch(record.Name, nameCandidates, context.Options.NameMatchThreshold);
    }

    private async Task<SnipeAssetMatch?> FindAssetByMacAsync(
        SnipeAssetImportRecord record,
        ImportRunContext context,
        CancellationToken cancellationToken)
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
            var displayMac = MacAddressNormalizer.NormalizeDisplay(macAddress);
            if (displayMac is null)
            {
                context.AddWarning("SnipeImport.InvalidMacAddress", $"Asset '{record.Name}' has invalid MAC address '{macAddress}'.");
                continue;
            }

            var filter = JsonSerializer.Serialize(
                new Dictionary<string, string> { [customFieldName] = displayMac },
                JsonOptions);
            var query = new Dictionary<string, string>
            {
                ["limit"] = LookupLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["filter"] = filter
            };

            foreach (var match in await SearchHardwareAsync(query, context.Options, cancellationToken).ConfigureAwait(false))
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

    private async Task<SnipeAssetMatch?> FindAssetBySerialAsync(
        string serial,
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            $"hardware/byserial/{Uri.EscapeDataString(serial)}",
            payload: null,
            options,
            cancellationToken).ConfigureAwait(false);

        if (IsBusinessError(document.RootElement))
        {
            return null;
        }

        return ParseAsset(document.RootElement)
            ?? throw new SnipeApiException("SnipeImport.MalformedResponse", $"Snipe-IT serial lookup for '{serial}' did not return an asset object.");
    }

    private async Task<IReadOnlyList<SnipeAssetMatch>> SearchAssetsByNameAsync(
        string name,
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        return await SearchHardwareAsync(
            new Dictionary<string, string>
            {
                ["limit"] = LookupLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["search"] = name
            },
            options,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyDryRunPlan(
        IReadOnlyList<PlannedRecord> plannedRecords,
        ImportRunContext context)
    {
        foreach (var company in context.PlannedCompanies)
        {
            context.AddAction("Create", "Company", company.Name, $"Company '{company.Name}' is missing.");
            context.CreatedCompanies++;
        }

        foreach (var category in context.PlannedCategories)
        {
            context.AddAction("Create", "Category", category.Name, $"Asset category '{category.Name}' is missing.");
            context.CreatedCategories++;
        }

        foreach (var model in context.PlannedModels)
        {
            context.AddAction("Create", "Model", model.Name, $"Model '{model.Name}' is missing.");
            context.CreatedModels++;
        }

        foreach (var plannedRecord in plannedRecords)
        {
            if (plannedRecord.ExistingAsset is null)
            {
                context.AddAction("Create", "Asset", plannedRecord.Record.AssetTag, $"Asset '{plannedRecord.Record.Name}' does not exist.");
                context.CreatedAssets++;
                continue;
            }

            context.AddAction(
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
        try
        {
            ReportProgress(progress, "Creating missing Snipe-IT reference records before asset writes.", 0, plannedRecords.Count);

            foreach (var company in context.PlannedCompanies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteCompanyCreateAsync(company, context, cancellationToken).ConfigureAwait(false);
            }

            foreach (var category in context.PlannedCategories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteCategoryCreateAsync(category, context, cancellationToken).ConfigureAwait(false);
            }

            foreach (var model in context.PlannedModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteModelCreateAsync(model, context, cancellationToken).ConfigureAwait(false);
            }

            ReportProgress(progress, "Created missing Snipe-IT reference records.", 0, plannedRecords.Count);
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

        context.AddAction("Create", "Company", company.Name, $"Company '{company.Name}' is missing.");
        company.CreatedId = await CreateEntityAsync(
            "companies",
            new Dictionary<string, object?> { ["name"] = company.Name },
            context.Options,
            cancellationToken).ConfigureAwait(false);
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
            ["category_id"] = categoryId.Value
        };

        if (model.ManufacturerId is not null)
        {
            payload["manufacturer_id"] = model.ManufacturerId.Value;
        }

        context.AddAction("Create", "Model", model.Name, $"Model '{model.Name}' is missing.");
        model.CreatedId = await CreateEntityAsync("models", payload, context.Options, cancellationToken).ConfigureAwait(false);
        context.CreatedModels++;
        return model.CreatedId.Value;
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

        context.AddAction("Create", "Category", category.Name, $"Asset category '{category.Name}' is missing.");
        category.CreatedId = await CreateEntityAsync(
            "categories",
            new Dictionary<string, object?>
            {
                ["name"] = category.Name,
                ["category_type"] = AssetCategoryType
            },
            context.Options,
            cancellationToken).ConfigureAwait(false);
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
        context.AddAction("Create", "Asset", record.AssetTag, $"Asset '{record.Name}' does not exist.");

        using var document = await SendJsonAsync(
            HttpMethod.Post,
            "hardware",
            BuildAssetPayload(record, modelId, companyId, context.Options, _timeProvider.GetUtcNow()),
            context.Options,
            cancellationToken).ConfigureAwait(false);
        EnsureBusinessSuccess(document.RootElement);
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
        context.AddAction("Update", "Asset", existingAsset.AssetTag ?? existingAsset.Name, $"Asset '{record.Name}' matched Snipe-IT asset id {existingAsset.Id}.");

        using var document = await SendJsonAsync(
            HttpMethod.Patch,
            $"hardware/{existingAsset.Id}",
            BuildAssetPayload(record, modelId, companyId, context.Options, _timeProvider.GetUtcNow()),
            context.Options,
            cancellationToken).ConfigureAwait(false);
        EnsureBusinessSuccess(document.RootElement);
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
                Normalize(plannedRecord.Record.Serial),
                plannedRecord.Record.CompanyName,
                plannedRecord.Record.ModelName,
                plannedRecord.Record.CategoryName,
                plannedRecord.Record.ManufacturerName,
                plannedRecord.ExistingAsset?.Id,
                plannedRecord.ExistingAsset?.AssetTag,
                FailureCode: null,
                FailureMessage: null))
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
                    model.CategoryName,
                    model.CategoryId,
                    model.ManufacturerName,
                    model.ManufacturerId))
                .ToList()
        };
    }

    private async Task<SnipeEntity?> FindEntityByNameAsync(
        string path,
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

        using var document = await SendJsonAsync(
            HttpMethod.Get,
            BuildPath(path, query),
            payload: null,
            context.Options,
            cancellationToken).ConfigureAwait(false);

        if (IsBusinessError(document.RootElement))
        {
            context.CacheEntityLookup(cacheKey, null);
            return null;
        }

        var entity = ParseRows(document.RootElement)
            .Select(ParseEntity)
            .Where(entity => entity is not null)
            .Select(entity => entity!)
            .FirstOrDefault(entity => string.Equals(entity.Name, expectedName, StringComparison.OrdinalIgnoreCase));
        context.CacheEntityLookup(cacheKey, entity);
        return entity;
    }

    private async Task<int> CreateEntityAsync(
        string path,
        object payload,
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(HttpMethod.Post, path, payload, options, cancellationToken).ConfigureAwait(false);
        EnsureBusinessSuccess(document.RootElement);
        return ReadEntityId(document.RootElement)
            ?? throw new SnipeApiException("SnipeImport.MissingResponseId", $"Snipe-IT create response for '{path}' did not include an id.");
    }

    private async Task<IReadOnlyList<SnipeAssetMatch>> SearchHardwareAsync(
        IReadOnlyDictionary<string, string> query,
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            BuildPath("hardware", query),
            payload: null,
            options,
            cancellationToken).ConfigureAwait(false);

        if (IsBusinessError(document.RootElement))
        {
            return [];
        }

        return ParseRows(document.RootElement)
            .Select(ParseAsset)
            .Where(asset => asset is not null)
            .Select(asset => asset!)
            .ToList();
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUri(relativePath, options));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);

        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SnipeApiException("SnipeImport.HttpFailure", $"Snipe-IT returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
        }
        catch (JsonException exception)
        {
            throw new SnipeApiException("SnipeImport.MalformedResponse", "Snipe-IT returned malformed JSON.", exception);
        }
    }

    private static Uri BuildUri(string relativePath, SnipeImportOptions options)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl), relativePath.TrimStart('/'));
    }

    private static Dictionary<string, object?> BuildAssetPayload(
        SnipeAssetImportRecord record,
        int modelId,
        int? companyId,
        SnipeImportOptions options,
        DateTimeOffset syncedAt)
    {
        var payload = new Dictionary<string, object?>
        {
            ["asset_tag"] = record.AssetTag,
            ["status_id"] = record.StatusId,
            ["model_id"] = modelId,
            ["name"] = record.Name,
            ["notes"] = BuildAutoSyncedNotes(record.Notes, syncedAt)
        };

        if (Normalize(record.Serial) is { } serial)
        {
            payload["serial"] = serial;
        }

        if (companyId is not null)
        {
            payload["company_id"] = companyId.Value;
        }

        if (Normalize(options.MacAddressCustomFieldDbColumnName) is { } macFieldName
            && record.MacAddresses.Select(MacAddressNormalizer.NormalizeDisplay).FirstOrDefault(value => value is not null) is { } macAddress)
        {
            payload[macFieldName] = macAddress;
        }

        return payload;
    }

    private static string BuildAutoSyncedNotes(string notes, DateTimeOffset syncedAt)
    {
        var syncLine = $"Auto Synced from Atera at {syncedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)}";
        return string.IsNullOrWhiteSpace(notes)
            ? syncLine
            : $"{notes.TrimEnd()}{Environment.NewLine}{syncLine}";
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

    private static IReadOnlyList<JsonElement> ParseRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToList();
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            return rows.EnumerateArray().ToList();
        }

        return [];
    }

    private static SnipeEntity? ParseEntity(JsonElement element)
    {
        var id = ReadInt(element, "id");
        var name = ReadString(element, "name");
        return id is null || name is null ? null : new SnipeEntity(id.Value, name);
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
            ReadString(element, "serial"));
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
            JsonValueKind.String => Normalize(value.GetString()),
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

    private static void EnsureBusinessSuccess(JsonElement root)
    {
        if (!IsBusinessError(root))
        {
            return;
        }

        var message = ReadString(root, "messages") ?? ReadString(root, "message") ?? "Snipe-IT returned an error response.";
        throw new SnipeApiException("SnipeImport.BusinessError", message);
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

    private static void ValidateOptions(SnipeImportOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Snipe-IT base URL must be an absolute URL.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ApiToken))
        {
            throw new ArgumentException("Snipe-IT API token is required.", nameof(options));
        }

        if (options.NameMatchThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Name match threshold must be greater than 0 and less than or equal to 1.");
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

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private sealed record SnipeEntity(int Id, string Name);

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

    private sealed record ManufacturerPlan(int? ManufacturerId);

    private sealed record ModelPlan(int? ModelId, PlannedModelCreate? ModelCreate);

    private sealed record PlannedRecord(
        SnipeAssetImportRecord Record,
        int? CompanyId,
        PlannedCompanyCreate? CompanyCreate,
        int? ManufacturerId,
        int? ModelId,
        PlannedModelCreate? ModelCreate,
        SnipeAssetMatch? ExistingAsset);

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
            int? manufacturerId)
        {
            Name = name;
            CategoryName = categoryName;
            CategoryId = categoryId;
            CategoryCreate = categoryCreate;
            ManufacturerName = manufacturerName;
            ManufacturerId = manufacturerId;
        }

        public string Name { get; }
        public string CategoryName { get; }
        public int? CategoryId { get; }
        public PlannedCategoryCreate? CategoryCreate { get; }
        public string ManufacturerName { get; }
        public int? ManufacturerId { get; }
        public int? CreatedId { get; set; }
    }

    private sealed class ImportRunContext
    {
        private readonly Dictionary<string, PlannedCompanyCreate> _plannedCompanies = [];
        private readonly Dictionary<string, PlannedCategoryCreate> _plannedCategories = [];
        private readonly Dictionary<string, PlannedModelCreate> _plannedModels = [];
        private readonly Dictionary<string, SnipeEntity?> _entityLookupCache = [];
        private bool _macMatchingDisabledWarningAdded;

        public ImportRunContext(SnipeImportOptions options, List<ModuleWarning> warnings)
        {
            Options = options;
            Warnings = warnings;
        }

        public SnipeImportOptions Options { get; }
        public List<ImportAction> Actions { get; } = [];
        public List<ImportFailure> Failures { get; } = [];
        public List<SnipeAssetPreflightRow> FailedPreflightAssets { get; } = [];
        public List<ModuleWarning> Warnings { get; }
        public IEnumerable<PlannedCompanyCreate> PlannedCompanies => _plannedCompanies.Values;
        public IEnumerable<PlannedCategoryCreate> PlannedCategories => _plannedCategories.Values;
        public IEnumerable<PlannedModelCreate> PlannedModels => _plannedModels.Values;
        public int CreatedAssets { get; set; }
        public int UpdatedAssets { get; set; }
        public int SkippedAssets { get; set; }
        public int FailedAssets { get; set; }
        public int CreatedCompanies { get; set; }
        public int CreatedCategories { get; set; }
        public int CreatedModels { get; set; }

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
            int? manufacturerId)
        {
            if (_plannedModels.TryGetValue(modelKey, out var plannedModel))
            {
                return plannedModel;
            }

            plannedModel = new PlannedModelCreate(modelName, categoryName, categoryId, categoryCreate, manufacturerName, manufacturerId);
            _plannedModels[modelKey] = plannedModel;
            return plannedModel;
        }

        public void AddAction(string actionType, string targetType, string targetName, string message)
        {
            Actions.Add(new ImportAction
            {
                ActionType = actionType,
                TargetType = targetType,
                TargetName = targetName,
                WasExecuted = !Options.DryRun,
                Message = message
            });
        }

        public void AddFailure(SnipeAssetImportRecord record, string code, string message)
        {
            FailedAssets++;
            FailedPreflightAssets.Add(new SnipeAssetPreflightRow(
                "Blocked",
                record.AssetTag,
                record.Name,
                Normalize(record.Serial),
                record.CompanyName,
                record.ModelName,
                record.CategoryName,
                record.ManufacturerName,
                ExistingAssetId: null,
                ExistingAssetTag: null,
                FailureCode: code,
                FailureMessage: message));
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

        public SnipeImportResult ToResult()
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
                DryRun = Options.DryRun,
                Actions = Actions,
                Failures = Failures,
                Warnings = Warnings
            };
        }
    }
}
