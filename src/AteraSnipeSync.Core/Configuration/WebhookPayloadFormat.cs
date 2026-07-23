namespace AteraSnipeSync.Core.Configuration;

/// <summary>
/// Selects the JSON wire shape sent to an operator-configured HTTPS webhook endpoint.
/// </summary>
public enum WebhookPayloadFormat
{
    TeamsAdaptiveCard,
    GenericJson
}
