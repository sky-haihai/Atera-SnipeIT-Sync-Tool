namespace AteraSnipeSync.Core.Notifications;

/// <summary>
/// Reports only the names of failed notification channels so transport secrets and endpoints never cross module boundaries.
/// </summary>
public sealed class NotificationDeliveryException(string message) : Exception(message);
