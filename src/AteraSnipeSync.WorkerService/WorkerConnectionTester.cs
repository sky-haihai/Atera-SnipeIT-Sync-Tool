using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AteraSnipeSync.Core.Atera;
using AteraSnipeSync.Core.Common;
using AteraSnipeSync.Core.Runtime.Ipc;

namespace AteraSnipeSync.WorkerService;

/// <summary>
/// Performs one bounded read-only probe against Atera and Snipe-IT and returns only sanitized per-endpoint outcomes.
/// </summary>
public sealed class WorkerConnectionTester(ILogger<WorkerConnectionTester> logger)
{
    /// <summary>
    /// Tests Atera first and Snipe-IT second under the caller's single run lease; cancellation stops the remaining probe.
    /// </summary>
    public async Task<ConnectionTestResult> TestAllAsync(
        WorkerSyncRuntime runtime,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var ateraResult = await TestAteraAsync(runtime, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snipeResult = await TestSnipeItAsync(runtime, progress, cancellationToken).ConfigureAwait(false);
        return new ConnectionTestResult
        {
            Atera = ateraResult,
            SnipeIt = snipeResult
        };
    }

    private async Task<ConnectionEndpointTestResult> TestAteraAsync(
        WorkerSyncRuntime runtime,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "ConnectionTest.Atera", "Testing Atera connection.", 0, 2);
        try
        {
            await runtime.AteraConnectionClient
                .PullInventoryAsync(runtime.BaseRequest.Atera, cancellationToken)
                .ConfigureAwait(false);
            Report(progress, "ConnectionTest.Atera", "Atera connection succeeded.", 1, 2);
            return Succeeded("Atera connection succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AteraPullException exception)
        {
            logger.LogWarning(
                "Atera connection test failed with {FailureKind}.",
                exception.FailureKind);
            return Failed($"Atera connection failed ({exception.FailureKind}).");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Atera connection test failed.");
            return Failed("Atera connection failed.");
        }
    }

    private async Task<ConnectionEndpointTestResult> TestSnipeItAsync(
        WorkerSyncRuntime runtime,
        IProgress<SyncProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, "ConnectionTest.SnipeIt", "Testing Snipe-IT connection.", 1, 2);
        try
        {
            var baseUri = ApiEndpointValidator.ValidateSnipeBaseUri(runtime.BaseRequest.SnipeIt.BaseUrl);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(baseUri, "hardware?limit=1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                runtime.BaseRequest.SnipeIt.ApiToken);
            request.Headers.UserAgent.ParseAdd("AteraSnipeItAutoSync/1.0");
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await runtime.SnipeItHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Failed("Snipe-IT connection failed (authentication or authorization rejected).");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"Snipe-IT connection failed (HTTP {(int)response.StatusCode}).");
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failed("Snipe-IT connection failed (unexpected response shape).");
            }

            if (document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                return Failed("Snipe-IT connection failed (API reported an error).");
            }

            Report(progress, "ConnectionTest.SnipeIt", "Snipe-IT connection succeeded.", 2, 2);
            return Succeeded("Snipe-IT connection succeeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Snipe-IT connection test returned malformed JSON.");
            return Failed("Snipe-IT connection failed (malformed JSON response).");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Snipe-IT connection test failed.");
            return Failed("Snipe-IT connection failed.");
        }
    }

    private static ConnectionEndpointTestResult Succeeded(string message)
    {
        return new ConnectionEndpointTestResult { Succeeded = true, Message = message };
    }

    private static ConnectionEndpointTestResult Failed(string message)
    {
        return new ConnectionEndpointTestResult { Succeeded = false, Message = message };
    }

    private static void Report(
        IProgress<SyncProgressUpdate>? progress,
        string stage,
        string message,
        int current,
        int total)
    {
        progress?.Report(new SyncProgressUpdate
        {
            Stage = stage,
            Message = message,
            Current = current,
            Total = total
        });
    }
}
