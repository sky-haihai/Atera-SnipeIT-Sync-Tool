using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;

namespace AteraSnipeSync.Tests.Notifications;

/// <summary>
/// Verifies notification configuration filtering without invoking any publisher implementation.
/// </summary>
public sealed class NotificationEventFilterTests
{
    [Fact]
    public void ShouldPublish_ReturnsFalse_WhenNotificationsDisabled()
    {
        var filter = new NotificationEventFilter();
        var config = CreateConfig(enabled: false, [NotificationEventTypes.SyncCompleted]);

        var shouldPublish = filter.ShouldPublish(config, CreateRequest(NotificationEventTypes.SyncCompleted));

        Assert.False(shouldPublish);
    }

    [Fact]
    public void ShouldPublish_ReturnsFalse_WhenOnEventsEmpty()
    {
        var filter = new NotificationEventFilter();
        var config = CreateConfig(enabled: true, []);

        var shouldPublish = filter.ShouldPublish(config, CreateRequest(NotificationEventTypes.SyncCompleted));

        Assert.False(shouldPublish);
    }

    [Fact]
    public void ShouldPublish_ReturnsTrue_WhenEventConfiguredCaseInsensitive()
    {
        var filter = new NotificationEventFilter();
        var config = CreateConfig(enabled: true, ["  scheduledsynccompleted  "]);

        var shouldPublish = filter.ShouldPublish(config, CreateRequest(NotificationEventTypes.ScheduledSyncCompleted));

        Assert.True(shouldPublish);
    }

    [Fact]
    public void ShouldPublish_ReturnsFalse_WhenEventNotConfigured()
    {
        var filter = new NotificationEventFilter();
        var config = CreateConfig(enabled: true, [NotificationEventTypes.SyncFailed]);

        var shouldPublish = filter.ShouldPublish(config, CreateRequest(NotificationEventTypes.SyncCompleted));

        Assert.False(shouldPublish);
    }

    [Fact]
    public void ShouldPublish_IgnoresBlankConfiguredEvents()
    {
        var filter = new NotificationEventFilter();
        var config = CreateConfig(enabled: true, ["", " ", NotificationEventTypes.ManualSyncFailed]);

        var shouldPublish = filter.ShouldPublish(config, CreateRequest(NotificationEventTypes.ManualSyncFailed));

        Assert.True(shouldPublish);
    }

    [Fact]
    public void ShouldPublish_ThrowsArgumentNullException_ForNullConfig()
    {
        var filter = new NotificationEventFilter();

        Assert.Throws<ArgumentNullException>(
            () => filter.ShouldPublish(null!, CreateRequest(NotificationEventTypes.SyncCompleted)));
    }

    [Fact]
    public void ShouldPublish_ThrowsArgumentNullException_ForNullRequest()
    {
        var filter = new NotificationEventFilter();

        Assert.Throws<ArgumentNullException>(
            () => filter.ShouldPublish(CreateConfig(enabled: true, [NotificationEventTypes.SyncCompleted]), null!));
    }

    private static NotificationConfig CreateConfig(
        bool enabled,
        IReadOnlyList<string> onEvents)
    {
        return new NotificationConfig
        {
            Enabled = enabled,
            OnEvents = onEvents
        };
    }

    private static NotificationRequest CreateRequest(string eventType)
    {
        return new NotificationRequest
        {
            EventType = eventType,
            Severity = "Information",
            Subject = "Sync completed",
            Message = "Sync completed.",
            SyncResult = null
        };
    }
}
