using System.Net;
using System.Text.Json;
using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.Notifications;

/// <summary>
/// Verifies SMTP composition, HTTPS webhook payloads, and multi-channel delivery without contacting external systems.
/// </summary>
public sealed class NotificationDeliveryTests
{
    [Fact]
    public async Task EmailSender_ComposesValidatedEnvelope_AndSendsOnce()
    {
        var transport = new CapturingSmtpTransport();
        var sender = new EmailNotificationSender(transport);
        var config = EmailConfig(emailTo: "one@example.test; two@example.test");

        await sender.SendAsync(config, Request(), CancellationToken.None);

        var envelope = Assert.Single(transport.Envelopes);
        Assert.Equal("smtp.example.test", envelope.Host);
        Assert.Equal(587, envelope.Port);
        Assert.True(envelope.UseSsl);
        Assert.Equal("mailer", envelope.Username);
        Assert.Equal("smtp-secret", envelope.Password);
        Assert.Equal("sync@example.test", envelope.From);
        Assert.Equal(["one@example.test", "two@example.test"], envelope.Recipients);
        Assert.Equal("Test subject", envelope.Subject);
        Assert.Equal("Test message", envelope.Body);
    }

    [Fact]
    public async Task EmailSender_RejectsPartialCredentials_BeforeTransport()
    {
        var transport = new CapturingSmtpTransport();
        var sender = new EmailNotificationSender(transport);
        var config = new NotificationConfig
        {
            Enabled = true,
            OnEvents = [NotificationEventTypes.SyncCompleted],
            SmtpHost = "smtp.example.test",
            SmtpUsername = "mailer",
            EmailFrom = "sync@example.test",
            EmailTo = "operator@example.test"
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => sender.SendAsync(config, Request(), CancellationToken.None));
        Assert.Empty(transport.Envelopes);
    }

    [Fact]
    public async Task WebhookSender_PostsGenericSafeJson_WithoutConfigurationSecrets()
    {
        var handler = new CapturingHttpHandler(HttpStatusCode.Accepted, "ignored-response-secret");
        var sender = new WebhookNotificationSender(
            new HttpClient(handler),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)));
        var config = new NotificationConfig
        {
            Enabled = true,
            OnEvents = [NotificationEventTypes.SyncCompleted],
            WebhookPayloadFormat = WebhookPayloadFormat.GenericJson,
            WebhookUrl = "https://hooks.example.test/notify",
            SmtpPassword = "must-not-appear"
        };

        await sender.SendAsync(config, Request(), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("application/json", handler.ContentType);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("SyncCompleted", json.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("Test subject", json.RootElement.GetProperty("subject").GetString());
        Assert.Equal("Test message", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("deleted").ValueKind);
        Assert.Equal(3, json.RootElement.GetProperty("deleted").GetInt32());
        Assert.DoesNotContain("must-not-appear", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks.example.test", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebhookSender_PostsTeamsWorkflowAdaptiveCardEnvelope()
    {
        var handler = new CapturingHttpHandler(HttpStatusCode.Accepted, string.Empty);
        var sender = new WebhookNotificationSender(
            new HttpClient(handler),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)));

        await sender.SendAsync(
            WebhookConfig("https://example.logic.azure.com/workflows/test"),
            Request(),
            CancellationToken.None);

        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        var attachment = Assert.Single(root.GetProperty("attachments").EnumerateArray());
        Assert.Equal(
            "application/vnd.microsoft.card.adaptive",
            attachment.GetProperty("contentType").GetString());
        Assert.Equal(JsonValueKind.Null, attachment.GetProperty("contentUrl").ValueKind);

        var card = attachment.GetProperty("content");
        Assert.Equal("http://adaptivecards.io/schemas/adaptive-card.json", card.GetProperty("$schema").GetString());
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("1.2", card.GetProperty("version").GetString());
        var body = card.GetProperty("body").EnumerateArray().ToArray();
        Assert.Equal(3, body.Length);
        Assert.Equal("Test subject", body[0].GetProperty("text").GetString());
        Assert.True(body[0].GetProperty("wrap").GetBoolean());
        Assert.Equal("FactSet", body[1].GetProperty("type").GetString());
        var facts = body[1].GetProperty("facts").EnumerateArray().ToArray();
        Assert.Contains(facts, fact => fact.GetProperty("value").GetString() == "Information");
        Assert.Contains(facts, fact => fact.GetProperty("value").GetString() == "SyncCompleted");
        Assert.Contains(facts, fact => fact.GetProperty("value").GetString() == "2026-07-21 12:00:00 UTC");
        Assert.Equal("Test message", body[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task WebhookSender_NonSuccess_ThrowsStatusOnlyWithoutResponseBody()
    {
        var handler = new CapturingHttpHandler(HttpStatusCode.BadRequest, "raw-response-secret");
        var sender = new WebhookNotificationSender(new HttpClient(handler), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => sender.SendAsync(
                WebhookConfig("https://hooks.example.test/notify"),
                Request(),
                CancellationToken.None));

        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-response-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks.example.test", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebhookSender_RejectsNonHttpsUrl_BeforeHttpSend()
    {
        var handler = new CapturingHttpHandler(HttpStatusCode.OK, string.Empty);
        var sender = new WebhookNotificationSender(new HttpClient(handler), TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sender.SendAsync(
                WebhookConfig("http://hooks.example.test/notify"),
                Request(),
                CancellationToken.None));
        Assert.Null(handler.Method);
    }

    [Fact]
    public async Task CompositePublisher_PartialFailure_StillAttemptsBoth_AndThrowsSanitizedSummary()
    {
        var email = new FakeChannelSender("Email", configured: true, failureMessage: "smtp-secret-host");
        var webhook = new FakeChannelSender("Webhook", configured: true);
        var publisher = Publisher(email, webhook);

        var exception = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => publisher.PublishAsync(Request(), Config(), CancellationToken.None));

        Assert.Equal(1, email.SendCount);
        Assert.Equal(1, webhook.SendCount);
        Assert.Contains("Email", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-secret-host", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompositePublisher_TestAsync_ReturnsIndependentChannelOutcomes()
    {
        var email = new FakeChannelSender("Email", configured: true, failureMessage: "private failure");
        var webhook = new FakeChannelSender("Webhook", configured: false);
        var publisher = Publisher(email, webhook);

        var result = await publisher.TestAsync(Config(), CancellationToken.None);

        Assert.True(result.Email.Configured);
        Assert.False(result.Email.Succeeded);
        Assert.DoesNotContain("private failure", result.Email.Message, StringComparison.Ordinal);
        Assert.False(result.Webhook.Configured);
        Assert.False(result.Webhook.Succeeded);
        Assert.Equal(1, email.SendCount);
        Assert.Equal(0, webhook.SendCount);
    }

    [Fact]
    public async Task CompositePublisher_TestAsync_ReportsEndpointAcceptanceWithoutClaimingDelivery()
    {
        var publisher = Publisher(
            new FakeChannelSender("Email", configured: true),
            new FakeChannelSender("Webhook", configured: true));

        var result = await publisher.TestAsync(Config(), CancellationToken.None);

        Assert.True(result.Email.Succeeded);
        Assert.Contains("accepted", result.Email.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not confirmed", result.Email.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Webhook.Succeeded);
        Assert.Contains("accepted", result.Webhook.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not confirmed", result.Webhook.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompositePublisher_PublishWithoutConfiguredChannels_FailsClosed()
    {
        var publisher = Publisher(
            new FakeChannelSender("Email", configured: false),
            new FakeChannelSender("Webhook", configured: false));

        await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => publisher.PublishAsync(Request(), Config(), CancellationToken.None));
    }

    private static CompositeNotificationPublisher Publisher(params INotificationChannelSender[] senders)
        => new(senders, TimeProvider.System, NullLogger<CompositeNotificationPublisher>.Instance);

    private static NotificationRequest Request() => new()
    {
        EventType = NotificationEventTypes.SyncCompleted,
        Severity = "Information",
        Subject = "Test subject",
        Message = "Test message",
        Deleted = 3,
        SyncResult = null
    };

    private static NotificationConfig Config() => new()
    {
        Enabled = true,
        OnEvents = [NotificationEventTypes.SyncCompleted]
    };

    private static NotificationConfig EmailConfig(string emailTo) => new()
    {
        Enabled = true,
        OnEvents = [NotificationEventTypes.SyncCompleted],
        SmtpHost = "smtp.example.test",
        SmtpPort = 587,
        SmtpUseSsl = true,
        SmtpUsername = "mailer",
        SmtpPassword = "smtp-secret",
        EmailFrom = "sync@example.test",
        EmailTo = emailTo
    };

    private static NotificationConfig WebhookConfig(string url) => new()
    {
        Enabled = true,
        OnEvents = [NotificationEventTypes.SyncCompleted],
        WebhookUrl = url
    };

    private sealed class CapturingSmtpTransport : ISmtpNotificationTransport
    {
        public List<SmtpNotificationEnvelope> Envelopes { get; } = [];

        public Task SendAsync(SmtpNotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            Envelopes.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHttpHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody) };
        }
    }

    private sealed class FakeChannelSender(
        string channelName,
        bool configured,
        string? failureMessage = null) : INotificationChannelSender
    {
        public string ChannelName => channelName;
        public int SendCount { get; private set; }

        public bool IsConfigured(NotificationConfig config) => configured;

        public Task SendAsync(
            NotificationConfig config,
            NotificationRequest request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return failureMessage is null
                ? Task.CompletedTask
                : throw new InvalidOperationException(failureMessage);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
