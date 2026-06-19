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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var context = new ImportRunContext(options, batch.Warnings.ToList());
        var plannedRecords = new List<PlannedRecord>();
        _logger.LogInformation("Starting Snipe-IT import for {AssetCount} asset(s).", batch.Assets.Count);

        foreach (var record in batch.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                ValidateRecord(record);
                plannedRecords.Add(await PlanRecordAsync(record, context, cancellationToken).ConfigureAwait(false));
            }
            catch (SnipeApiException exception)
            {
                context.AddFailure(record, exception.Code, exception.Message);
            }
            catch (JsonException exception)
            {
                context.AddFailure(record, "SnipeImport.MalformedResponse", $"Snipe-IT response could not be parsed: {exception.Message}");
            }
        }

        if (options.ManualPreflightCsvEnabled)
        {
            if (!await TryWritePreflightCsvAsync(plannedRecords, context, cancellationToken).ConfigureAwait(false))
            {
                return context.ToResult();
            }
        }

        if (options.DryRun)
        {
            ApplyDryRunPlan(plannedRecords, context);
            return context.ToResult();
        }

        foreach (var plannedRecord in plannedRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ExecutePlanAsync(plannedRecord, context, cancellationToken).ConfigureAwait(false);
            }
            catch (SnipeApiException exception)
            {
                context.AddFailure(plannedRecord.Record, exception.Code, exception.Message);
            }
            catch (JsonException exception)
            {
                context.AddFailure(plannedRecord.Record, "SnipeImport.MalformedResponse", $"Snipe-IT response could not be parsed: {exception.Message}");
            }
        }

        _logger.LogInformation(
            "Completed Snipe-IT import. Created={CreatedAssets}, Updated={UpdatedAssets}, Failed={FailedAssets}.",
            context.CreatedAssets,
            context.UpdatedAssets,
            context.FailedAssets);

        return context.ToResult();
    }

    private async Task<PlannedRecord> PlanRecordAsync(
        SnipeAssetImportRecord record,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var companyPlan = await PlanCompanyAsync(record.CompanyName, context, cancellationToken).ConfigureAwait(false);
        var categoryId = await FindRequiredCategoryAsync(record.CategoryName, context, cancellationToken).ConfigureAwait(false);
        var manufacturerId = await FindManufacturerAsync(record.ManufacturerName, context, cancellationToken).ConfigureAwait(false);
        var modelPlan = await PlanModelAsync(record, categoryId, manufacturerId, context, cancellationToken).ConfigureAwait(false);
        var existingAsset = await FindExistingAssetAsync(record, context, cancellationToken).ConfigureAwait(false);

        return new PlannedRecord(
            record,
            companyPlan.CompanyId,
            companyPlan.CompanyCreate,
            categoryId,
            manufacturerId,
            modelPlan.ModelId,
            modelPlan.ModelCreate,
            existingAsset);
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
            context.Options,
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

    private async Task<int> FindRequiredCategoryAsync(
        string categoryName,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        var category = await FindEntityByNameAsync(
            "categories",
            categoryName,
            new Dictionary<string, string> { ["search"] = categoryName },
            context.Options,
            cancellationToken).ConfigureAwait(false);

        return category?.Id
            ?? throw new SnipeApiException("SnipeImport.CategoryMissing", $"Category '{categoryName}' does not exist.");
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
            context.Options,
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
        int categoryId,
        int? manufacturerId,
        ImportRunContext context,
        CancellationToken cancellationToken)
    {
        if (context.TryGetPlannedModel(record.ModelName, categoryId, manufacturerId, out var existingPlan))
        {
            return new ModelPlan(null, existingPlan);
        }

        var model = await FindEntityByNameAsync(
            "models",
            record.ModelName,
            new Dictionary<string, string>
            {
                ["search"] = record.ModelName,
                ["category_id"] = categoryId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            context.Options,
            cancellationToken).ConfigureAwait(false);

        if (model is not null)
        {
            return new ModelPlan(model.Id, null);
        }

        if (!context.Options.CreateMissingModels)
        {
            throw new SnipeApiException("SnipeImport.ModelMissing", $"Model '{record.ModelName}' does not exist and creation is disabled.");
        }

        return new ModelPlan(null, context.GetOrAddPlannedModel(record.ModelName, record.CategoryName, categoryId, record.ManufacturerName, manufacturerId));
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
        var companyId = plannedRecord.CompanyId;
        if (plannedRecord.CompanyCreate is not null)
        {
            companyId = await ExecuteCompanyCreateAsync(plannedRecord.CompanyCreate, context, cancellationToken).ConfigureAwait(false);
        }

        var modelId = plannedRecord.ModelId;
        if (plannedRecord.ModelCreate is not null)
        {
            modelId = await ExecuteModelCreateAsync(plannedRecord.ModelCreate, context, cancellationToken).ConfigureAwait(false);
        }

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

        var payload = new Dictionary<string, object?>
        {
            ["name"] = model.Name,
            ["category_id"] = model.CategoryId
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
                plannedRecord.ExistingAsset?.AssetTag)).ToList(),
            Companies = context.PlannedCompanies
                .Select(company => new SnipeCompanyPreflightRow("Add", company.Name))
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
        SnipeImportOptions options,
        CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(
            HttpMethod.Get,
            BuildPath(path, query),
            payload: null,
            options,
            cancellationToken).ConfigureAwait(false);

        if (IsBusinessError(document.RootElement))
        {
            return null;
        }

        return ParseRows(document.RootElement)
            .Select(ParseEntity)
            .Where(entity => entity is not null)
            .Select(entity => entity!)
            .FirstOrDefault(entity => string.Equals(entity.Name, expectedName, StringComparison.OrdinalIgnoreCase));
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

    private sealed record CompanyPlan(int? CompanyId, PlannedCompanyCreate? CompanyCreate);

    private sealed record ModelPlan(int? ModelId, PlannedModelCreate? ModelCreate);

    private sealed record PlannedRecord(
        SnipeAssetImportRecord Record,
        int? CompanyId,
        PlannedCompanyCreate? CompanyCreate,
        int CategoryId,
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

    private sealed class PlannedModelCreate
    {
        public PlannedModelCreate(
            string name,
            string categoryName,
            int categoryId,
            string manufacturerName,
            int? manufacturerId)
        {
            Name = name;
            CategoryName = categoryName;
            CategoryId = categoryId;
            ManufacturerName = manufacturerName;
            ManufacturerId = manufacturerId;
        }

        public string Name { get; }
        public string CategoryName { get; }
        public int CategoryId { get; }
        public string ManufacturerName { get; }
        public int? ManufacturerId { get; }
        public int? CreatedId { get; set; }
    }

    private sealed class ImportRunContext
    {
        private readonly Dictionary<string, PlannedCompanyCreate> _plannedCompanies = [];
        private readonly Dictionary<string, PlannedModelCreate> _plannedModels = [];
        private bool _macMatchingDisabledWarningAdded;

        public ImportRunContext(SnipeImportOptions options, List<ModuleWarning> warnings)
        {
            Options = options;
            Warnings = warnings;
        }

        public SnipeImportOptions Options { get; }
        public List<ImportAction> Actions { get; } = [];
        public List<ImportFailure> Failures { get; } = [];
        public List<ModuleWarning> Warnings { get; }
        public IEnumerable<PlannedCompanyCreate> PlannedCompanies => _plannedCompanies.Values;
        public IEnumerable<PlannedModelCreate> PlannedModels => _plannedModels.Values;
        public int CreatedAssets { get; set; }
        public int UpdatedAssets { get; set; }
        public int SkippedAssets { get; set; }
        public int FailedAssets { get; set; }
        public int CreatedCompanies { get; set; }
        public int CreatedModels { get; set; }

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

        public bool TryGetPlannedModel(
            string modelName,
            int categoryId,
            int? manufacturerId,
            out PlannedModelCreate? model)
        {
            return _plannedModels.TryGetValue(BuildModelKey(modelName, categoryId, manufacturerId), out model);
        }

        public PlannedModelCreate GetOrAddPlannedModel(
            string modelName,
            string categoryName,
            int categoryId,
            string manufacturerName,
            int? manufacturerId)
        {
            var key = BuildModelKey(modelName, categoryId, manufacturerId);
            if (_plannedModels.TryGetValue(key, out var plannedModel))
            {
                return plannedModel;
            }

            plannedModel = new PlannedModelCreate(modelName, categoryName, categoryId, manufacturerName, manufacturerId);
            _plannedModels[key] = plannedModel;
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
                CreatedModels = CreatedModels,
                DryRun = Options.DryRun,
                Actions = Actions,
                Failures = Failures,
                Warnings = Warnings
            };
        }

        private static string BuildModelKey(string modelName, int categoryId, int? manufacturerId)
        {
            return $"{NormalizeKey(modelName)}|{categoryId.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{manufacturerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}";
        }
    }
}
