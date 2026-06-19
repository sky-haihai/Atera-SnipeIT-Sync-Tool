using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AteraSnipeSync.Core.Common;
using Microsoft.Extensions.Logging;

namespace AteraSnipeSync.Core.Atera;

/// <summary>
/// Pulls paged Atera agent inventory through the official API and converts wire records into AgentInfo.
/// </summary>
public sealed class AteraClient : IAteraClient
{
    private const int PageSize = 500;
    private const string ApiKeyHeaderName = "X-API-KEY";
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    private readonly HttpClient _httpClient;
    private readonly AteraPullOptions _options;
    private readonly IAteraClock _clock;
    private readonly ILogger<AteraClient> _logger;

    public AteraClient(
        HttpClient httpClient,
        AteraPullOptions options,
        IAteraClock clock,
        ILogger<AteraClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ValidateOptions(options);

        _httpClient = httpClient;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Reads all Atera agent pages, preserving converted records only when the complete pull succeeds.
    /// </summary>
    public async Task<AteraPullResult> PullInventoryAsync(
        AteraPullRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiKey = request.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("Atera API key is required.", nameof(request));
        }

        _logger.LogInformation("Starting Atera inventory pull.");

        try
        {
            var agents = new List<AgentInfo>();
            var warnings = new List<ModuleWarning>();
            var page = 1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var envelope = await SendPageAsync(page, apiKey, cancellationToken).ConfigureAwait(false);
                ConvertPageAgents(envelope.Items!, page, agents, warnings, _logger);

                var totalPages = ResolveTotalPages(envelope, page);
                if (page >= totalPages)
                {
                    break;
                }

                page++;
            }

            var result = new AteraPullResult
            {
                Agents = agents,
                Summary = new PullSummary
                {
                    AgentCount = agents.Count,
                    PulledAt = _clock.UtcNow
                },
                Warnings = warnings
            };

            _logger.LogInformation("Completed Atera inventory pull with {AgentCount} agent(s).", agents.Count);
            return result;
        }
        catch (AteraPullException exception)
        {
            _logger.LogError(
                exception,
                "Atera inventory pull failed with {FailureKind}.",
                exception.FailureKind);
            throw;
        }
    }

    private async Task<AteraAgentsEnvelope> SendPageAsync(
        int page,
        string apiKey,
        CancellationToken cancellationToken)
    {
        Exception? lastTransientError = null;

        for (var attempt = 0; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            try
            {
                using var request = CreateRequest(page, apiKey);
                _logger.LogDebug("Requesting Atera agents page {Page}.", page);

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (IsAuthenticationFailure(response.StatusCode))
                {
                    throw new AteraPullException(
                        AteraPullFailureKind.AuthenticationFailed,
                        $"Atera rejected the API key with HTTP {(int)response.StatusCode}.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryableStatusCode(response.StatusCode))
                    {
                        lastTransientError = new HttpRequestException(
                            $"Atera returned retryable HTTP {(int)response.StatusCode}.");
                        await DelayBeforeRetryAsync(page, attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new AteraPullException(
                        AteraPullFailureKind.NonRetryableHttpFailure,
                        $"Atera agents page {page} failed with HTTP {(int)response.StatusCode}.");
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var envelope = await JsonSerializer.DeserializeAsync<AteraAgentsEnvelope>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

                if (envelope?.Items is null)
                {
                    throw new AteraPullException(
                        AteraPullFailureKind.MalformedResponse,
                        $"Atera agents page {page} did not include an items array.");
                }

                return envelope;
            }
            catch (JsonException exception)
            {
                throw new AteraPullException(
                    AteraPullFailureKind.MalformedResponse,
                    $"Atera agents page {page} could not be parsed.",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                lastTransientError = exception;
                await DelayBeforeRetryAsync(page, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastTransientError = exception;
                await DelayBeforeRetryAsync(page, attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new AteraPullException(
            AteraPullFailureKind.RetryExhausted,
            $"Atera agents page {page} failed after retry attempts were exhausted.",
            lastTransientError);
    }

    private HttpRequestMessage CreateRequest(int page, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildAgentsPageUri(page));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, apiKey);
        return request;
    }

    private Uri BuildAgentsPageUri(int page)
    {
        var baseUri = new Uri(_options.BaseUri.AbsoluteUri.TrimEnd('/') + "/");
        var endpoint = new Uri(baseUri, "agents");
        var query = $"page={page.ToString(CultureInfo.InvariantCulture)}&itemsInPage={PageSize.ToString(CultureInfo.InvariantCulture)}";
        return new Uri($"{endpoint.AbsoluteUri}?{query}");
    }

    private async Task DelayBeforeRetryAsync(
        int page,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (attempt >= _options.MaxRetryAttempts)
        {
            return;
        }

        _logger.LogWarning(
            "Retrying Atera agents page {Page}; retry attempt {RetryAttempt} of {MaxRetryAttempts}.",
            page,
            attempt + 1,
            _options.MaxRetryAttempts);

        await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
    }

    private static void ConvertPageAgents(
        IReadOnlyList<JsonElement> items,
        int page,
        ICollection<AgentInfo> agents,
        ICollection<ModuleWarning> warnings,
        ILogger<AteraClient> logger)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.ValueKind != JsonValueKind.Object)
            {
                var reason = $"page {page}, item {index + 1} is not an object.";
                logger.LogWarning("Skipping malformed Atera agent record: {Reason}", reason);
                warnings.Add(AteraWarningFactory.MalformedAgentRecord(reason));
                continue;
            }

            var agentId = ReadDisplayValue(item, "AgentID");
            var machineName = ReadDisplayValue(item, "MachineName");
            var agentName = ReadDisplayValue(item, "AgentName");
            var systemName = ReadDisplayValue(item, "SystemName");
            var name = FirstNonBlank(machineName, agentName, systemName);

            if (agentId is null || name is null)
            {
                var sourceDescription = $"page {page}, item {index + 1}, MachineID={ReadDisplayValue(item, "MachineID") ?? "<missing>"}, DeviceGuid={ReadDisplayValue(item, "DeviceGuid") ?? "<missing>"}";
                logger.LogWarning("Skipping Atera agent record with missing identity: {SourceDescription}", sourceDescription);
                warnings.Add(AteraWarningFactory.MissingAgentIdentity(sourceDescription));
                continue;
            }

            agents.Add(new AgentInfo
            {
                AgentId = agentId,
                Name = name,
                RawJson = item.GetRawText(),
                MachineId = ReadDisplayValue(item, "MachineID"),
                DeviceGuid = ReadDisplayValue(item, "DeviceGuid"),
                FolderId = ReadDisplayValue(item, "FolderID"),
                FolderName = ReadDisplayValue(item, "FolderName"),
                CustomerId = ReadDisplayValue(item, "CustomerID"),
                CustomerName = ReadDisplayValue(item, "CustomerName"),
                AgentName = agentName,
                SystemName = systemName,
                MachineName = machineName,
                DomainName = ReadDisplayValue(item, "DomainName"),
                CurrentLoggedUsers = ReadDisplayValue(item, "CurrentLoggedUsers"),
                ComputerDescription = ReadDisplayValue(item, "ComputerDescription"),
                Monitored = ReadBoolean(item, "Monitored"),
                AgentVersion = ReadDisplayValue(item, "AgentVersion"),
                Favorite = ReadBoolean(item, "Favorite"),
                ThresholdId = ReadDisplayValue(item, "ThresholdID"),
                MonitoredAgentId = ReadDisplayValue(item, "MonitoredAgentID"),
                Created = ReadDateTimeOffset(item, "Created"),
                Modified = ReadDateTimeOffset(item, "Modified"),
                Online = ReadBoolean(item, "Online"),
                LastSeen = ReadDateTimeOffset(item, "LastSeen"),
                ReportedFromIp = ReadDisplayValue(item, "ReportedFromIP"),
                AppViewUrl = ReadDisplayValue(item, "AppViewUrl"),
                Motherboard = ReadDisplayValue(item, "Motherboard"),
                Processor = ReadDisplayValue(item, "Processor"),
                Memory = ReadInt32(item, "Memory"),
                Display = ReadDisplayValue(item, "Display"),
                Sound = ReadDisplayValue(item, "Sound"),
                ProcessorCoresCount = ReadInt32(item, "ProcessorCoresCount"),
                SystemDrive = ReadDisplayValue(item, "SystemDrive"),
                ProcessorClock = ReadDisplayValue(item, "ProcessorClock"),
                Vendor = ReadDisplayValue(item, "Vendor"),
                VendorSerialNumber = ReadDisplayValue(item, "VendorSerialNumber"),
                VendorBrandModel = ReadDisplayValue(item, "VendorBrandModel"),
                ProductName = ReadDisplayValue(item, "ProductName"),
                BiosManufacturer = ReadDisplayValue(item, "BiosManufacturer"),
                BiosVersion = ReadDisplayValue(item, "BiosVersion"),
                BiosReleaseDate = ReadDateTimeOffset(item, "BiosReleaseDate"),
                MacAddresses = ReadStringArray(item, "MacAddresses"),
                IpAddresses = ReadStringArray(item, "IpAddresses"),
                HardwareDisksJson = ReadRawJson(item, "HardwareDisks"),
                BatteryInfoJson = ReadRawJson(item, "BatteryInfo"),
                OS = ReadDisplayValue(item, "OS"),
                OSType = ReadDisplayValue(item, "OSType"),
                WindowsSerialNumber = ReadDisplayValue(item, "WindowsSerialNumber"),
                Office = ReadDisplayValue(item, "Office"),
                OfficeSP = ReadDisplayValue(item, "OfficeSP"),
                OfficeOEM = ReadBoolean(item, "OfficeOEM"),
                OfficeSerialNumber = ReadDisplayValue(item, "OfficeSerialNumber"),
                OSNum = ReadDouble(item, "OSNum"),
                LastRebootTime = ReadDateTimeOffset(item, "LastRebootTime"),
                OSVersion = ReadDisplayValue(item, "OSVersion"),
                OSBuild = ReadDisplayValue(item, "OSBuild"),
                OfficeFullVersion = ReadDisplayValue(item, "OfficeFullVersion"),
                DeviceType = ReadDisplayValue(item, "DeviceType"),
                LastLoginUser = ReadDisplayValue(item, "LastLoginUser")
            });
        }
    }

    private static int ResolveTotalPages(AteraAgentsEnvelope envelope, int requestedPage)
    {
        if (envelope.Page is not null && envelope.Page != requestedPage)
        {
            throw new AteraPullException(
                AteraPullFailureKind.PaginationStateUnknown,
                $"Atera returned page {envelope.Page} while page {requestedPage} was requested.");
        }

        if (envelope.TotalPages is not null)
        {
            if (envelope.TotalPages < 0)
            {
                throw new AteraPullException(
                    AteraPullFailureKind.MalformedResponse,
                    "Atera returned a negative totalPages value.");
            }

            return envelope.TotalPages.Value;
        }

        if (envelope.TotalItemCount is not null && envelope.ItemsInPage is > 0)
        {
            return (int)Math.Ceiling(envelope.TotalItemCount.Value / (double)envelope.ItemsInPage.Value);
        }

        throw new AteraPullException(
            AteraPullFailureKind.PaginationStateUnknown,
            "Atera response did not include totalPages or enough count metadata to determine pagination state.");
    }

    private static string? ReadDisplayValue(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Normalize(value.GetString()),
            JsonValueKind.Number => Normalize(value.GetRawText()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? ReadBoolean(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt32(out var number) && number == 1 => true,
            JsonValueKind.Number when value.TryGetInt32(out var number) && number == 0 => false,
            _ => null
        };
    }

    private static int? ReadInt32(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static double? ReadDouble(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement item, string propertyName)
    {
        var value = ReadDisplayValue(item, propertyName);
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var child in value.EnumerateArray())
        {
            var displayValue = child.ValueKind switch
            {
                JsonValueKind.String => Normalize(child.GetString()),
                JsonValueKind.Number => Normalize(child.GetRawText()),
                _ => null
            };

            if (displayValue is not null)
            {
                values.Add(displayValue);
            }
        }

        return values;
    }

    private static string? ReadRawJson(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value.GetRawText()
            : null;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.Select(Normalize).FirstOrDefault(value => value is not null);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsAuthenticationFailure(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return RetryableStatusCodes.Contains(statusCode);
    }

    private static void ValidateOptions(AteraPullOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.BaseUri);

        if (!options.BaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Atera base URI must be absolute.", nameof(options));
        }

        if (options.MaxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Max retry attempts cannot be negative.");
        }

        if (options.RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delay cannot be negative.");
        }
    }

    private sealed class AteraAgentsEnvelope
    {
        [JsonPropertyName("items")]
        public List<JsonElement>? Items { get; init; }

        [JsonPropertyName("totalItemCount")]
        public int? TotalItemCount { get; init; }

        [JsonPropertyName("page")]
        public int? Page { get; init; }

        [JsonPropertyName("itemsInPage")]
        public int? ItemsInPage { get; init; }

        [JsonPropertyName("totalPages")]
        public int? TotalPages { get; init; }

        [JsonPropertyName("prevLink")]
        public string? PrevLink { get; init; }

        [JsonPropertyName("nextLink")]
        public string? NextLink { get; init; }
    }
}
