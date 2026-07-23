using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AteraSnipeSync.Core.Common;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Owns Snipe-IT HTTP request creation, read retry policy, response parsing, and safe transport-error classification.
/// Business-level success envelopes remain the importer's responsibility because their meaning depends on the operation.
/// </summary>
internal sealed class SnipeApiClient
{
    private const int MaximumErrorDetailLength = 2000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public SnipeApiClient(HttpClient httpClient, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Sends one API request, retries only idempotent GET operations, and returns a parsed JSON document.
    /// Write requests intentionally ignore caller cancellation after dispatch so an unknown mutation outcome is not retried.
    /// </summary>
    public async Task<JsonDocument> SendJsonAsync(
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

    private static HttpRequestMessage CreateRequest(
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

    private static string? FormatErrorDetails(JsonElement element)
    {
        var details = new List<string>();
        AppendErrorDetails(element, fieldPath: null, details);
        var combined = string.Join(
            "; ",
            details.Where(detail => !string.IsNullOrWhiteSpace(detail)).Distinct(StringComparer.Ordinal));
        if (combined.Length == 0)
        {
            return null;
        }

        return combined.Length <= MaximumErrorDetailLength
            ? combined
            : string.Concat(combined.AsSpan(0, MaximumErrorDetailLength), "...");
    }

    private static void AppendErrorDetails(JsonElement element, string? fieldPath, List<string> details)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = fieldPath is null ? property.Name : $"{fieldPath}.{property.Name}";
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

    private static void AddErrorDetail(string? value, string? fieldPath, List<string> details)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        details.Add(fieldPath is null ? normalized : $"{fieldPath}: {normalized}");
    }

    private static string DescribeRequest(string operation, HttpMethod method, string relativePath)
    {
        return $"{operation} via {method.Method} /{relativePath.TrimStart('/')}";
    }
}
