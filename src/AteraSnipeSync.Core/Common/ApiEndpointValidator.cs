namespace AteraSnipeSync.Core.Common;

/// <summary>
/// Enforces the transport and host boundaries that protect API credentials before an HTTP request is created.
/// </summary>
public static class ApiEndpointValidator
{
    private const string OfficialAteraHost = "app.atera.com";

    /// <summary>
    /// Accepts only the official HTTPS Atera API host so an X-API-KEY cannot be redirected to an arbitrary server.
    /// </summary>
    public static Uri ValidateAteraBaseUri(string value)
    {
        var uri = ParseAbsolute(value, "Atera");
        if (!IsHttps(uri) || !string.Equals(uri.Host, OfficialAteraHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Atera base URL must use HTTPS on {OfficialAteraHost}.", nameof(value));
        }

        return EnsureTrailingSlash(uri);
    }

    /// <summary>
    /// Requires a Snipe-IT API v1 URL over HTTPS; HTTP is accepted only for loopback development endpoints.
    /// </summary>
    public static Uri ValidateSnipeBaseUri(string value)
    {
        var uri = ParseAbsolute(value, "Snipe-IT");
        if (!IsHttps(uri) && !uri.IsLoopback)
        {
            throw new ArgumentException("Snipe-IT base URL must use HTTPS (HTTP is allowed only for loopback development).", nameof(value));
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Snipe-IT base URL must end with /api/v1.", nameof(value));
        }

        return EnsureTrailingSlash(uri);
    }

    private static Uri ParseAbsolute(string value, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException($"{serviceName} base URL must be an absolute URL.", nameof(value));
        }

        return uri;
    }

    private static bool IsHttps(Uri uri) => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }
}
