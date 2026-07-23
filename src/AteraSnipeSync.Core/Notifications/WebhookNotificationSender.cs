using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json.Nodes;
using AteraSnipeSync.Core.Configuration;

namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Sends either a Teams Adaptive Card envelope or generic notification JSON to a configured HTTPS webhook.
/// </summary>
public sealed class WebhookNotificationSender(
    HttpClient httpClient,
    TimeProvider timeProvider) : INotificationChannelSender
{
    public string ChannelName => "Webhook";

    public bool IsConfigured(NotificationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !string.IsNullOrWhiteSpace(config.WebhookUrl);
    }

    /// <summary>
    /// Validates HTTPS, posts the selected safe JSON shape, and treats 2xx only as endpoint acceptance.
    /// </summary>
    public async Task SendAsync(
        NotificationConfig config,
        NotificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = ValidateEndpoint(config.WebhookUrl);
        var occurredAtUtc = timeProvider.GetUtcNow();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(CreatePayload(config.WebhookPayloadFormat, request, occurredAtUtc))
        };
        using var response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotificationDeliveryException(
                $"Webhook returned HTTP {(int)response.StatusCode}.");
        }
    }

    /// <summary>
    /// Builds the exact configured webhook contract without including endpoint or configuration secrets.
    /// </summary>
    private static JsonObject CreatePayload(
        WebhookPayloadFormat format,
        NotificationRequest request,
        DateTimeOffset occurredAtUtc)
    {
        return format switch
        {
            WebhookPayloadFormat.TeamsAdaptiveCard => CreateTeamsAdaptiveCardPayload(request, occurredAtUtc),
            WebhookPayloadFormat.GenericJson => CreateGenericPayload(request, occurredAtUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported webhook payload format.")
        };
    }

    /// <summary>
    /// Creates the Teams incoming-workflow message envelope containing one Adaptive Card attachment.
    /// </summary>
    private static JsonObject CreateTeamsAdaptiveCardPayload(
        NotificationRequest request,
        DateTimeOffset occurredAtUtc)
    {
        var facts = new JsonArray
        {
            CreateFact("Severity", request.Severity),
            CreateFact("Event", request.EventType),
            CreateFact(
                "Occurred",
                occurredAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))
        };
        var body = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["size"] = "Medium",
                ["weight"] = "Bolder",
                ["wrap"] = true,
                ["text"] = request.Subject
            },
            new JsonObject
            {
                ["type"] = "FactSet",
                ["facts"] = facts
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["wrap"] = true,
                ["text"] = request.Message
            }
        };

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["contentUrl"] = null,
                    ["content"] = new JsonObject
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.2",
                        ["body"] = body
                    }
                }
            }
        };
    }

    private static JsonObject CreateFact(string title, string value) => new()
    {
        ["title"] = title,
        ["value"] = value
    };

    private static JsonObject CreateGenericPayload(
        NotificationRequest request,
        DateTimeOffset occurredAtUtc) => new()
    {
        ["eventType"] = request.EventType,
        ["severity"] = request.Severity,
        ["subject"] = request.Subject,
        ["message"] = request.Message,
        ["deleted"] = request.Deleted,
        ["occurredAtUtc"] = occurredAtUtc
    };

    private static Uri ValidateEndpoint(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException("WebhookUrl must be an absolute HTTPS URL.", nameof(value));
        }

        return endpoint;
    }
}
