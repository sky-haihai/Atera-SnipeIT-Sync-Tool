using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.Common;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Plans and executes operator-confirmed normalization for models in selected source categories.
/// </summary>
public sealed class SnipeModelCategoryNormalizer
{
    private const int MaximumSnapshotPages = 10000;
    private const int MaximumErrorDetailLength = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<SnipeModelCategoryNormalizer> _logger;

    public SnipeModelCategoryNormalizer(
        HttpClient httpClient,
        ILogger<SnipeModelCategoryNormalizer> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the existing target asset category, reads every model, and returns a mutation-free review plan.
    /// </summary>
    public async Task<SnipeModelCategoryNormalizationPlan> PlanAsync(
        SnipeModelCategoryNormalizationOptions options,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null)
    {
        var validated = ValidateOptions(options);
        ReportProgress(
            progress,
            $"Resolving the target category and scanning Snipe-IT models in: {string.Join(", ", validated.SourceCategoryNames)}.",
            0,
            null,
            0);

        var targetCategory = await ResolveTargetCategoryAsync(validated, cancellationToken).ConfigureAwait(false);
        var models = new Dictionary<int, ModelReference>();
        var scannedModelCount = 0;
        var modelSnapshotCompleted = false;
        int? modelTotal = null;
        for (var page = 0; page < MaximumSnapshotPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = scannedModelCount;
            using var document = await SendJsonAsync(
                HttpMethod.Get,
                $"models?limit={validated.PageSize}&offset={offset}",
                serializedPayload: null,
                validated,
                cancellationToken,
                mutation: false).ConfigureAwait(false);

            var rows = ReadRequiredRows(document.RootElement, "Scan Snipe-IT models");
            modelTotal ??= ReadOptionalTotal(document.RootElement, "Scan Snipe-IT models");
            foreach (var row in rows)
            {
                AddModel(models, ParseModelRow(row));
            }

            scannedModelCount += rows.Count;
            ReportProgress(
                progress,
                $"Scanned {scannedModelCount} Snipe-IT model(s).",
                scannedModelCount,
                modelTotal,
                CalculatePlanningPercent(scannedModelCount, modelTotal));

            if (rows.Count == 0
                || modelTotal is not null && scannedModelCount >= modelTotal.Value
                || modelTotal is null && rows.Count < validated.PageSize)
            {
                modelSnapshotCompleted = true;
                break;
            }
        }

        if (!modelSnapshotCompleted)
        {
            throw CreateFailure("IncompleteSnapshot", "Model snapshot exceeded the maximum page count.");
        }

        if (modelTotal is not null && scannedModelCount != modelTotal.Value)
        {
            throw CreateFailure(
                "IncompleteSnapshot",
                $"Model snapshot reported {modelTotal.Value} row(s) but returned {scannedModelCount}.");
        }

        var plannedModels = models.Values
            .Where(model => model.CategoryId != targetCategory.Id
                && validated.SourceCategoryNames.Contains(model.CategoryName))
            .Select(model => new SnipeModelCategoryNormalizationCandidate(
                model.ModelId,
                model.ModelName,
                model.CategoryName))
            .OrderBy(candidate => candidate.ModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ModelId)
            .ToList();

        _logger.LogInformation(
            "Planned model category normalization after scanning {ScannedModelCount} model(s): {CandidateModelCount} candidate(s) from {SourceCategories}, target category id {CategoryId}.",
            scannedModelCount,
            plannedModels.Count,
            string.Join(", ", validated.SourceCategoryNames),
            targetCategory.Id);
        ReportProgress(
            progress,
            $"Category normalization scan complete: {plannedModels.Count} of {scannedModelCount} model(s) require an update.",
            scannedModelCount,
            scannedModelCount,
            50);

        return new SnipeModelCategoryNormalizationPlan
        {
            ScannedModelCount = scannedModelCount,
            TargetCategoryId = targetCategory.Id,
            TargetCategoryName = targetCategory.Name,
            SourceCategoryNames = validated.SourceCategoryNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            Models = plannedModels
        };
    }

    /// <summary>
    /// Updates each reviewed model once, continuing after independent failures and returning partial results on cancellation.
    /// </summary>
    public async Task<SnipeModelCategoryNormalizationResult> ExecuteAsync(
        SnipeModelCategoryNormalizationPlan plan,
        SnipeModelCategoryNormalizationOptions options,
        CancellationToken cancellationToken,
        IProgress<SyncProgressUpdate>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validated = ValidateOptions(options);
        ValidatePlan(plan, validated);

        var outcomes = new List<SnipeModelCategoryNormalizationOutcome>();
        for (var index = 0; index < plan.Models.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return BuildResult(plan, outcomes, cancelled: true, progress);
            }

            var candidate = plan.Models[index];
            ReportProgress(
                progress,
                $"Updating model {index + 1}/{plan.Models.Count}: {candidate.ModelName}.",
                index,
                plan.Models.Count,
                CalculateExecutionPercent(index, plan.Models.Count));

            try
            {
                var payload = JsonSerializer.Serialize(
                    new Dictionary<string, object?>
                    {
                        ["name"] = candidate.ModelName,
                        ["category_id"] = plan.TargetCategoryId
                    },
                    JsonOptions);
                using var document = await SendJsonAsync(
                    HttpMethod.Put,
                    $"models/{candidate.ModelId}",
                    payload,
                    validated,
                    CancellationToken.None,
                    mutation: true).ConfigureAwait(false);
                EnsureBusinessSuccess(document.RootElement, $"Update model '{candidate.ModelName}'");

                outcomes.Add(new SnipeModelCategoryNormalizationOutcome(
                    candidate.ModelId,
                    candidate.ModelName,
                    candidate.SourceCategoryName,
                    plan.TargetCategoryName,
                    Success: true,
                    ErrorCode: null,
                    ErrorMessage: null));
                _logger.LogInformation(
                    "Updated Snipe-IT model id {ModelId} from {SourceCategory} to category id {CategoryId}.",
                    candidate.ModelId,
                    candidate.SourceCategoryName,
                    plan.TargetCategoryId);
            }
            catch (SnipeApiException exception)
            {
                outcomes.Add(new SnipeModelCategoryNormalizationOutcome(
                    candidate.ModelId,
                    candidate.ModelName,
                    candidate.SourceCategoryName,
                    plan.TargetCategoryName,
                    Success: false,
                    ErrorCode: exception.Code,
                    ErrorMessage: exception.Message));
                _logger.LogWarning(
                    "Could not update Snipe-IT model id {ModelId}: {FailureCode}.",
                    candidate.ModelId,
                    exception.Code);
            }

            ReportProgress(
                progress,
                $"Processed model {index + 1}/{plan.Models.Count}: {candidate.ModelName}.",
                index + 1,
                plan.Models.Count,
                CalculateExecutionPercent(index + 1, plan.Models.Count));
        }

        return BuildResult(plan, outcomes, cancelled: false, progress);
    }

    private async Task<SnipeCategory> ResolveTargetCategoryAsync(
        ValidatedOptions options,
        CancellationToken cancellationToken)
    {
        var categories = new List<SnipeCategory>();
        var offset = 0;
        var categorySnapshotCompleted = false;
        int? total = null;
        for (var page = 0; page < MaximumSnapshotPages; page++)
        {
            using var document = await SendJsonAsync(
                HttpMethod.Get,
                $"categories?limit={options.PageSize}&offset={offset}",
                serializedPayload: null,
                options,
                cancellationToken,
                mutation: false).ConfigureAwait(false);
            var rows = ReadRequiredRows(document.RootElement, "Load Snipe-IT categories");
            total ??= ReadOptionalTotal(document.RootElement, "Load Snipe-IT categories");
            categories.AddRange(rows.Select(ParseCategoryRow));
            offset += rows.Count;

            if (rows.Count == 0
                || total is not null && offset >= total.Value
                || total is null && rows.Count < options.PageSize)
            {
                categorySnapshotCompleted = true;
                break;
            }
        }

        if (!categorySnapshotCompleted)
        {
            throw CreateFailure("IncompleteSnapshot", "Category snapshot exceeded the maximum page count.");
        }

        if (total is not null && offset != total.Value)
        {
            throw CreateFailure(
                "IncompleteSnapshot",
                $"Category snapshot reported {total.Value} row(s) but returned {offset}.");
        }

        var namedCategories = categories
            .Where(category => string.Equals(category.Name, options.TargetCategoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var assetCategories = namedCategories
            .Where(category => category.CategoryType is null
                || string.Equals(category.CategoryType, "asset", StringComparison.OrdinalIgnoreCase))
            .GroupBy(category => category.Id)
            .Select(group => group.First())
            .ToList();

        if (assetCategories.Count == 0)
        {
            var suffix = namedCategories.Count == 0
                ? "does not exist"
                : "does not identify an asset category";
            throw CreateFailure(
                "TargetCategoryMissing",
                $"Target category '{options.TargetCategoryName}' {suffix}; no model updates were sent.");
        }

        if (assetCategories.Count > 1)
        {
            throw CreateFailure(
                "AmbiguousTargetCategory",
                $"Target category '{options.TargetCategoryName}' matched multiple category ids; no model updates were sent.");
        }

        return assetCategories[0];
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        string? serializedPayload,
        ValidatedOptions options,
        CancellationToken cancellationToken,
        bool mutation)
    {
        using var request = new HttpRequestMessage(method, BuildUri(options.BaseUri, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
        if (serializedPayload is not null)
        {
            request.Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = TryReadErrorDetail(content);
                var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                    ? $"HTTP {(int)response.StatusCode}"
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                var detailSuffix = detail is null ? string.Empty : $" Detail: {detail}";
                throw CreateFailure(
                    ClassifyHttpFailure(response.StatusCode),
                    $"{method} /{relativePath} failed: Snipe-IT returned {reason}.{detailSuffix}");
            }

            try
            {
                var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
                EnsureBusinessSuccess(document.RootElement, $"{method} /{relativePath}");
                return document;
            }
            catch (JsonException exception)
            {
                throw CreateFailure(
                    "MalformedResponse",
                    $"{method} /{relativePath} failed: Snipe-IT returned malformed JSON.",
                    exception);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested || mutation)
        {
            throw CreateFailure(
                "Timeout",
                $"{method} /{relativePath} failed: the Snipe-IT request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw CreateFailure(
                "NetworkFailure",
                $"{method} /{relativePath} failed: network request error: {exception.Message}",
                exception);
        }
    }

    private static ModelReference ParseModelRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw CreateFailure("MalformedResponse", "Model snapshot contained a non-object row.");
        }

        var category = ReadRequiredNestedObject(row, "category", "Model row");
        return new ModelReference(
            ReadRequiredPositiveInt(row, "id", "Model row"),
            ReadRequiredName(row, "name", "Model row"),
            ReadRequiredPositiveInt(category, "id", "Model category"),
            ReadRequiredName(category, "name", "Model category"));
    }

    private static SnipeCategory ParseCategoryRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw CreateFailure("MalformedResponse", "Category snapshot contained a non-object row.");
        }

        var categoryType = row.TryGetProperty("category_type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            ? DecodeName(typeElement.GetString())
            : null;
        return new SnipeCategory(
            ReadRequiredPositiveInt(row, "id", "Category row"),
            ReadRequiredName(row, "name", "Category row"),
            categoryType);
    }

    private static void AddModel(
        IDictionary<int, ModelReference> models,
        ModelReference model)
    {
        if (!models.TryGetValue(model.ModelId, out var existing))
        {
            models[model.ModelId] = model;
            return;
        }

        if (!string.Equals(existing.ModelName, model.ModelName, StringComparison.Ordinal)
            || existing.CategoryId != model.CategoryId
            || !string.Equals(existing.CategoryName, model.CategoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateFailure(
                "InconsistentModelReference",
                $"Model id {model.ModelId} appeared with conflicting name or category values; no model updates were sent.");
        }
    }

    private static IReadOnlyList<JsonElement> ReadRequiredRows(JsonElement root, string operation)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rows", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            throw CreateFailure("MalformedResponse", $"{operation} response did not contain a rows array.");
        }

        return rows.EnumerateArray().Select(row => row.Clone()).ToList();
    }

    private static int? ReadOptionalTotal(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("total", out var total))
        {
            return null;
        }

        if (TryReadInt(total, out var value) && value >= 0)
        {
            return value;
        }

        throw CreateFailure("MalformedResponse", $"{operation} response contained an invalid total value.");
    }

    private static JsonElement ReadRequiredNestedObject(JsonElement parent, string propertyName, string context)
    {
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            throw CreateFailure("MalformedResponse", $"{context} did not contain a valid {propertyName} object.");
        }

        return element;
    }

    private static int ReadRequiredPositiveInt(JsonElement parent, string propertyName, string context)
    {
        if (parent.TryGetProperty(propertyName, out var element)
            && TryReadInt(element, out var value)
            && value > 0)
        {
            return value;
        }

        throw CreateFailure("MalformedResponse", $"{context} did not contain a valid positive {propertyName}.");
    }

    private static string ReadRequiredName(JsonElement parent, string propertyName, string context)
    {
        if (parent.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var value = DecodeName(element.GetString());
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw CreateFailure("MalformedResponse", $"{context} did not contain a valid {propertyName}.");
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            _ => FailInt(out value)
        };
    }

    private static bool FailInt(out int value)
    {
        value = 0;
        return false;
    }

    private static void EnsureBusinessSuccess(JsonElement root, string operation)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || !string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var detail = root.TryGetProperty("messages", out var messages)
            ? FlattenMessage(messages)
            : null;
        throw CreateFailure(
            "BusinessError",
            detail is null
                ? $"{operation} failed: Snipe-IT reported status=error."
                : $"{operation} failed: Snipe-IT reported status=error. Detail: {detail}");
    }

    private static string? TryReadErrorDetail(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var messages))
            {
                return FlattenMessage(messages);
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var message))
            {
                return FlattenMessage(message);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? FlattenMessage(JsonElement element)
    {
        var parts = new List<string>();
        CollectMessageParts(element, parts, prefix: null);
        if (parts.Count == 0)
        {
            return null;
        }

        var value = string.Join("; ", parts.Distinct(StringComparer.Ordinal));
        return value.Length <= MaximumErrorDetailLength
            ? value
            : value[..MaximumErrorDetailLength] + "…";
    }

    private static void CollectMessageParts(JsonElement element, ICollection<string> parts, string? prefix)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = DecodeName(element.GetString());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(prefix is null ? text : $"{prefix}: {text}");
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectMessageParts(item, parts, prefix);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectMessageParts(property.Value, parts, property.Name);
                }

                break;
        }
    }

    private static ValidatedOptions ValidateOptions(SnipeModelCategoryNormalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baseUri = ApiEndpointValidator.ValidateSnipeBaseUri(options.BaseUrl);
        var apiToken = options.ApiToken?.Trim();
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            throw new ArgumentException("Snipe-IT API token is required.", nameof(options));
        }

        var targetCategoryName = DecodeName(options.TargetCategoryName);
        if (string.IsNullOrWhiteSpace(targetCategoryName))
        {
            throw new ArgumentException("Target category name is required.", nameof(options));
        }

        if (options.PageSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Page size must be between 1 and 500.");
        }

        var sourceCategoryNames = (options.SourceCategoryNames ?? [])
            .Select(DecodeName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceCategoryNames.Count == 0)
        {
            throw new ArgumentException("At least one source category name is required.", nameof(options));
        }

        return new ValidatedOptions(
            baseUri,
            apiToken,
            targetCategoryName,
            sourceCategoryNames,
            options.PageSize);
    }

    private static void ValidatePlan(SnipeModelCategoryNormalizationPlan plan, ValidatedOptions options)
    {
        if (plan.TargetCategoryId <= 0
            || !string.Equals(plan.TargetCategoryName, options.TargetCategoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Normalization plan target does not match the current target category option.", nameof(plan));
        }

        if (!plan.SourceCategoryNames.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(options.SourceCategoryNames))
        {
            throw new ArgumentException("Normalization plan source categories do not match the current source category options.", nameof(plan));
        }

        if (plan.Models.Any(candidate => candidate.ModelId <= 0
            || string.IsNullOrWhiteSpace(candidate.ModelName)
            || string.IsNullOrWhiteSpace(candidate.SourceCategoryName)
            || string.Equals(candidate.SourceCategoryName, plan.TargetCategoryName, StringComparison.OrdinalIgnoreCase)
            || !options.SourceCategoryNames.Contains(candidate.SourceCategoryName)))
        {
            throw new ArgumentException("Normalization plan contains an invalid model candidate.", nameof(plan));
        }

        if (plan.Models.Select(candidate => candidate.ModelId).Distinct().Count() != plan.Models.Count)
        {
            throw new ArgumentException("Normalization plan contains duplicate model ids.", nameof(plan));
        }
    }

    private static SnipeModelCategoryNormalizationResult BuildResult(
        SnipeModelCategoryNormalizationPlan plan,
        IReadOnlyList<SnipeModelCategoryNormalizationOutcome> outcomes,
        bool cancelled,
        IProgress<SyncProgressUpdate>? progress)
    {
        var result = new SnipeModelCategoryNormalizationResult
        {
            Plan = plan,
            Outcomes = outcomes.ToList(),
            Cancelled = cancelled
        };
        ReportProgress(
            progress,
            cancelled
                ? "Category normalization canceled with a partial result."
                : $"Category normalization finished: {result.UpdatedModelCount} updated, {result.FailedModelCount} failed.",
            outcomes.Count,
            plan.Models.Count,
            cancelled ? CalculateExecutionPercent(outcomes.Count, plan.Models.Count) : 100);
        return result;
    }

    private static void ReportProgress(
        IProgress<SyncProgressUpdate>? progress,
        string message,
        int current,
        int? total,
        int percent)
    {
        progress?.Report(new SyncProgressUpdate
        {
            Stage = "CategoryNormalize",
            Message = message,
            Current = current,
            Total = total,
            Percent = percent
        });
    }

    private static int CalculatePlanningPercent(int current, int? total)
    {
        return total is > 0
            ? Math.Clamp((int)Math.Round(current / (double)total.Value * 45D), 0, 45)
            : 5;
    }

    private static int CalculateExecutionPercent(int current, int total)
    {
        return total > 0
            ? 50 + Math.Clamp((int)Math.Round(current / (double)total * 50D), 0, 50)
            : 100;
    }

    private static string DecodeName(string? value)
    {
        return WebUtility.HtmlDecode(value ?? string.Empty).Trim();
    }

    private static Uri BuildUri(Uri baseUri, string relativePath)
    {
        return new Uri(new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/"), relativePath.TrimStart('/'));
    }

    private static string ClassifyHttpFailure(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "AuthenticationFailed",
            HttpStatusCode.Forbidden => "AuthorizationFailed",
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest => "ValidationFailed",
            HttpStatusCode.TooManyRequests => "RateLimited",
            >= HttpStatusCode.InternalServerError => "ServerFailure",
            _ => "HttpFailure"
        };
    }

    private static SnipeApiException CreateFailure(string suffix, string message)
    {
        return new SnipeApiException($"SnipeModelCategoryNormalization.{suffix}", message);
    }

    private static SnipeApiException CreateFailure(string suffix, string message, Exception innerException)
    {
        return new SnipeApiException($"SnipeModelCategoryNormalization.{suffix}", message, innerException);
    }

    /// <summary>
    /// Holds normalized, validated values so request methods never need to reinterpret operator input.
    /// </summary>
    private sealed record ValidatedOptions(
        Uri BaseUri,
        string ApiToken,
        string TargetCategoryName,
        IReadOnlySet<string> SourceCategoryNames,
        int PageSize);

    /// <summary>
    /// Carries the Model and Category relationship extracted from one model snapshot row.
    /// </summary>
    private sealed record ModelReference(int ModelId, string ModelName, int CategoryId, string CategoryName);

    /// <summary>
    /// Represents the category fields needed to resolve the unique existing asset category target.
    /// </summary>
    private sealed record SnipeCategory(int Id, string Name, string? CategoryType);

}
