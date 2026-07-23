using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace AteraSnipeSync.Tests.Notifications;

/// <summary>
/// Verifies the safe default notification publisher validates requests without external sends.
/// </summary>
public sealed class NullNotificationPublisherTests
{
    [Fact]
    public async Task PublishAsync_CompletesWithoutExternalCalls()
    {
        var publisher = CreatePublisher();

        await publisher.PublishAsync(CreateRequest(), Config(), CancellationToken.None);
    }

    [Fact]
    public async Task PublishAsync_ThrowsArgumentNullException_WhenRequestNull()
    {
        var publisher = CreatePublisher();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => publisher.PublishAsync(null!, Config(), CancellationToken.None));
    }

    [Theory]
    [InlineData("", "Information", "Sync completed", "Sync completed.")]
    [InlineData("SyncCompleted", "", "Sync completed", "Sync completed.")]
    [InlineData("SyncCompleted", "Information", "", "Sync completed.")]
    [InlineData("SyncCompleted", "Information", "Sync completed", "")]
    public async Task PublishAsync_ThrowsArgumentException_WhenRequestFieldsBlank(
        string eventType,
        string severity,
        string subject,
        string message)
    {
        var publisher = CreatePublisher();
        var request = CreateRequest(eventType, severity, subject, message);

        await Assert.ThrowsAsync<ArgumentException>(
            () => publisher.PublishAsync(request, Config(), CancellationToken.None));
    }

    [Fact]
    public async Task PublishAsync_HonorsCancellation()
    {
        var publisher = CreatePublisher();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => publisher.PublishAsync(CreateRequest(), Config(), cts.Token));
    }

    private static NullNotificationPublisher CreatePublisher()
    {
        return new NullNotificationPublisher(NullLogger<NullNotificationPublisher>.Instance);
    }

    private static NotificationConfig Config()
        => new() { Enabled = false, OnEvents = [] };

    private static NotificationRequest CreateRequest(
        string eventType = NotificationEventTypes.SyncCompleted,
        string severity = "Information",
        string subject = "Sync completed",
        string message = "Sync completed.")
    {
        return new NotificationRequest
        {
            EventType = eventType,
            Severity = severity,
            Subject = subject,
            Message = message,
            Deleted = 0,
            SyncResult = null
        };
    }
}
